using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Browse-tree and configuration endpoints: everything Phase 2 needs to add and edit a
/// device through the UI, with no code required.
/// </summary>
internal static class ConfigurationEndpoints
{
    internal static void MapConfigurationApi(this WebApplication app)
    {
        app.MapGet("/api/sites", (TagCatalogSource catalogSource, Caller caller) =>
            Results.Ok(catalogSource.Current.Sites
                .Where(site => caller.Access.CanView(site.Id))
                .OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                .Select(site => new SiteDto(site.Id, site.Name, site.TimeZoneId))));

        // Which drivers this build has, and which push — so the device form offers a scan
        // interval only where one means something (ADR-0016).
        app.MapGet("/api/drivers", (DriverShapes shapes) =>
            Results.Ok(shapes.All.Select(driver => new DriverDto(driver.Key, driver.Pushing))));

        app.MapGet("/api/sites/{siteId:guid}/tree", (Guid siteId, TagCatalogSource catalogSource, DriverShapes shapes, Caller caller) =>
        {
            // The most literal reading of Phase 5's gate: a whole Site's configuration.
            // Not found rather than forbidden, so the answer does not confirm the Site
            // exists (ADR-0011).
            if (!caller.Access.CanView(siteId))
            {
                return Results.NotFound();
            }

            var tree = SiteTreeBuilder.Build(catalogSource.Current, siteId, shapes);
            return tree is null ? Results.NotFound() : Results.Ok(tree);
        });

        MapFolders(app);
        MapDevices(app);
        MapTags(app);
        MapTagDeletion(app);
        app.MapEdgeApi();
    }

    private static void MapFolders(WebApplication app)
    {
        app.MapPost("/api/sites/{siteId:guid}/folders", async (
            Guid siteId,
            CreateFolderRequest request,
            IFolderRepository folders,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var folder = new Folder
            {
                Id = Guid.NewGuid(),
                SiteId = siteId,
                ParentFolderId = request.ParentFolderId,
                Name = request.Name,
            };

            return await SaveAsync(
                () => folders.AddAsync(folder, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/sites/{siteId}/tree", folder.Id));
        }).AdminWrite("folder.create", "folder");

        app.MapDelete("/api/sites/{siteId:guid}/folders/{folderId:guid}", async (
            Guid folderId,
            IFolderRepository folders,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await SaveAsync(
                () => folders.DeleteAsync(folderId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent)).AdminWrite("folder.delete", "folder", "folderId");

        app.MapPut("/api/sites/{siteId:guid}/folders/{folderId:guid}", async (
            Guid siteId,
            Guid folderId,
            UpdateFolderRequest request,
            IFolderRepository folders,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var folder = new Folder
            {
                Id = folderId,
                SiteId = siteId,
                ParentFolderId = request.ParentFolderId,
                Name = request.Name,
            };

            return await SaveAsync(
                () => folders.UpdateAsync(folder, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent);
        }).AdminWrite("folder.update", "folder", "folderId");
    }

    private static void MapDevices(WebApplication app)
    {
        app.MapPost("/api/sites/{siteId:guid}/devices", async (
            Guid siteId,
            SaveDeviceRequest request,
            IDeviceRepository devices,
            DriverShapes shapes,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (shapes.ScanIntervalProblem(request.DriverKey, request.ScanIntervalMs) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var device = ToDomain(Guid.NewGuid(), siteId, request);

            return await SaveAsync(
                () => devices.AddAsync(device, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/devices/{device.Id}", device.Id));
        }).AdminWrite("device.create", "device");

        app.MapPut("/api/sites/{siteId:guid}/devices/{deviceId:guid}", async (
            Guid siteId,
            Guid deviceId,
            SaveDeviceRequest request,
            IDeviceRepository devices,
            DriverShapes shapes,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (shapes.ScanIntervalProblem(request.DriverKey, request.ScanIntervalMs) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            return await SaveAsync(
                () => devices.UpdateAsync(ToDomain(deviceId, siteId, request), cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent);
        }).AdminWrite("device.update", "device", "deviceId");

        app.MapDelete("/api/sites/{siteId:guid}/devices/{deviceId:guid}", async (
            Guid deviceId,
            IDeviceRepository devices,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await SaveAsync(
                () => devices.DeleteAsync(deviceId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent)).AdminWrite("device.delete", "device", "deviceId");

        app.MapGet("/api/devices/{deviceId:guid}", (Guid deviceId, TagCatalogSource catalogSource, DriverShapes shapes, Caller caller) =>
        {
            var catalog = catalogSource.Current;
            var device = catalog.FindDevice(deviceId);

            return device is null || !caller.Access.CanView(device.SiteId)
                ? Results.NotFound()
                : Results.Ok(SiteTreeBuilder.ToDto(device, catalog, shapes));
        });
    }

    private static void MapTags(WebApplication app)
    {
        app.MapPost("/api/devices/{deviceId:guid}/tags", async (
            Guid deviceId,
            SaveTagRequest request,
            ITagRepository tags,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (!TryToDomain(Guid.NewGuid(), deviceId, request, out var tag, out var error))
            {
                return Results.BadRequest(new { error });
            }

            return await SaveAsync(
                () => tags.AddAsync(tag, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/tags/{tag.Id}", tag.Id));
        }).AdminWrite("tag.create", "tag");

        app.MapPut("/api/devices/{deviceId:guid}/tags/{tagId:guid}", async (
            Guid deviceId,
            Guid tagId,
            SaveTagRequest request,
            ITagRepository tags,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (!TryToDomain(tagId, deviceId, request, out var tag, out var error))
            {
                return Results.BadRequest(new { error });
            }

            return await SaveAsync(
                () => tags.UpdateAsync(tag, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent);
        }).AdminWrite("tag.update", "tag", "tagId");
    }

    private static void MapTagDeletion(WebApplication app) =>
        app.MapDelete("/api/devices/{deviceId:guid}/tags/{tagId:guid}", async (
            Guid tagId,
            ITagRepository tags,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await SaveAsync(
                () => tags.DeleteAsync(tagId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent)).AdminWrite("tag.delete", "tag", "tagId");

    /// <summary>
    /// Runs a configuration write, reloads the catalogue on success, and turns the
    /// failures this layer can expect into client errors rather than 500s.
    /// </summary>
    internal static async Task<IResult> SaveAsync(
        Func<Task> write,
        ConfigurationReloader reloader,
        CancellationToken cancellationToken,
        Func<IResult> success)
    {
        try
        {
            await write();
        }
        catch (ConfigurationConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (PostgresException exception) when (exception.SqlState == "23503")
        {
            // The composite foreign keys are the enforcement point for cross-site
            // placement, so this is a request the database refused, not a server fault.
            return Results.BadRequest(new
            {
                error = exception.ConstraintName switch
                {
                    "fk_folder_parent_same_site" =>
                        "A folder's parent must belong to the same site as the folder.",
                    "fk_device_folder_same_site" =>
                        "A device's folder must belong to the same site as the device.",
                    _ => "The referenced site, folder or device does not exist.",
                },
            });
        }

        // Only after the write lands: a reload on a failed write would republish the
        // catalogue that is already in force, for nothing.
        await reloader.ReloadAsync(cancellationToken);
        return success();
    }

    private static Device ToDomain(Guid id, Guid siteId, SaveDeviceRequest request)
    {
        var device = new Device
        {
            Id = id,
            SiteId = siteId,
            FolderId = request.FolderId,
            Name = request.Name,
            DriverKey = request.DriverKey,
            ConnectionSettings = request.ConnectionSettings,
            // Which edge acquires this device, or none for the Gateway to poll it itself
            // (ADR-0019). The repository refuses an assignment that would leave the device's
            // tags with no source at all.
            EdgeId = request.EdgeId,
        };

        // A pushing device has none (ADR-0016): the stored column keeps its default, nothing
        // reads it, and the API never shows it. Refused before this point if one was sent.
        if (request.ScanIntervalMs is { } scanIntervalMs)
        {
            device.ScanInterval = TimeSpan.FromMilliseconds(scanIntervalMs);
        }

        return device;
    }

    private static bool TryToDomain(
        Guid id,
        Guid deviceId,
        SaveTagRequest request,
        out Tag tag,
        out string error)
    {
        tag = null!;
        error = string.Empty;

        if (!Enum.TryParse<TagValueKind>(request.ValueKind, ignoreCase: true, out var valueKind))
        {
            error = $"Unknown value kind '{request.ValueKind}'.";
            return false;
        }

        UnitOfMeasure? unit;
        try
        {
            unit = request.Unit?.ToDomain();
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        if (unit is not null && valueKind != TagValueKind.Numeric)
        {
            // A unit describes a measured quantity. Attaching one to a boolean or a text
            // tag would give the UI something to display that means nothing.
            error = "Only a numeric tag can carry a unit of measure.";
            return false;
        }

        tag = new Tag
        {
            Id = id,
            DeviceId = deviceId,
            Name = request.Name,
            ValueKind = valueKind,
            Unit = unit,
            SourceAddress = request.SourceAddress,
            IsWritable = request.IsWritable,
        };

        return true;
    }
}
