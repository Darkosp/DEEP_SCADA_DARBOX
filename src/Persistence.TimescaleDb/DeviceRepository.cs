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

        // A device may be created already assigned to an edge (ADR-0019), and that assignment is
        // guarded exactly as an edit's is: the Gateway stops polling a device an edge reads, so
        // assigning one to an edge with no link device — or assigning a device that carries a
        // link — would leave its tags with no source at all. The guard is part of the statement
        // for the same reason it is on the update: a concurrent edit of the edge cannot land
        // behind the check.
        const string sql = """
            INSERT INTO device (id, site_id, folder_id, name, driver_key, connection_settings, scan_interval_ms, edge_id)
            SELECT @Id, @SiteId, @FolderId, @Name, @DriverKey, @ConnectionSettings::jsonb, @ScanIntervalMs, @EdgeId
            WHERE @EdgeId IS NULL
               OR (EXISTS (SELECT 1 FROM edge_active
                           WHERE id = @EdgeId AND link_device_id IS NOT NULL)
                   AND NOT EXISTS (SELECT 1 FROM edge_active WHERE link_device_id = @Id))
            """;

        int inserted;
        try
        {
            inserted = await connection.ExecuteAsync(
                new CommandDefinition(sql, ToParameters(device), cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.DeviceTakenAsync(_dataSource, device.Name, device.SiteId, device.FolderId, cancellationToken).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            throw await RefuseAssignmentAsync(device, cancellationToken).ConfigureAwait(false);
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
        //
        // Two assignments are refused, and refused inside the statement so that a concurrent
        // edit of the edge cannot land behind the check. Assigning to an edge that has no link
        // device would leave the device's tags with no source at all — the Gateway stops polling
        // it (ADR-0019) and nothing carries it. And a device that carries an edge's link *is*
        // that link: another edge cannot also acquire it.
        const string sql = """
            UPDATE device
            SET folder_id = @FolderId,
                name = @Name,
                driver_key = @DriverKey,
                connection_settings = @ConnectionSettings::jsonb,
                scan_interval_ms = @ScanIntervalMs,
                edge_id = @EdgeId
            WHERE id = @Id
              AND deleted_at IS NULL
              AND (@EdgeId IS NULL
                   OR (EXISTS (SELECT 1 FROM edge_active
                               WHERE id = @EdgeId AND link_device_id IS NOT NULL)
                       AND NOT EXISTS (SELECT 1 FROM edge_active WHERE link_device_id = @Id)))
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int updated;
        try
        {
            updated = await connection.ExecuteAsync(
                new CommandDefinition(sql, ToParameters(device), cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.DeviceTakenAsync(_dataSource, device.Name, device.SiteId, device.FolderId, cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            throw await RefuseAssignmentAsync(device, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Why a write was refused, in the operator's words (ADR-0019). Called only once the
    /// statement has written nothing, so one of these is true.
    /// </summary>
    private async Task<ConfigurationConflictException> RefuseAssignmentAsync(
        Device device, CancellationToken cancellationToken)
    {
        if (device.EdgeId is not { } edgeId)
        {
            return new ConfigurationConflictException($"Device {device.Id} no longer exists.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var carriesALink = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM edge_active WHERE link_device_id = @deviceId",
            new { deviceId = device.Id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (carriesALink is not null)
        {
            return new ConfigurationConflictException(
                $"This device carries the link for the edge '{carriesALink}', so it cannot itself be "
                + "assigned to an edge. Repoint that edge at another device first.");
        }

        var edge = await connection.QuerySingleOrDefaultAsync<EdgeRow>(new CommandDefinition(
            "SELECT id, tenant_id, name, link_device_id FROM edge_active WHERE id = @edgeId",
            new { edgeId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return edge is null
            ? new ConfigurationConflictException($"Edge {edgeId} no longer exists.")
            : new ConfigurationConflictException(
                $"The edge '{edge.Name}' has no link device yet, so this device cannot be assigned "
                + "to it. The Gateway stops polling a device an edge reads, and that edge's link is "
                + "what carries its tags instead — with no link, nothing would read them.");
    }

    public async Task DeleteAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Device and tags go in one transaction. Half a delete — a device gone while its
        // tags remain, or the reverse — would leave configuration in a state no operator
        // asked for and no screen renders sensibly.
        //
        // A device an edge names as its link is not deleted (ADR-0019). More than its own tags
        // is at stake: the edge keeps naming it, so the assignment guard would still pass for
        // that edge afterwards and the next device assigned to it would be left with nothing
        // reading it. The edge is repointed first, explicitly, and the check is part of the
        // statement so a concurrent assignment cannot land behind it.
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE device
            SET deleted_at = now()
            WHERE id = @deviceId
              AND deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM edge_active WHERE link_device_id = @deviceId)
            """,
            new { deviceId },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (deleted == 0)
        {
            // Nothing was written: either the device is gone, or it carries an edge's link.
            var carriesALink = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT name FROM edge_active WHERE link_device_id = @deviceId",
                new { deviceId },
                transaction,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            throw carriesALink is not null
                ? new ConfigurationConflictException(
                    $"This device carries the link for the edge '{carriesALink}', so it cannot be "
                    + "deleted. Repoint that edge at another device first.")
                : new ConfigurationConflictException($"Device {deviceId} no longer exists.");
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
