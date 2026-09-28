using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Operations;
using ScadaDarbox.Gateway.RealTime;
using ScadaDarbox.Gateway.Scanning;
using ScadaDarbox.Gateway.Security;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.Mqtt;
using ScadaDarbox.Modules.Drivers.OpcUa;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway;

/// <summary>
/// Composes the Gateway: startup checks, services and endpoints.
/// </summary>
/// <remarks>
/// Separate from Program so a test can start the real host — the same composition and the
/// same startup checks — against its own database and with its own stand-in devices.
/// </remarks>
public static class GatewayApp
{
    /// <summary>The application role's database password, when the connection string carries none.</summary>
    public const string AppPasswordVariable = "SCADA_APP_DB_PASSWORD";

    /// <param name="args">Command-line arguments, as given to the process.</param>
    /// <param name="configure">
    /// Runs after every default registration and before the host is built, so a test can
    /// replace a service — the device drivers, say.
    /// </param>
    /// <exception cref="SchemaVersionMismatchException">The database is not at this build's schema.</exception>
    /// <exception cref="UnsafeDatabaseRoleException">The connection could rewrite the audit trail.</exception>
    /// <exception cref="DatabaseLoginRefusedException">The database turned down the application role's login.</exception>
    /// <exception cref="NoMigrationScriptsException">This build carries no migration scripts to check against.</exception>
    public static async Task<WebApplication> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var startup = CancellationToken.None;

        // Before anything else is registered or can log a request (ADR-0011).
        builder.Services.ScrubQueryTokens();

        var dataSource = NpgsqlDataSource.Create(ApplicationConnectionString(builder.Configuration));

        // This process never migrates (ADR-0012). It refuses to start against any schema but
        // the one it was built for, and over a connection that could rewrite the audit trail
        // — both before it touches a single table. These are also its first connection, so a
        // login the database turns down is reported here too, as a refusal and not a crash.
        try
        {
            await SchemaVersion.EnsureCurrentAsync(dataSource, startup);
            await SchemaVersion.EnsureUnprivilegedAsync(dataSource, startup);
        }
        catch (PostgresException exception) when (DatabaseLoginRefusedException.IsLoginRefusal(exception))
        {
            throw new DatabaseLoginRefusedException(
                new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Username ?? "(none)",
                exception);
        }

        // The database is prepared before the host is built so that the tag catalogue is a
        // fully-formed value by the time anything can resolve it, rather than a
        // half-populated singleton that fills in later.
        await DemoConfigurationSeeder.SeedIfEmptyAsync(
            dataSource,
            builder.Configuration.GetValue("Modbus:Host", "127.0.0.1")!,
            builder.Configuration.GetValue("Modbus:Port", 5502),
            startup);

        var configurationStore = new PostgresConfigurationStore(dataSource);
        var catalogSource = new TagCatalogSource(await ConfigurationReloader.BuildAsync(configurationStore, startup));

        var securityStore = new SecurityStore(dataSource);
        var initialAdmin = await InitialAdmin.EnsureAsync(
            args, securityStore, securityStore, catalogSource.Current.Tenant.Id, startup);
        var userDirectorySource = new UserDirectorySource(await AccessReloader.BuildAsync(securityStore, startup));

        RegisterCoreServices(builder, dataSource, configurationStore, catalogSource);
        RegisterSecurity(builder, securityStore, userDirectorySource);

        builder.Services.AddSignalR();

        // ASP.NET Core creates a Data Protection key ring at startup whether or not anything
        // uses it — nothing here does: sessions are opaque server-side tokens (ADR-0011). Left
        // inside a container, the ring is lost with it and a warning says so on every start. A
        // deployment names a directory on a volume; a developer's machine keeps the default.
        if (builder.Configuration["DataProtection:KeysDirectory"] is { Length: > 0 } keysDirectory)
        {
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        }

        configure?.Invoke(builder);

        var app = builder.Build();
        ReportInitialAdmin(app.Logger, initialAdmin, userDirectorySource.Current);

        // The web client is served from here, so the browser talks to one origin and no
        // cross-origin allowance exists at all (Phase 6). Its files are public — the sign-in
        // screen has to load before anyone has a session — so they are served ahead of
        // authentication; everything under /api and /hubs is still behind it. Without a
        // built client in the web root (a developer running `ng serve`) this serves nothing.
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // index.html names the hashed bundles of *this* build. Without an instruction a
            // browser may reuse a cached copy for hours after an upgrade and keep running the
            // previous client against the new Gateway; no-cache makes it ask every time (a
            // 304 when nothing changed). The bundles themselves are named by their content.
            OnPrepareResponse = context =>
            {
                if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                }
            },
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        MapTagReads(app);
        app.MapAuthApi();
        app.MapUserApi();
        app.MapConfigurationApi();
        app.MapAlarmApi();
        app.MapTemplateApi();
        app.MapTagWriteApi();

        app.MapHub<TagHub>(HubQueryToken.HubPath);

        return app;
    }

    private static void RegisterCoreServices(
        WebApplicationBuilder builder,
        NpgsqlDataSource dataSource,
        PostgresConfigurationStore configurationStore,
        TagCatalogSource catalogSource)
    {
        var services = builder.Services;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(dataSource);
        services.AddSingleton(catalogSource);
        services.AddSingleton<IConfigurationStore>(configurationStore);
        services.AddSingleton<ConfigurationReloader>();
        services.AddSingleton<IFolderRepository, FolderRepository>();
        services.AddSingleton<IAlarmDefinitionRepository, AlarmDefinitionRepository>();
        services.AddSingleton<IDeviceTemplateRepository, DeviceTemplateRepository>();
        services.AddSingleton<IDeviceRepository, DeviceRepository>();
        services.AddSingleton<IEdgeRepository, EdgeRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<IHistorian, TimescaleHistorian>();
        services.AddSingleton<ITagValueSubscriber, SignalRTagBroadcaster>();
        services.AddSingleton<ITagEngine, TagEngine>();

        // One AlarmEngine wearing two hats: it is fed by the tag engine's fan-out and read by
        // the API, so it must be the same instance in both roles rather than two that
        // disagree about which alarms are standing.
        var alarms = builder.Configuration.GetSection("Alarms");
        services.AddSingleton(new AlarmEngineOptions
        {
            MaxShelveDuration = alarms.GetValue("MaxShelveDuration", TimeSpan.FromHours(24)),
        });
        services.AddSingleton(new ShelveSweepSettings(alarms.GetValue("ShelveSweepInterval", TimeSpan.FromSeconds(30))));
        services.AddSingleton<IAlarmJournal, AlarmJournal>();
        services.AddSingleton<IAlarmSubscriber, SignalRAlarmBroadcaster>();
        services.AddSingleton<AlarmEngine>();
        services.AddSingleton<IAlarmEngine>(provider => provider.GetRequiredService<AlarmEngine>());
        services.AddSingleton<ITagValueSubscriber>(provider => provider.GetRequiredService<AlarmEngine>());

        // Compile-time composition of driver modules (ADR-0002): each is referenced as a
        // project and registered here by hand. Nothing is scanned for or loaded dynamically.
        // Each is given the host's logger factory, so a driver can say why a tag has no value
        // — the reason a Bad reading cannot carry on its own.
        services.AddSingleton<IDeviceDriverFactory>(provider => new ModbusTcpDriverFactory(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IDeviceDriverFactory>(provider => new OpcUaDriverFactory(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IPushingDeviceDriverFactory>(provider =>
            new MqttPushingDriverFactory(provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<TagWriter>();

        // Before the scanner: hosted services start in registration order and stop in
        // reverse, so the alarm list is rebuilt before the first value arrives and
        // EvaluationStopped is written after the last (ADR-0013).
        services.AddHostedService<AlarmEngineLifecycle>();
        services.AddHostedService<ShelveExpirySweeper>();
        services.AddSingleton<DriverShapes>();

        // How far a pushing source's clock may disagree with this one before the journal says
        // so (ADR-0017). Transit time is inside the comparison, so this is not a precision.
        services.AddSingleton(new PushedSourceSettings(
            builder.Configuration.GetValue("PushedSources:ClockSkewTolerance", PushedSourceSettings.DefaultClockSkewTolerance)));
        services.AddHostedService<DeviceScannerService>();
    }

    private static void RegisterSecurity(
        WebApplicationBuilder builder,
        SecurityStore securityStore,
        UserDirectorySource userDirectorySource)
    {
        var services = builder.Services;
        var sessions = builder.Configuration.GetSection("Sessions");

        services.AddSingleton(new SessionPolicy
        {
            IdleTimeout = sessions.GetValue("IdleTimeout", TimeSpan.FromHours(12)),
            AbsoluteLifetime = sessions.GetValue("AbsoluteLifetime", TimeSpan.FromDays(7)),
        });
        services.AddSingleton(new HubSweepSettings(sessions.GetValue("HubSweepInterval", TimeSpan.FromSeconds(30))));

        services.AddSingleton(securityStore);
        services.AddSingleton<ISecurityStore>(securityStore);
        services.AddSingleton<IAuditLog>(securityStore);
        services.AddSingleton(userDirectorySource);
        services.AddSingleton<SessionManager>();
        services.AddSingleton<Authenticator>();

        // The registry and the reloader are the one path by which access changes: the
        // reloader swaps the directory and then waits for the registry to move every open
        // connection, so a revocation reaches live connections in the same step as it
        // reaches the next request (ADR-0011).
        services.AddSingleton<HubConnectionRegistry>();
        services.AddSingleton<AccessReloader>();
        services.AddHostedService<HubSessionSweeper>();

        services
            .AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationHandler.SchemeName,
                configureOptions: null);
        services.AddAuthorization(options => options.AddScadaPolicies());
        services.AddSingleton<IAuthorizationHandler, AdminRequirementHandler>();
    }

    private static void MapTagReads(WebApplication app)
    {
        app.MapGet("/api/tags", (ITagEngine engine, TagCatalogSource catalogSource, Caller caller) =>
        {
            var tagCatalog = catalogSource.Current;

            // Tags with no reading yet are still listed, so the client shows the configured
            // hierarchy rather than an empty page until the first scan completes.
            var current = engine.GetAllCurrent().ToDictionary(s => s.TagId);

            var tags = tagCatalog.Tags
                .Where(tag => caller.Access.CanSeeTag(tagCatalog, tag.Id))
                .OrderBy(tag => tagCatalog.PathOf(tag.Id), StringComparer.OrdinalIgnoreCase)
                .Select(tag => current.TryGetValue(tag.Id, out var snapshot)
                    ? TagSnapshotDto.From(snapshot)
                    : new TagSnapshotDto(
                        tag.Id,
                        tagCatalog.PathOf(tag.Id),
                        TagValueDto.None,
                        // Nothing measured yet, so no measurement time: not year 0001 standing in for one.
                        SourceTimestampUtc: null,
                        "Bad",
                        tag.Unit?.Symbol,
                        NoDataSinceUtc: null));

            return Results.Ok(tags);
        });

        app.MapGet("/api/tags/{tagId:guid}", (Guid tagId, ITagEngine engine, TagCatalogSource catalogSource, Caller caller) =>
        {
            // Not found for a tag in a Site the caller cannot see — the same answer as for
            // one that does not exist, so the response does not confirm it is there.
            if (!caller.Access.CanSeeTag(catalogSource.Current, tagId))
            {
                return Results.NotFound();
            }

            var snapshot = engine.GetCurrent(tagId);
            return snapshot is null ? Results.NotFound() : Results.Ok(TagSnapshotDto.From(snapshot));
        });

        app.MapGet("/api/tags/{tagId:guid}/history", async (
            Guid tagId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            IHistorian historian,
            ITagRepository tags,
            TimeProvider timeProvider,
            Caller caller,
            CancellationToken cancellationToken) =>
        {
            // Resolved past any deletion, for two reasons: history for a device retired last
            // year still reads as its name (ADR-0001, ADR-0009), and its Site is still known.
            // The live catalogue would not know a retired tag's Site, and "not in the
            // catalogue" must not turn into "visible to everyone" (ADR-0011).
            var identity = await tags.FindIdentityIncludingDeletedAsync(tagId, cancellationToken);
            var permitted = identity is null ? caller.Access.CanViewUnscoped : caller.Access.CanView(identity.SiteId);

            if (!permitted)
            {
                return Results.NotFound();
            }

            var now = timeProvider.GetUtcNow();
            var samples = await historian.ReadAsync(
                tagId,
                from ?? now.AddMinutes(-15),
                to ?? now,
                cancellationToken);

            return Results.Ok(new TagHistoryDto(
                tagId,
                identity?.TagName,
                identity?.DeviceName,
                identity?.IsDeleted ?? false,
                samples.Select(sample => new HistorySampleDto(
                    TagValueDto.From(sample.Value),
                    sample.SourceTimestampUtc,
                    sample.IngestedAtUtc,
                    sample.Quality.ToString())).ToList()));
        });
    }

    /// <summary>
    /// The application role's connection string. Its password is not in appsettings.json,
    /// which is committed: it comes from the environment (ADR-0011).
    /// </summary>
    private static string ApplicationConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("ScadaDb")
            ?? throw new InvalidOperationException("Connection string 'ScadaDb' is not configured.");

        var connection = new NpgsqlConnectionStringBuilder(configured);
        if (string.IsNullOrEmpty(connection.Password))
        {
            connection.Password = Environment.GetEnvironmentVariable(AppPasswordVariable)
                ?? throw new InvalidOperationException(
                    $"No database password: set {AppPasswordVariable} to the password the migrator gave " +
                    $"the application role '{ApplicationRole.Name}'.");
        }

        return connection.ConnectionString;
    }

    private static void ReportInitialAdmin(ILogger logger, InitialAdmin.Outcome outcome, UserDirectory users)
    {
        switch (outcome)
        {
            case InitialAdmin.Outcome.Created:
                logger.LogInformation("Created the initial Admin account.");
                break;

            case InitialAdmin.Outcome.IgnoredBecauseUsersExist:
                logger.LogWarning(
                    "An initial Admin was supplied, but users already exist, so it was ignored and no account " +
                    "was created. Remove it from the command line or environment.");
                break;

            case InitialAdmin.Outcome.NotRequested when users.Users.Count == 0:
                logger.LogWarning(
                    "No users exist, so nobody can log in. Start the Gateway once with {UsernameOption} and " +
                    "{PasswordOption}, or the {UsernameVariable} and {PasswordVariable} environment variables.",
                    InitialAdmin.UsernameOption,
                    InitialAdmin.PasswordOption,
                    InitialAdmin.UsernameVariable,
                    InitialAdmin.PasswordVariable);
                break;
        }
    }
}
