using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// The real Gateway — the same composition and startup checks as production — running on a
/// loopback port against a scratch database, with stand-in drivers in place of devices.
/// </summary>
public sealed class GatewayTestHost : IAsyncLifetime
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "correct horse battery staple";
    public const string UserPassword = "a long enough passphrase";

    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);

    private ScratchDatabase? _database;
    private WebApplication? _app;

    /// <summary>Serves the seeded demo device and every device a test creates by default.</summary>
    public FakeDriverFactory Drivers { get; } = new(FakeDriverFactory.Key);

    /// <summary>A second protocol, so a test can show a write reaching the right one.</summary>
    public FakeDriverFactory OtherDrivers { get; } = new(FakeDriverFactory.OtherKey);

    /// <summary>Everything the Gateway logged, at every level.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public Uri BaseAddress { get; private set; } = null!;

    public IServiceProvider Services => _app!.Services;

    public async Task InitializeAsync()
    {
        if (!ScratchDatabase.IsAvailable)
        {
            return;
        }

        _database = await ScratchDatabase.CreateMigratedAsync();
        await StartAppAsync();
    }

    /// <summary>The database this host's Gateway runs against.</summary>
    public ScratchDatabase Database => _database!;

    /// <summary>
    /// Stops the Gateway cleanly and starts a new one on the same database, as a restart
    /// would: nothing held in memory survives, only what was persisted.
    /// </summary>
    public async Task RestartAsync()
    {
        await _app!.StopAsync();
        await _app.DisposeAsync();
        _app = null;

        await StartAppAsync();
    }

    private async Task StartAppAsync()
    {
        _app = await GatewayApp.BuildAsync(
            _database!.ApplicationArgs(
                "--Sessions:HubSweepInterval=00:00:01",
                $"--initial-admin-username={AdminUsername}",
                $"--initial-admin-password={AdminPassword}"),
            builder =>
            {
                builder.Services.RemoveAll<IDeviceDriverFactory>();
                builder.Services.AddSingleton<IDeviceDriverFactory>(Drivers);
                builder.Services.AddSingleton<IDeviceDriverFactory>(OtherDrivers);
                builder.Services.AddSingleton<IPushingDeviceDriverFactory>(new FakePushingDriverFactory());
                builder.Services.AddSingleton<IPushingDeviceDriverFactory>(new SilentPushingDriverFactory());

                // Every level, for this provider only: the query-token test must hold even
                // against the most verbose logging someone could switch on.
                builder.Logging.AddProvider(Logs);
                builder.Logging.AddFilter<CapturingLoggerProvider>(category: null, LogLevel.Trace);
            });

        await _app.StartAsync();
        BaseAddress = new Uri(_app.Urls.First());
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    public HttpClient CreateClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public async Task<string> LoginAsync(string username, string password)
    {
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    public Task<string> LoginAsAdminAsync() => LoginAsync(AdminUsername, AdminPassword);

    /// <summary>Creates a non-Admin user holding the given Site roles, and logs them in.</summary>
    public async Task<TestUser> CreateUserAsync(string adminToken, params (Guid SiteId, string Role)[] roles)
    {
        var username = $"user-{Guid.NewGuid():N}"[..20];
        using var client = CreateClient(adminToken);

        using var created = await client.PostAsJsonAsync("/api/users", new { username, password = UserPassword, isAdmin = false });
        created.EnsureSuccessStatusCode();
        var userId = await created.Content.ReadFromJsonAsync<Guid>();

        foreach (var (siteId, role) in roles)
        {
            using var granted = await client.PutAsJsonAsync($"/api/users/{userId}/sites/{siteId}", new { role });
            granted.EnsureSuccessStatusCode();
        }

        return new TestUser(userId, username, await LoginAsync(username, UserPassword));
    }

    /// <summary>
    /// Creates a device with one writable numeric tag on <paramref name="siteId"/>, scanned every
    /// <paramref name="scanIntervalMs"/> by a stand-in driver — or, for a pushing driver, with no
    /// scan interval at all, which is what the API demands of one (ADR-0016).
    /// </summary>
    public async Task<LiveDevice> CreateLiveDeviceAsync(
        string adminToken,
        Guid siteId,
        string name,
        string driverKey = FakeDriverFactory.Key,
        int? scanIntervalMs = 200)
    {
        using var client = CreateClient(adminToken);

        using var device = await client.PostAsJsonAsync(
            $"/api/sites/{siteId}/devices",
            new SaveDeviceRequest($"{name} device", driverKey, new Dictionary<string, string>(), scanIntervalMs, FolderId: null));
        device.EnsureSuccessStatusCode();
        var deviceId = await device.Content.ReadFromJsonAsync<Guid>();

        using var tag = await client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/tags",
            new SaveTagRequest(name, "Numeric", Unit: null, "holding:0", IsWritable: true));
        tag.EnsureSuccessStatusCode();

        return new LiveDevice(deviceId, await tag.Content.ReadFromJsonAsync<Guid>());
    }

    public async Task<Guid> CreateLiveTagAsync(string adminToken, Guid siteId, string name) =>
        (await CreateLiveDeviceAsync(adminToken, siteId, name)).TagId;

    public async Task<Guid> AddThresholdAsync(string adminToken, Guid tagId, double highLimit)
    {
        using var client = CreateClient(adminToken);
        using var response = await client.PostAsJsonAsync($"/api/tags/{tagId}/alarms", new { highLimit, lowLimit = (double?)null });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    /// <summary>
    /// Puts a threshold below anything the stand-in driver reports, and waits until the alarm
    /// is standing.
    /// </summary>
    public async Task<Guid> RaiseAlarmAsync(string adminToken, Guid tagId)
    {
        var definitionId = await AddThresholdAsync(adminToken, tagId, highLimit: 0);
        await WaitUntilAsync(async () => await AlarmStateAsync(adminToken, definitionId) is not null, $"alarm {definitionId} to be raised");
        return definitionId;
    }

    /// <summary>The standing alarm's state as the Admin sees it, or null if none is standing.</summary>
    public async Task<string?> AlarmStateAsync(string adminToken, Guid definitionId)
    {
        using var client = CreateClient(adminToken);
        var alarms = await client.GetFromJsonAsync<JsonElement>("/api/alarms");

        return alarms.EnumerateArray()
            .Where(alarm => alarm.GetProperty("definitionId").GetGuid() == definitionId)
            .Select(alarm => alarm.GetProperty("state").GetString())
            .FirstOrDefault();
    }

    public async Task<Guid> CreateTemplateAsync(string adminToken, string name)
    {
        using var client = CreateClient(adminToken);
        using var response = await client.PostAsJsonAsync("/api/templates", new { name });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    /// <summary>Waits until the historian holds at least one sample for the tag.</summary>
    public Task WaitForHistoryAsync(string adminToken, Guid tagId) =>
        WaitUntilAsync(
            async () =>
            {
                using var client = CreateClient(adminToken);
                var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
                var history = await client.GetFromJsonAsync<JsonElement>($"/api/tags/{tagId}/history?from={from}");
                return history.GetProperty("samples").GetArrayLength() > 0;
            },
            $"history for tag {tagId}");

    /// <summary>
    /// The actors of every audit entry with this action about this entity, read over the
    /// Gateway's own application-role connection.
    /// </summary>
    public async Task<IReadOnlyList<Guid?>> AuditActorsAsync(string action, Guid entityId)
    {
        await using var command = Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(
            "SELECT actor_user_id FROM audit_log WHERE action = @action AND entity_id = @entityId ORDER BY id");
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityId", entityId);

        var actors = new List<Guid?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actors.Add(reader.IsDBNull(0) ? null : reader.GetGuid(0));
        }

        return actors;
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultWait);

        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(100);
        }
    }
}

public sealed record TestUser(Guid Id, string Username, string Token);

public sealed record LiveDevice(Guid DeviceId, Guid TagId);
