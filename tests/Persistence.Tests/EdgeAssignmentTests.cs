using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// Migration 0012 over the application role (ADR-0019): an Edge whose name is its identity and is
/// unique across the tenant, and a device assigned to at most one edge — or to none, which leaves
/// it the Gateway's to poll exactly as before.
/// </summary>
public sealed class EdgeAssignmentTests : IClassFixture<TestDatabase>
{
    private const string UniqueViolation = "23505";

    private readonly TestDatabase _database;

    public EdgeAssignmentTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task The_index_refuses_a_duplicate_edge_name_however_it_is_cased()
    {
        // Through the raw table, so the refusal is the index's and not the repository's.
        var world = await SeedAsync();
        await using var connection = await _database.DataSource.OpenConnectionAsync();

        await InsertEdgeAsync(connection, world.TenantId, "Boiler House");
        var refused = await Assert.ThrowsAsync<PostgresException>(
            () => InsertEdgeAsync(connection, world.TenantId, "boiler HOUSE"));

        Assert.Equal(UniqueViolation, refused.SqlState);
        Assert.Equal(UniqueNames.EdgeIndex, refused.ConstraintName);
    }

    [RequiresDatabaseFact]
    public async Task A_name_is_unique_within_its_tenant_not_across_tenants()
    {
        // An edge's parent is its tenant (ADR-0015), so two tenants may each have a "Boiler
        // House" while one tenant may not have two.
        var first = await SeedAsync();
        var second = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);

        await edges.AddAsync(NewEdge(first, "Boiler House"), CancellationToken.None);
        await edges.AddAsync(NewEdge(second, "Boiler House"), CancellationToken.None);

        var all = await edges.GetAllAsync(CancellationToken.None);
        Assert.Single(all, e => e.TenantId == first.TenantId);
        Assert.Single(all, e => e.TenantId == second.TenantId);
    }

    [RequiresDatabaseFact]
    public async Task A_soft_deleted_edge_gives_up_its_name()
    {
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);

        var first = NewEdge(world, "Boiler House");
        await edges.AddAsync(first, CancellationToken.None);
        await edges.DeleteAsync(first.Id, CancellationToken.None);

        // The index covers live rows only (ADR-0015), so the name is free again.
        await edges.AddAsync(NewEdge(world, "boiler house"), CancellationToken.None);

        var all = await edges.GetAllAsync(CancellationToken.None);
        Assert.Equal("boiler house", Assert.Single(all, e => e.TenantId == world.TenantId).Name);
    }

    [RequiresDatabaseFact]
    public async Task A_device_carries_its_assignment_and_an_unassigned_device_carries_none()
    {
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);
        var edge = NewEdge(world, "Boiler House");
        await edges.AddAsync(edge, CancellationToken.None);

        var assigned = await AddDeviceAsync(world, "Pump 1", edge.Id);
        var unassigned = await AddDeviceAsync(world, "Pump 2");

        var devices = new DeviceRepository(_database.DataSource);
        Assert.Equal(edge.Id, (await devices.FindAsync(assigned, CancellationToken.None))!.EdgeId);
        Assert.Null((await devices.FindAsync(unassigned, CancellationToken.None))!.EdgeId);

        // The assignment is an id, not a name: renaming the edge leaves it in place.
        edge.Name = "Boiler House North";
        await edges.UpdateAsync(edge, CancellationToken.None);
        Assert.Equal(edge.Id, (await devices.FindAsync(assigned, CancellationToken.None))!.EdgeId);
    }

    [RequiresDatabaseFact]
    public async Task An_edge_that_still_has_a_device_is_not_deleted_until_it_is_unassigned()
    {
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);
        var edge = NewEdge(world, "Boiler House");
        await edges.AddAsync(edge, CancellationToken.None);
        var deviceId = await AddDeviceAsync(world, "Pump 1", edge.Id);

        var refused = await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => edges.DeleteAsync(edge.Id, CancellationToken.None));
        Assert.Contains("still has devices assigned", refused.Message);
        Assert.Contains(await edges.GetAllAsync(CancellationToken.None), e => e.Id == edge.Id);

        // Unassign, and the same delete now goes through.
        await using (var connection = await _database.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE device SET edge_id = NULL WHERE id = @deviceId", new { deviceId });
        }

        await edges.DeleteAsync(edge.Id, CancellationToken.None);
        Assert.DoesNotContain(await edges.GetAllAsync(CancellationToken.None), e => e.Id == edge.Id);
    }

    [RequiresDatabaseFact]
    public async Task The_application_role_can_write_edges()
    {
        // Migration 0012 leans on migration 0008's default privileges rather than granting again;
        // this proves the reliance holds for the role the Gateway runs as (ADR-0011).
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.ApplicationDataSource);
        var edge = NewEdge(world, "Boiler House");

        await edges.AddAsync(edge, CancellationToken.None);
        edge.Name = "Boiler House North";
        await edges.UpdateAsync(edge, CancellationToken.None);

        var all = await edges.GetAllAsync(CancellationToken.None);
        Assert.Equal("Boiler House North", Assert.Single(all, e => e.Id == edge.Id).Name);
    }

    [RequiresDatabaseFact]
    public async Task An_assignment_is_refused_by_name_while_the_edge_has_no_link_device()
    {
        // The Gateway stops polling a device an edge reads (ADR-0019), so the link is the only
        // thing that would feed its tags. An assignment before there is a link would leave them
        // with no source at all, and nothing would say so.
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);
        var edge = NewEdge(world, "Boiler House");
        await edges.AddAsync(edge, CancellationToken.None);

        var devices = new DeviceRepository(_database.DataSource);
        var device = await FindDeviceAsync(devices, await AddDeviceAsync(world, "Pump 1"));
        device.EdgeId = edge.Id;

        var refused = await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => devices.UpdateAsync(device, CancellationToken.None));

        Assert.Contains(edge.Name, refused.Message);
        Assert.Contains("no link device", refused.Message);
        Assert.Null((await devices.FindAsync(device.Id, CancellationToken.None))!.EdgeId);
    }

    [RequiresDatabaseFact]
    public async Task A_device_that_carries_an_edges_link_cannot_itself_be_assigned_to_an_edge()
    {
        // A device that carries a link *is* that link; another edge acquiring it would take the
        // transport out from under the first edge's tags.
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);
        var linkEdge = NewEdge(world, "Boiler House");
        var otherEdge = NewEdge(world, "Pump House");
        await edges.AddAsync(linkEdge, CancellationToken.None);
        await edges.AddAsync(otherEdge, CancellationToken.None);

        var linkDeviceId = await AddDeviceAsync(world, "Edge link");
        linkEdge.LinkDeviceId = linkDeviceId;
        await edges.UpdateAsync(linkEdge, CancellationToken.None);

        var devices = new DeviceRepository(_database.DataSource);
        var linkDevice = await FindDeviceAsync(devices, linkDeviceId);
        linkDevice.EdgeId = otherEdge.Id;

        var refused = await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => devices.UpdateAsync(linkDevice, CancellationToken.None));

        Assert.Contains(linkEdge.Name, refused.Message);
        Assert.Null((await devices.FindAsync(linkDeviceId, CancellationToken.None))!.EdgeId);
    }

    [RequiresDatabaseFact]
    public async Task An_edges_link_is_not_cleared_or_moved_while_a_device_is_assigned_to_it()
    {
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);
        var edge = NewEdge(world, "Boiler House");
        await edges.AddAsync(edge, CancellationToken.None);

        var linkDeviceId = await AddDeviceAsync(world, "Edge link");
        edge.LinkDeviceId = linkDeviceId;
        await edges.UpdateAsync(edge, CancellationToken.None);

        var otherLinkId = await AddDeviceAsync(world, "Spare link");
        await AddDeviceAsync(world, "Pump 1", edge.Id);

        edge.LinkDeviceId = null;
        var cleared = await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => edges.UpdateAsync(edge, CancellationToken.None));
        Assert.Contains("still has devices assigned", cleared.Message);

        edge.LinkDeviceId = otherLinkId;
        var moved = await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => edges.UpdateAsync(edge, CancellationToken.None));
        Assert.Contains("still has devices assigned", moved.Message);

        // Renaming with the link left where it is still goes through — the guard is on the link,
        // not on every edit of an edge that has devices.
        edge.LinkDeviceId = linkDeviceId;
        edge.Name = "Boiler House North";
        await edges.UpdateAsync(edge, CancellationToken.None);

        var stored = Assert.Single(await edges.GetAllAsync(CancellationToken.None), e => e.Id == edge.Id);
        Assert.Equal("Boiler House North", stored.Name);
        Assert.Equal(linkDeviceId, stored.LinkDeviceId);
    }

    [RequiresDatabaseFact]
    public async Task A_device_that_carries_an_edges_link_is_not_deleted_until_the_edge_is_repointed()
    {
        // Deleting it would leave the edge still naming a device that is gone, so the next device
        // assigned to that edge would pass the assignment guard with nothing reading it.
        var world = await SeedAsync();
        var edges = new EdgeRepository(_database.DataSource);
        var edge = NewEdge(world, "Boiler House");
        await edges.AddAsync(edge, CancellationToken.None);

        var linkDeviceId = await AddDeviceAsync(world, "Edge link");
        edge.LinkDeviceId = linkDeviceId;
        await edges.UpdateAsync(edge, CancellationToken.None);

        var devices = new DeviceRepository(_database.DataSource);
        var refused = await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => devices.DeleteAsync(linkDeviceId, CancellationToken.None));

        Assert.Contains(edge.Name, refused.Message);
        Assert.NotNull(await devices.FindAsync(linkDeviceId, CancellationToken.None));

        // Repointed — allowed, because nothing is assigned — and the same delete now goes through.
        edge.LinkDeviceId = null;
        await edges.UpdateAsync(edge, CancellationToken.None);

        await devices.DeleteAsync(linkDeviceId, CancellationToken.None);
        Assert.Null(await devices.FindAsync(linkDeviceId, CancellationToken.None));
    }

    private static async Task<Device> FindDeviceAsync(DeviceRepository devices, Guid deviceId) =>
        await devices.FindAsync(deviceId, CancellationToken.None)
        ?? throw new InvalidOperationException($"Device {deviceId} was not found.");

    private static Edge NewEdge(World world, string name) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = world.TenantId,
        Name = name,
    };

    private static Task<int> InsertEdgeAsync(NpgsqlConnection connection, Guid tenantId, string name) =>
        connection.ExecuteAsync(
            "INSERT INTO edge (id, tenant_id, name) VALUES (@id, @tenant, @name)",
            new { id = Guid.NewGuid(), tenant = tenantId, name });

    private async Task<Guid> AddDeviceAsync(World world, string name, Guid? edgeId = null)
    {
        var deviceId = Guid.NewGuid();
        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO device (id, site_id, name, driver_key, connection_settings, scan_interval_ms, edge_id)
            VALUES (@id, @site, @name, 'modbus-tcp', '{}'::jsonb, 1000, @edgeId)
            """,
            new { id = deviceId, site = world.SiteId, name, edgeId });
        return deviceId;
    }

    private async Task<World> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var world = new World(tenantId, Guid.NewGuid());

        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO tenant (id, name) VALUES (@tenant, 'Edge tenant');
            INSERT INTO site (id, tenant_id, name) VALUES (@site, @tenant, 'Edge site');
            """,
            new { tenant = tenantId, site = world.SiteId });

        return world;
    }

    private sealed record World(Guid TenantId, Guid SiteId);
}
