using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Device configuration storage (ADR-0008).</summary>
public sealed class DeviceRepository : IDeviceRepository
{
    private const string SelectColumns = """
        SELECT id, site_id, folder_id, name, driver_key,
               connection_settings::text AS connection_settings, scan_interval_ms,
               template_id, edge_id
        FROM device_active
        """;

    private readonly NpgsqlDataSource _dataSource;

    static DeviceRepository() => DapperConfiguration.Ensure();

    public DeviceRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<Device>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DeviceRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE site_id = @siteId ORDER BY name",
                new { siteId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<DeviceRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE id = @deviceId",
                new { deviceId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToDomain();
    }

    public async Task AddAsync(Device device, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO device (id, site_id, folder_id, name, driver_key, connection_settings, scan_interval_ms, edge_id)
                VALUES (@Id, @SiteId, @FolderId, @Name, @DriverKey, @ConnectionSettings::jsonb, @ScanIntervalMs, @EdgeId)
                """,
                ToParameters(device),
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.DeviceTakenAsync(_dataSource, device.Name, device.SiteId, device.FolderId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(Device device, CancellationToken cancellationToken)
    {
        // site_id is deliberately not updatable. It is the tenant and security scope
        // (ADR-0004), not a placement field: moving a device between sites would change
        // who can see its history, which is not something an edit form should do.
        //
        // edge_id, by contrast, is: assigning a device to an edge or releasing it (ADR-0019)
        // is an ordinary edit of the device, and the only way the assignment ever changes.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int updated;
        try
        {
            updated = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE device
                SET folder_id = @FolderId,
                    name = @Name,
                    driver_key = @DriverKey,
                    connection_settings = @ConnectionSettings::jsonb,
                    scan_interval_ms = @ScanIntervalMs,
                    edge_id = @EdgeId
                WHERE id = @Id AND deleted_at IS NULL
                """,
                ToParameters(device),
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.DeviceTakenAsync(_dataSource, device.Name, device.SiteId, device.FolderId, cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            throw new ConfigurationConflictException($"Device {device.Id} no longer exists.");
        }
    }

    public async Task DeleteAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Device and tags go in one transaction. Half a delete — a device gone while its
        // tags remain, or the reverse — would leave configuration in a state no operator
        // asked for and no screen renders sensibly.
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE device SET deleted_at = now() WHERE id = @deviceId AND deleted_at IS NULL",
            new { deviceId },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (deleted == 0)
        {
            throw new ConfigurationConflictException($"Device {deviceId} no longer exists.");
        }

        // Cascading is right here where it is wrong for a folder: a tag has no placement
        // of its own to preserve, since its device owns it (ADR-0001 §3).
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE tag SET deleted_at = now() WHERE device_id = @deviceId AND deleted_at IS NULL",
            new { deviceId },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object ToParameters(Device device) => new
    {
        device.Id,
        device.SiteId,
        device.FolderId,
        device.Name,
        device.DriverKey,
        ConnectionSettings = ConnectionSettingsJson.Serialize(device.ConnectionSettings),
        ScanIntervalMs = (int)device.ScanInterval.TotalMilliseconds,
        device.EdgeId,
    };
}
