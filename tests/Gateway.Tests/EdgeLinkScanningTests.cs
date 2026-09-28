using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0019 §3 through the running Gateway: a device assigned to an edge is acquired by that
/// edge, not by the Gateway. The Gateway starts no scan loop for it, and the link the edge names
/// carries its tags — which is the only thing feeding them, and the reason an assignment without
/// a link is refused.
/// </summary>
/// <remarks>
/// The two halves are one change, so one test covers both. Dropping the assignment filter leaves
/// the stand-in poller's counter on the tag for ever; not handing the link the assigned device's
/// tags leaves the sample it sends with no tag to be accepted for.
/// </remarks>
public sealed class EdgeLinkScanningTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public EdgeLinkScanningTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task An_assigned_device_is_not_polled_and_its_tag_arrives_over_the_edges_link()
    {
        var admin = await _host.LoginAsAdminAsync();
        using var client = _host.CreateClient(admin);

        // The link the edge names: a pushing source that hands over one sample per tag it was
        // given, then goes silent. It is started with its own tag to begin with, and the
        // assignment below is what widens the list it is handed.
        var link = await _host.CreateLiveDeviceAsync(
            admin, Bitola, "Edge link probe", FakePushingDriverFactory.Key, scanIntervalMs: null);

        // Read by the Gateway itself until it is assigned — by a stand-in that counts.
        var plant = await _host.CreateLiveDeviceAsync(admin, Bitola, "Pump House probe");

        // A device that stays the Gateway's, so "it went Bad" is the assignment and not a scan
        // loop that stopped for some other reason.
        var gatewayOwned = await _host.CreateLiveDeviceAsync(admin, Bitola, "Boiler House probe");

        await AssignAsync(plant.DeviceId, link.DeviceId);

        // The link was started with the plant device's tag among those it carries — a pushing
        // driver is handed only the tags it may accept samples for — so the one sample it sends
        // is accepted for that tag and reaches the history. The stand-in poller never produces
        // 7.5: it counts.
        await GatewayTestHost.WaitUntilAsync(
            async () => await HistoryHasValueAsync(client, plant.TagId, FakePushingDriverFactory.FirstValue),
            "the plant device's tag to arrive over the edge's link");

        // And nothing polls it any more: the counter would otherwise keep it Good for ever, where
        // the link fell silent and the tag reads Bad at the time of the last thing measured.
        await GatewayTestHost.WaitUntilAsync(
            async () => await QualityOfAsync(client, plant.TagId) == "Bad",
            "the assigned device's tag to read Bad once the link is silent",
            TimeSpan.FromSeconds(10));

        // The control: a device the Gateway still owns is untouched by any of this.
        Assert.Equal("Good", await QualityOfAsync(client, gatewayOwned.TagId));
    }

    /// <summary>
    /// Assigns a device to a new edge through the API an operator uses: create the edge naming the
    /// device that carries its link, then edit the device to name that edge (ADR-0019).
    /// </summary>
    private async Task AssignAsync(Guid deviceId, Guid linkDeviceId)
    {
        using var client = _host.CreateClient(await _host.LoginAsAdminAsync());

        using var created = await client.PostAsJsonAsync(
            "/api/edges", new { name = $"edge-{Guid.NewGuid():N}", linkDeviceId });
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
    }

    private static async Task<string?> QualityOfAsync(HttpClient client, Guid tagId)
    {
        using var response = await client.GetAsync($"/api/tags/{tagId}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var tag = await response.Content.ReadFromJsonAsync<JsonElement>();
        return tag.GetProperty("quality").GetString();
    }

    private static async Task<bool> HistoryHasValueAsync(HttpClient client, Guid tagId, double value)
    {
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
        using var response = await client.GetAsync($"/api/tags/{tagId}/history?from={from}");
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var history = await response.Content.ReadFromJsonAsync<JsonElement>();
        return history.GetProperty("samples").EnumerateArray().Any(sample =>
            sample.GetProperty("value").GetProperty("kind").GetString() == "numeric"
            && sample.GetProperty("value").GetProperty("numeric").GetDouble() == value);
    }
}
