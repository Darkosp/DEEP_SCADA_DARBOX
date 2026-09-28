using System.Net;
using System.Net.Http.Json;
using ScadaDarbox.Gateway.Contracts;
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

        var edgeName = await AssignToNewEdgeAsync(admin, edgeOwned.DeviceId);

        using var asOperator = _host.CreateClient(@operator.Token);

        // Refused, and named: the operator learns which edge holds the device, not that it failed.
        using var refused = await asOperator.PostAsJsonAsync($"/api/tags/{edgeOwned.TagId}/value", new { value = 42 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(edgeName, await refused.Content.ReadAsStringAsync());

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
    /// Assigns a device to a new edge through the API an operator uses: create the edge naming the
    /// device that carries its link, then edit the device to name that edge (ADR-0019). The edge's
    /// name comes back, so the refusal can be checked against it.
    /// </summary>
    /// <remarks>
    /// The edge names a link device before anything is assigned to it. The Gateway stops polling a
    /// device an edge reads, so that link is the only thing that would feed its tags, and an
    /// assignment without one is refused rather than left with nothing reading them (ADR-0019).
    /// </remarks>
    private async Task<string> AssignToNewEdgeAsync(string adminToken, Guid deviceId)
    {
        var link = await _host.CreateLiveDeviceAsync(
            adminToken, Bitola, "Edge link probe", FakePushingDriverFactory.Key, scanIntervalMs: null);

        using var client = _host.CreateClient(adminToken);

        var name = $"edge-{Guid.NewGuid():N}";
        using var created = await client.PostAsJsonAsync("/api/edges", new { name, linkDeviceId = link.DeviceId });
        created.EnsureSuccessStatusCode();
        var edgeId = await created.Content.ReadFromJsonAsync<Guid>();

        // Assigning is an ordinary edit of the device (ADR-0019 §2), so the device is saved whole
        // with the edge it now belongs to — the same request the device form makes.
        var device = await client.GetFromJsonAsync<TreeDeviceDto>($"/api/devices/{deviceId}")
            ?? throw new InvalidOperationException($"Device {deviceId} was not found.");

        using var assigned = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}",
            new SaveDeviceRequest(
                device.Name, device.DriverKey, device.ConnectionSettings, device.ScanIntervalMs, device.FolderId, edgeId));
        assigned.EnsureSuccessStatusCode();

        return name;
    }
}
