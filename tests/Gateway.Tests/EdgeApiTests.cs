using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScadaDarbox.Gateway.Tests.Hosting;
using ScadaDarbox.Persistence.TimescaleDb;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The edges of the tenant and which devices they read, through the API (ADR-0019). An edge is
/// tenant-scoped rather than Site-scoped — its name is the identity in its certificate — so the
/// resource is Admin-only, and assigning a device is an ordinary edit of that device.
/// </summary>
public sealed class EdgeApiTests : IClassFixture<GatewayTestHost>
{
    private static readonly Guid Bitola = DemoConfigurationSeeder.SecondSiteId;

    private readonly GatewayTestHost _host;

    public EdgeApiTests(GatewayTestHost host) => _host = host;

    [RequiresDatabaseFact]
    public async Task An_edge_is_created_listed_renamed_and_deleted_and_every_change_is_journalled()
    {
        var admin = await _host.LoginAsAdminAsync();
        var link = await _host.CreateLiveDeviceAsync(admin, Bitola, "API edge link", FakePushingDriverFactory.Key, scanIntervalMs: null);

        using var client = _host.CreateClient(admin);

        using var created = await client.PostAsJsonAsync(
            "/api/edges", new { name = "Boiler House", linkDeviceId = link.DeviceId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var edgeId = await created.Content.ReadFromJsonAsync<Guid>();

        // Listed with the device carrying its link, so an operator can see what is not typed here.
        var listed = await EdgeAsync(client, edgeId);
        Assert.Equal("Boiler House", listed.GetProperty("name").GetString());
        Assert.Equal(link.DeviceId, listed.GetProperty("linkDeviceId").GetGuid());

        using var renamed = await client.PutAsJsonAsync(
            $"/api/edges/{edgeId}", new { name = "Boiler House North", linkDeviceId = link.DeviceId });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal("Boiler House North", (await EdgeAsync(client, edgeId)).GetProperty("name").GetString());

        using var deleted = await client.DeleteAsync($"/api/edges/{edgeId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(JsonValueKind.Undefined, (await EdgeOrNullAsync(client, edgeId)).ValueKind);

        // Every one of the three is a configuration change, so every one is recorded (ADR-0011).
        Assert.NotNull(Assert.Single(await _host.AuditActorsAsync("edge.create", edgeId)));
        Assert.NotNull(Assert.Single(await _host.AuditActorsAsync("edge.update", edgeId)));
        Assert.NotNull(Assert.Single(await _host.AuditActorsAsync("edge.delete", edgeId)));
    }

    [RequiresDatabaseFact]
    public async Task Assigning_a_device_to_an_edge_and_releasing_it_are_edits_of_that_device()
    {
        var admin = await _host.LoginAsAdminAsync();
        var link = await _host.CreateLiveDeviceAsync(admin, Bitola, "Assignment link", FakePushingDriverFactory.Key, scanIntervalMs: null);
        var edgeId = await CreateEdgeAsync(admin, "Assignment edge", link.DeviceId);
        var deviceId = await CreateDeviceAsync(admin, Bitola, "Assigned pump");

        // The edge reports what it reads, and a device it has never been given is not among them.
        Assert.False(await IsReadByEdgeAsync(admin, edgeId, deviceId));

        using var client = _host.CreateClient(admin);
        using var assigned = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}", Save("Assigned pump", edgeId: edgeId));
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        Assert.True(await IsReadByEdgeAsync(admin, edgeId, deviceId));

        // Releasing is the same edit with no edge, and puts the device back in the Gateway's hands.
        using var released = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}", Save("Assigned pump", edgeId: null));
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.False(await IsReadByEdgeAsync(admin, edgeId, deviceId));
    }

    [RequiresDatabaseFact]
    public async Task An_assignment_is_refused_by_name_while_the_edge_has_no_link_device()
    {
        // The Gateway stops polling a device an edge reads (ADR-0019), so an assignment before
        // there is a link would leave its tags with no source at all — and the refusal has to say
        // which edge is at fault rather than that something failed.
        var admin = await _host.LoginAsAdminAsync();
        var edgeId = await CreateEdgeAsync(admin, "Linkless edge", linkDeviceId: null);
        var deviceId = await CreateDeviceAsync(admin, Bitola, "Unlinkable pump");

        using var client = _host.CreateClient(admin);
        using var refused = await client.PutAsJsonAsync(
            $"/api/sites/{Bitola}/devices/{deviceId}", Save("Unlinkable pump", edgeId: edgeId));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Linkless edge", message);
        Assert.Contains("no link device", message);
        Assert.False(await IsReadByEdgeAsync(admin, edgeId, deviceId));
    }

    [RequiresDatabaseFact]
    public async Task A_device_created_already_assigned_is_refused_the_same_way()
    {
        // Creating and assigning in one request is the same state as assigning afterwards, so it
        // is guarded by the same rule rather than being a way around it.
        var admin = await _host.LoginAsAdminAsync();
        var edgeId = await CreateEdgeAsync(admin, "Linkless create edge", linkDeviceId: null);

        using var client = _host.CreateClient(admin);
        using var refused = await client.PostAsJsonAsync(
            $"/api/sites/{Bitola}/devices",
            new
            {
                name = "Born assigned pump",
                driverKey = FakeDriverFactory.Key,
                connectionSettings = new Dictionary<string, string>(),
                scanIntervalMs = 200,
                folderId = (Guid?)null,
                edgeId,
            });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("no link device", await refused.Content.ReadAsStringAsync());

        // Nothing was written: a refused create must not leave the device behind unassigned.
        var tree = await client.GetFromJsonAsync<JsonElement>($"/api/sites/{Bitola}/tree");
        Assert.DoesNotContain(
            tree.GetProperty("devices").EnumerateArray(),
            device => device.GetProperty("name").GetString() == "Born assigned pump");
    }

    [RequiresDatabaseFact]
    public async Task A_link_device_has_to_be_one_that_pushes_and_is_still_configured()
    {
        // A link is the Gateway's end of the connection the edge already holds, so a device the
        // Gateway polls is not one (ADR-0016); and a device that is gone cannot be one either.
        var admin = await _host.LoginAsAdminAsync();
        var polled = await _host.CreateLiveDeviceAsync(admin, Bitola, "Polled not a link");

        using var client = _host.CreateClient(admin);

        using var polledRefused = await client.PostAsJsonAsync(
            "/api/edges", new { name = "Polled link edge", linkDeviceId = polled.DeviceId });
        Assert.Equal(HttpStatusCode.BadRequest, polledRefused.StatusCode);
        Assert.Contains("polled", await polledRefused.Content.ReadAsStringAsync());

        using var missingRefused = await client.PostAsJsonAsync(
            "/api/edges", new { name = "Missing link edge", linkDeviceId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, missingRefused.StatusCode);
        Assert.Contains("no such device", await missingRefused.Content.ReadAsStringAsync());

        // An edge with no link at all is allowed — it is what an operator creates first.
        var edgeId = await CreateEdgeAsync(admin, "Linkless but valid", linkDeviceId: null);
        Assert.Equal(JsonValueKind.Null, (await EdgeAsync(client, edgeId)).GetProperty("linkDeviceId").ValueKind);
    }

    [RequiresDatabaseFact]
    public async Task The_edges_api_is_admin_only()
    {
        // An edge is scoped to the tenant, not a Site, so there is no Site to answer against and
        // no not-found that would tell a lesser caller less than a refusal does (ADR-0011).
        var admin = await _host.LoginAsAdminAsync();
        var viewer = await _host.CreateUserAsync(admin, (Bitola, "Viewer"));

        using var asViewer = _host.CreateClient(viewer.Token);

        using var read = await asViewer.GetAsync("/api/edges");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        using var write = await asViewer.PostAsJsonAsync("/api/edges", new { name = "Not mine", linkDeviceId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        using var anonymous = _host.CreateClient();
        using var signedOut = await anonymous.GetAsync("/api/edges");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    /// <summary>Whether the edge reports the device among the ones it reads.</summary>
    private async Task<bool> IsReadByEdgeAsync(string adminToken, Guid edgeId, Guid deviceId)
    {
        using var client = _host.CreateClient(adminToken);
        var edge = await EdgeAsync(client, edgeId);

        return edge.GetProperty("deviceIds").EnumerateArray()
            .Any(candidate => candidate.GetGuid() == deviceId);
    }

    private async Task<Guid> CreateEdgeAsync(string adminToken, string name, Guid? linkDeviceId)
    {
        using var client = _host.CreateClient(adminToken);
        using var created = await client.PostAsJsonAsync("/api/edges", new { name, linkDeviceId });
        created.EnsureSuccessStatusCode();

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<Guid> CreateDeviceAsync(string adminToken, Guid siteId, string name)
    {
        using var client = _host.CreateClient(adminToken);
        using var created = await client.PostAsJsonAsync($"/api/sites/{siteId}/devices", Save(name));
        created.EnsureSuccessStatusCode();

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private static object Save(string name, Guid? edgeId = null) => new
    {
        name,
        driverKey = FakeDriverFactory.Key,
        connectionSettings = new Dictionary<string, string>(),
        scanIntervalMs = 200,
        folderId = (Guid?)null,
        edgeId,
    };

    private static async Task<JsonElement> EdgeAsync(HttpClient client, Guid edgeId)
    {
        var edge = await EdgeOrNullAsync(client, edgeId);
        return edge.ValueKind == JsonValueKind.Undefined
            ? throw new InvalidOperationException($"Edge {edgeId} is not in the list.")
            : edge;
    }

    private static async Task<JsonElement> EdgeOrNullAsync(HttpClient client, Guid edgeId)
    {
        var edges = await client.GetFromJsonAsync<JsonElement>("/api/edges");
        return edges.EnumerateArray()
            .FirstOrDefault(edge => edge.GetProperty("id").GetGuid() == edgeId);
    }
}
