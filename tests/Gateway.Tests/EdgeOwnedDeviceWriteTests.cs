using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0019 §3: a device assigned to an edge is acquired by that edge, not by the Gateway. The
/// link is outbound only, so the Gateway has no route to such a device — and a write to one of
/// its tags must say so, rather than open a connection that cannot be made and report a device
/// error that never happened (ADR-0003).
/// </summary>
public sealed class EdgeOwnedDeviceWriteTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public EdgeOwnedDeviceWriteTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task A_write_to_a_tag_on_an_edge_assigned_device_is_refused_by_name_and_journalled()
    {
        var admin = await _host.LoginAsAdminAsync();
        var @operator = await _host.CreateUserAsync(admin, (Bitola, "Operator"));
        var edgeOwned = await _host.CreateLiveDeviceAsync(admin, Bitola, "Edge-owned write probe");
        var gatewayOwned = await _host.CreateLiveDeviceAsync(admin, Bitola, "Gateway-owned write probe");

        var edge = await AssignToNewEdgeAsync(admin, edgeOwned.DeviceId);

        using var asOperator = _host.CreateClient(@operator.Token);

        // Refused, and named: the operator learns which edge holds the device, not that it failed.
        using var refused = await asOperator.PostAsJsonAsync($"/api/tags/{edgeOwned.TagId}/value", new { value = 42 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(edge.Name, await refused.Content.ReadAsStringAsync());

        // Refused, not attempted: nothing reached a driver, and the attempt is in the journal.
        Assert.DoesNotContain(_host.Drivers.Writes, write => write.Tag.TagId == edgeOwned.TagId);
        Assert.Equal(@operator.Id, Assert.Single(await _host.AuditActorsAsync("tag.write_refused", edgeOwned.TagId)));
        Assert.Empty(await _host.AuditActorsAsync("tag.write", edgeOwned.TagId));

        // The control shows the refusal is the assignment and not the write path: a device the
        // Gateway still owns is written exactly as before.
        using var allowed = await asOperator.PostAsJsonAsync($"/api/tags/{gatewayOwned.TagId}/value", new { value = 7 });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Single(_host.Drivers.Writes, write => write.Tag.TagId == gatewayOwned.TagId);
    }

    /// <summary>
    /// Assigns a device to a new edge the way a later slice's <c>/api/edges</c> will: through the
    /// repository, then a catalogue reload. No endpoint does this yet — ADR-0019 leaves it to a
    /// later slice — so the test writes the row directly, through the same repository that slice
    /// will use rather than through SQL of its own.
    /// </summary>
    /// <remarks>
    /// The edge names a link device before anything is assigned to it. The Gateway stops polling a
    /// device an edge reads, so that link is the only thing that would feed its tags, and the
    /// repository refuses an assignment without one (ADR-0019).
    /// </remarks>
    private async Task<Edge> AssignToNewEdgeAsync(string adminToken, Guid deviceId)
    {
        var link = await _host.CreateLiveDeviceAsync(
            adminToken, Bitola, "Edge link probe", FakePushingDriverFactory.Key, scanIntervalMs: null);

        var dataSource = _host.Services.GetRequiredService<NpgsqlDataSource>();
        var tenant = await _host.Services.GetRequiredService<IConfigurationStore>()
            .GetTenantAsync(CancellationToken.None);

        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"edge-{Guid.NewGuid():N}",
            LinkDeviceId = link.DeviceId,
        };

        await new EdgeRepository(dataSource).AddAsync(edge, CancellationToken.None);

        var devices = _host.Services.GetRequiredService<IDeviceRepository>();
        var device = await devices.FindAsync(deviceId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Device {deviceId} was not found.");

        device.EdgeId = edge.Id;
        await devices.UpdateAsync(device, CancellationToken.None);

        await _host.Services.GetRequiredService<ConfigurationReloader>().ReloadAsync(CancellationToken.None);

        return edge;
    }
}
