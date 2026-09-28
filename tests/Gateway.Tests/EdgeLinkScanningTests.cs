using System.Net.Http.Json;
using System.Text.Json;
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
    /// Assigns a device to a new edge the way a later slice's <c>/api/edges</c> will: through the
    /// repository, then a catalogue reload. No endpoint does this yet — ADR-0019 leaves it to a
    /// later slice — so the test writes the row through the same repository that slice will use
    /// rather than through SQL of its own.
    /// </summary>
    private async Task<Edge> AssignAsync(Guid deviceId, Guid linkDeviceId)
    {
        var dataSource = _host.Services.GetRequiredService<NpgsqlDataSource>();
        var tenant = await _host.Services.GetRequiredService<IConfigurationStore>()
            .GetTenantAsync(CancellationToken.None);

        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"edge-{Guid.NewGuid():N}",
            LinkDeviceId = linkDeviceId,
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
