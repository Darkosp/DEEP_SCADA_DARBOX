using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The Gateway publishing each edge's configuration (ADR-0019 §4): retained on that edge's own
/// topic, again when the content changes and not when anything else does, and emptied when the
/// edge is deleted.
/// </summary>
public sealed class EdgeConfigurationPublishingTests : IAsyncLifetime
{
    private static readonly Guid EdgeId = new("11111111-1111-4111-8111-111111111101");
    private static readonly Guid PumpId = new("22222222-2222-4222-8222-222222222201");
    private static readonly Guid PressureId = new("33333333-3333-4333-8333-333333333301");

    private int _port;
    private MqttServer? _broker;

    public async Task InitializeAsync()
    {
        var factory = new MqttServerFactory();

        // A port is taken by asking the OS for a free one, releasing it, and binding it a moment
        // later — a window another process on a loaded machine can win. That is how this class
        // failed once, in a whole-solution run, as "the first configuration was never published"
        // twenty seconds later, when what had gone wrong was the bind at the start. Retrying puts
        // the failure where it happens, naming the port, and leaves the publish alone.
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var broker = factory.CreateMqttServer(factory.CreateServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointPort(port)
                .Build());

            try
            {
                await broker.StartAsync();
                _port = port;
                _broker = broker;
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                // Something else took the port between the probe and this bind. Take another.
                broker.Dispose();
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }
    }

    [Fact]
    public async Task A_change_is_published_retained_to_that_edges_topic()
    {
        var source = new TagCatalogSource(Catalogue(assigned: false));
        using var publisher = Publisher(source);
        await publisher.StartAsync(CancellationToken.None);

        using var cloud = await SubscribeAsync("scada/edge/edge-a/config");
        await WaitUntilAsync(() => cloud.Count >= 1, "the first configuration to be published");

        // The edge's device is assigned: the cloud's configuration of it changes.
        source.Set(Catalogue(assigned: true));
        await WaitUntilAsync(() => cloud.Count >= 2, "the changed configuration to be published");

        // And it is retained: an edge that subscribes later — one that was off while this
        // happened — is given it the moment it asks (ADR-0019 §4).
        var late = await SubscribeAsync("scada/edge/edge-a/config", window: TimeSpan.FromSeconds(2));
        Assert.True(late.Retain, "the configuration an edge is given on subscribing must be retained");
        var read = EdgeConfigurationPayload.Read(late.Payload!);
        Assert.Null(read.Refusal);
        Assert.Equal(new[] { "Pump skid" }, read.Devices.Select(device => device.Name));

        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_configuration_that_has_not_changed_is_not_published_again()
    {
        // Otherwise every unrelated edit — a folder renamed, another device added — would restart
        // the acquisition of every edge (ADR-0019 §5).
        var source = new TagCatalogSource(Catalogue(assigned: true));
        using var publisher = Publisher(source);
        await publisher.StartAsync(CancellationToken.None);

        using var cloud = await SubscribeAsync("scada/edge/edge-a/config");
        await WaitUntilAsync(() => cloud.Count >= 1, "the first configuration to be published");
        var published = cloud.Count;

        source.Set(Catalogue(assigned: true));
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Equal(published, cloud.Count);

        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_edge_that_is_deleted_has_its_configuration_emptied()
    {
        // The topic keeps the name, so a later edge under that name must start reading nothing
        // rather than inherit the devices of the edge that used to answer to it.
        var source = new TagCatalogSource(Catalogue(assigned: true));
        using var publisher = Publisher(source);
        await publisher.StartAsync(CancellationToken.None);

        using var cloud = await SubscribeAsync("scada/edge/edge-a/config");
        await WaitUntilAsync(() => cloud.Count >= 1, "the first configuration to be published");

        source.Set(Catalogue(assigned: true, withEdge: false));
        await WaitUntilAsync(() => cloud.Count >= 2, "the edge's configuration to be emptied");

        var read = EdgeConfigurationPayload.Read(cloud.Payload!);
        Assert.Null(read.Refusal);
        Assert.Empty(read.Devices);

        await publisher.StopAsync(CancellationToken.None);
    }

    private EdgeConfigurationPublisher Publisher(TagCatalogSource source) => new(
        source,
        Options.Create(new EdgeProvisioningOptions
        {
            Host = "127.0.0.1",
            Port = _port,
            UsesTls = false,
        }),
        NullLogger<EdgeConfigurationPublisher>.Instance);

    /// <summary>A catalogue with this edge, and one device either assigned to it or not.</summary>
    private static TagCatalog Catalogue(bool assigned, bool withEdge = true)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Darbo" };
        var site = new Site { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Skopje" };
        var edge = new Edge { Id = EdgeId, TenantId = tenant.Id, Name = "edge-a" };
        var pump = new Device
        {
            Id = PumpId,
            SiteId = site.Id,
            Name = "Pump skid",
            DriverKey = "opc-ua",
            EdgeId = assigned ? EdgeId : null,
        };
        var pressure = new Tag
        {
            Id = PressureId,
            DeviceId = pump.Id,
            Name = "Discharge Pressure",
            ValueKind = TagValueKind.Numeric,
            SourceAddress = "ns=2;s=Pump1.Pressure",
        };

        return new TagCatalog(tenant, [site], [], [pump], [pressure], null, withEdge ? [edge] : []);
    }

    private async Task<Subscription> SubscribeAsync(string topic, TimeSpan? window = null)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var messages = new ConcurrentQueue<MqttApplicationMessage>();
        client.ApplicationMessageReceivedAsync += message =>
        {
            messages.Enqueue(message.ApplicationMessage);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _port)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .Build());
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        if (window is { } delay)
        {
            await Task.Delay(delay);
        }

        return new Subscription(client, messages);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(100);
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>An edge's side of the topic: everything it was sent, in order.</summary>
    private sealed class Subscription : IDisposable
    {
        private readonly IMqttClient _client;
        private readonly ConcurrentQueue<MqttApplicationMessage> _messages;

        internal Subscription(IMqttClient client, ConcurrentQueue<MqttApplicationMessage> messages)
        {
            _client = client;
            _messages = messages;
        }

        internal int Count => _messages.Count;

        internal MqttApplicationMessage Last => _messages.Last();

        /// <summary>Whether the last message arrived because it was retained, not because it was published.</summary>
        internal bool Retain => _messages.Last().Retain;

        internal string? Payload => _messages.Last().ConvertPayloadToString();

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
