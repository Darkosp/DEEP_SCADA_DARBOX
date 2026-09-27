using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// ADR-0019: a device assigned to an edge is acquired by that edge, not by the Gateway, so the
/// link that edge names is the only thing feeding its tags. The catalogue is where "what does
/// this device deliver" is answered, and for a link that answer is wider than its own tags —
/// which is also what the link's silence watch covers.
/// </summary>
public class EdgeLinkCatalogTests
{
    [Fact]
    public void The_edge_that_reads_a_device_is_named_and_a_device_the_Gateway_polls_has_none()
    {
        var world = new LinkWorld();

        Assert.Equal(world.Edge.Id, world.Catalog.EdgeOfDevice(world.PlantDevice.Id)!.Id);
        Assert.Null(world.Catalog.EdgeOfDevice(world.LinkDevice.Id));
        Assert.Null(world.Catalog.EdgeOfDevice(world.GatewayDevice.Id));
    }

    [Fact]
    public void A_link_device_carries_its_own_tags_and_those_of_the_devices_assigned_to_its_edge()
    {
        var world = new LinkWorld();

        var carried = world.Catalog.TagsCarriedBy(world.LinkDevice.Id).Select(tag => tag.Id);

        Assert.Equal(new[] { world.LinkTag.Id, world.PlantTag.Id }.Order(), carried.Order());
    }

    [Fact]
    public void A_device_the_Gateway_polls_carries_only_its_own_tags()
    {
        var world = new LinkWorld();

        Assert.Equal(
            new[] { world.GatewayTag.Id },
            world.Catalog.TagsCarriedBy(world.GatewayDevice.Id).Select(tag => tag.Id));
    }

    [Fact]
    public void A_device_assigned_to_an_edge_with_no_link_device_is_carried_by_nobody()
    {
        // The repositories refuse to build this state (ADR-0019); this is the reading side's own
        // guarantee if one ever reaches it — an edge with no link carries nothing beyond what the
        // device it names delivers itself, rather than the assignment being read as though it had
        // never been made.
        var world = new LinkWorld(hasLink: false);

        Assert.Equal(world.Edge.Id, world.Catalog.EdgeOfDevice(world.PlantDevice.Id)!.Id);
        Assert.Equal(
            new[] { world.LinkTag.Id },
            world.Catalog.TagsCarriedBy(world.LinkDevice.Id).Select(tag => tag.Id));
    }

    [Fact]
    public void One_link_device_carries_the_tags_of_every_edge_that_names_it()
    {
        // A single subscription can carry several edges (ADR-0017), so a link's list accumulates
        // rather than one edge's assignment replacing another's.
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Darbo" };
        var site = new Site { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Skopje" };

        var link = Device(site.Id, "Edge link", "fake-push");
        var linkTag = Tag(link.Id, "Link status");
        var first = Device(site.Id, "Pump House", "modbus-tcp");
        var firstTag = Tag(first.Id, "Discharge Pressure");
        var second = Device(site.Id, "Boiler House", "modbus-tcp");
        var secondTag = Tag(second.Id, "Steam Pressure");

        var firstEdge = new Edge { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "first", LinkDeviceId = link.Id };
        var secondEdge = new Edge { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "second", LinkDeviceId = link.Id };
        first.EdgeId = firstEdge.Id;
        second.EdgeId = secondEdge.Id;

        var catalog = new TagCatalog(
            tenant,
            [site],
            [],
            [link, first, second],
            [linkTag, firstTag, secondTag],
            alarms: null,
            edges: [firstEdge, secondEdge]);

        Assert.Equal(
            new[] { linkTag.Id, firstTag.Id, secondTag.Id }.Order(),
            catalog.TagsCarriedBy(link.Id).Select(tag => tag.Id).Order());
    }

    private static Device Device(Guid siteId, string name, string driverKey) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = siteId,
        Name = name,
        DriverKey = driverKey,
    };

    private static Tag Tag(Guid deviceId, string name) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        Name = name,
        ValueKind = TagValueKind.Numeric,
        SourceAddress = "holding:0",
    };

    /// <summary>A link device, a plant device assigned to an edge, and a device the Gateway polls.</summary>
    private sealed class LinkWorld
    {
        internal Tenant Tenant { get; } = new() { Id = Guid.NewGuid(), Name = "Darbo" };

        internal Site Site { get; }

        internal Edge Edge { get; }

        internal Device LinkDevice { get; }

        internal Tag LinkTag { get; }

        internal Device PlantDevice { get; }

        internal Tag PlantTag { get; }

        internal Device GatewayDevice { get; }

        internal Tag GatewayTag { get; }

        internal TagCatalog Catalog { get; }

        /// <param name="hasLink">
        /// Whether the edge names a link device. False is the edge-before-its-link state, which
        /// the repositories refuse to assign a device into.
        /// </param>
        internal LinkWorld(bool hasLink = true)
        {
            Site = new Site { Id = Guid.NewGuid(), TenantId = Tenant.Id, Name = "Skopje" };

            LinkDevice = Device(Site.Id, "Edge link", "fake-push");
            LinkTag = Tag(LinkDevice.Id, "Link status");
            PlantDevice = Device(Site.Id, "Pump House", "modbus-tcp");
            PlantTag = Tag(PlantDevice.Id, "Discharge Pressure");
            GatewayDevice = Device(Site.Id, "Boiler House", "modbus-tcp");
            GatewayTag = Tag(GatewayDevice.Id, "Flow");

            Edge = new Edge
            {
                Id = Guid.NewGuid(),
                TenantId = Tenant.Id,
                Name = "boiler-edge",
                LinkDeviceId = hasLink ? LinkDevice.Id : null,
            };

            PlantDevice.EdgeId = Edge.Id;

            Catalog = new TagCatalog(
                Tenant,
                [Site],
                [],
                [LinkDevice, PlantDevice, GatewayDevice],
                [LinkTag, PlantTag, GatewayTag],
                alarms: null,
                edges: [Edge]);
        }
    }
}
