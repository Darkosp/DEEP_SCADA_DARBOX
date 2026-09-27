using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Edge configuration storage (ADR-0008, ADR-0019).</summary>
/// <remarks>
/// The name's uniqueness is the database's to enforce (migration 0012); this only turns a
/// violation into a refusal an operator can read, as it does for the other name indexes.
/// </remarks>
public sealed class EdgeRepository : IEdgeRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static EdgeRepository() => DapperConfiguration.Ensure();

    public EdgeRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<Edge>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<EdgeRow>(
            new CommandDefinition(
                "SELECT id, tenant_id, name FROM edge_active ORDER BY name",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(Edge edge, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO edge (id, tenant_id, name) VALUES (@Id, @TenantId, @Name)",
                edge,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw UniqueNames.EdgeTaken(edge.Name);
        }
    }

    public async Task UpdateAsync(Edge edge, CancellationToken cancellationToken)
    {
        // tenant_id is deliberately not updatable. It is the ownership scope (ADR-0004), not a
        // field an edit form should be able to move.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int updated;
        try
        {
            updated = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE edge
                SET name = @Name
                WHERE id = @Id AND deleted_at IS NULL
                """,
                edge,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw UniqueNames.EdgeTaken(edge.Name);
        }

        if (updated == 0)
        {
            throw new ConfigurationConflictException($"Edge {edge.Id} no longer exists.");
        }
    }

    public async Task DeleteAsync(Guid edgeId, CancellationToken cancellationToken)
    {
        // Emptiness is checked inside the UPDATE, so a device assigned to this edge concurrently
        // cannot land on an edge a parallel request is deleting.
        const string sql = """
            UPDATE edge
            SET deleted_at = now()
            WHERE id = @edgeId
              AND deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM device_active WHERE edge_id = @edgeId)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { edgeId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            return;
        }

        // Nothing was written: either the edge is already gone, or it still has a device
        // assigned. Saying which is the difference between an error the operator can act on and
        // one they cannot.
        var stillHasDevices = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM device_active WHERE edge_id = @edgeId)",
                new { edgeId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        throw stillHasDevices
            ? new ConfigurationConflictException(
                "This edge still has devices assigned to it. Unassign them first — deleting an edge "
                + "never unassigns what it reads, because what an edge reads must not change as a "
                + "side effect of something else.")
            : new ConfigurationConflictException($"Edge {edgeId} no longer exists.");
    }
}
