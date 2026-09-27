using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Turns a violation of ADR-0015's name indexes into a refusal an operator can act on: what
/// already has that name, and where.
/// </summary>
/// <remarks>
/// The database is what enforces the rule (migration 0010) — no code path can bypass an index.
/// This only puts it into words, and in the operator's words: a Site and folder path, never a
/// constraint or a decision record. Any other unique violation is left alone, because calling
/// it a name clash would mislead.
/// </remarks>
internal static class UniqueNames
{
    public const string DeviceIndex = "ux_device_name_in_parent";
    public const string FolderIndex = "ux_folder_name_in_parent";
    public const string TagIndex = "ux_tag_name_in_device";
    public const string EdgeIndex = "ux_edge_name_in_tenant";

    private const string UniqueViolation = "23505";

    /// <summary>Whether this is one of the name indexes refusing a write.</summary>
    public static bool IsNameClash(PostgresException exception) =>
        exception.SqlState == UniqueViolation &&
        exception.ConstraintName is DeviceIndex or FolderIndex or TagIndex or EdgeIndex;

    public static async Task<ConfigurationConflictException> DeviceTakenAsync(
        NpgsqlDataSource dataSource, string name, Guid siteId, Guid? folderId, CancellationToken cancellationToken) =>
        new($"A device named '{name}' already exists {await PlaceAsync(dataSource, siteId, folderId, cancellationToken).ConfigureAwait(false)}.");

    public static async Task<ConfigurationConflictException> FolderTakenAsync(
        NpgsqlDataSource dataSource, string name, Guid siteId, Guid? parentFolderId, CancellationToken cancellationToken) =>
        new($"A folder named '{name}' already exists {await PlaceAsync(dataSource, siteId, parentFolderId, cancellationToken).ConfigureAwait(false)}.");

    public static async Task<ConfigurationConflictException> TagTakenAsync(
        NpgsqlDataSource dataSource, string name, Guid deviceId, CancellationToken cancellationToken) =>
        new($"The device {await DevicePathAsync(dataSource, deviceId, cancellationToken).ConfigureAwait(false)} already has a tag named '{name}'.");

    /// <summary>
    /// An edge whose name is already taken. No place to name, unlike the others: an edge's name
    /// is unique across the tenant rather than within a parent (ADR-0015, ADR-0019).
    /// </summary>
    public static ConfigurationConflictException EdgeTaken(string name) =>
        new($"An edge named '{name}' already exists. An edge's name is the name in its certificate, so it must be unique across the deployment.");

    /// <summary>A tag added to a template, which one of the devices made from it already has.</summary>
    public static async Task<ConfigurationConflictException> TemplateTagTakenAsync(
        NpgsqlDataSource dataSource, string name, Guid templateId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var deviceId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            SELECT d.id FROM device d JOIN tag t ON t.device_id = d.id
            WHERE d.template_id = @templateId AND d.deleted_at IS NULL
              AND t.deleted_at IS NULL AND lower(t.name) = lower(@name)
            ORDER BY d.name LIMIT 1
            """,
            new { templateId, name },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var device = deviceId is { } id ? await DevicePathAsync(dataSource, id, cancellationToken).ConfigureAwait(false) : null;
        return new ConfigurationConflictException(device is null
            ? $"A device made from this template already has a tag named '{name}', so it cannot be added to the template."
            : $"The device {device}, made from this template, already has a tag named '{name}', so it cannot be added to the template.");
    }

    /// <summary>A template whose own tags would give one device two tags of the same name.</summary>
    public static ConfigurationConflictException TemplateTagsCollide() =>
        new("This template has two tags whose names differ only in case, so a device made from it would have two tags of the same name. Rename one of them in the template.");

    /// <summary>"directly under Skopje", or "in Skopje / Building / Hall".</summary>
    private static async Task<string> PlaceAsync(
        NpgsqlDataSource dataSource, Guid siteId, Guid? folderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var site = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM site WHERE id = @siteId", new { siteId }, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? "this Site";

        if (folderId is not { } folder)
        {
            return $"directly under {site}";
        }

        var path = await FolderPathAsync(connection, folder, cancellationToken).ConfigureAwait(false);
        return $"in {site} / {path}";
    }

    /// <summary>"Skopje / Hall / Pump House".</summary>
    private static async Task<string> DevicePathAsync(NpgsqlDataSource dataSource, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var device = await connection.QuerySingleOrDefaultAsync<(string Site, string Device, Guid? FolderId)>(new CommandDefinition(
            """
            SELECT s.name, d.name, d.folder_id
            FROM device d JOIN site s ON s.id = d.site_id
            WHERE d.id = @deviceId
            """,
            new { deviceId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (device.Device is null)
        {
            return "this device";
        }

        return device.FolderId is { } folder
            ? $"{device.Site} / {await FolderPathAsync(connection, folder, cancellationToken).ConfigureAwait(false)} / {device.Device}"
            : $"{device.Site} / {device.Device}";
    }

    /// <summary>A folder's names from the Site down, e.g. "Building / Hall".</summary>
    private static Task<string> FolderPathAsync(NpgsqlConnection connection, Guid folderId, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<string>(new CommandDefinition(
            // Bounded depth, so a cycle already in the table cannot make an error message hang.
            """
            WITH RECURSIVE up AS (
                SELECT id, parent_folder_id, name, 1 AS depth FROM folder WHERE id = @folderId
                UNION ALL
                SELECT f.id, f.parent_folder_id, f.name, u.depth + 1
                FROM folder f JOIN up u ON f.id = u.parent_folder_id
                WHERE u.depth < 64
            )
            SELECT string_agg(name, ' / ' ORDER BY depth DESC) FROM up
            """,
            new { folderId },
            cancellationToken: cancellationToken))!;
}
