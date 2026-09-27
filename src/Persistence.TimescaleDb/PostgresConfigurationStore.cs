using Dapper;
using Npgsql;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Reads the configured hierarchy from PostgreSQL with Dapper (ADR-0008).
/// Configuration lives in the same engine as the time-series data (ADR-0006).
/// </summary>
public sealed class PostgresConfigurationStore : IConfigurationStore
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresConfigurationStore() => DapperConfiguration.Ensure();

    public PostgresConfigurationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Tenant> GetTenantAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var tenant = await connection.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition("SELECT id, name FROM tenant LIMIT 1", cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        // Every deployment holds exactly one tenant row (ADR-0004); its absence means
        // the instance was never provisioned, not that tenancy is optional.
        return tenant ?? throw new InvalidOperationException("No tenant row found — the instance is not provisioned.");
    }

    public async Task<IReadOnlyList<Site>> GetSitesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<SiteRow>(
            new CommandDefinition(
                "SELECT id, tenant_id, name, time_zone_id FROM site ORDER BY name",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Folder>> GetFoldersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<FolderRow>(
            new CommandDefinition(
                "SELECT id, site_id, parent_folder_id, name FROM folder_active ORDER BY name",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DeviceRow>(
            new CommandDefinition(
                """
                SELECT id, site_id, folder_id, name, driver_key,
                       connection_settings::text AS connection_settings, scan_interval_ms,
                       template_id, edge_id
                FROM device_active
                ORDER BY name
                """,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Edge>> GetEdgesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<EdgeRow>(
            new CommandDefinition(
                "SELECT id, tenant_id, name, link_device_id FROM edge_active ORDER BY name",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<TagRow>(
            new CommandDefinition(
                """
                SELECT id, device_id, name, value_kind, unit_symbol, unit_dimension,
                       unit_factor_to_si, unit_offset_to_si, source_address, is_writable,
                       template_tag_id
                FROM tag_active
                ORDER BY name
                """,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<AlarmDefinition>> GetAlarmDefinitionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AlarmDefinitionRow>(
            new CommandDefinition(
                "SELECT id, tag_id, high_limit, low_limit FROM alarm_definition_active",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }
}
