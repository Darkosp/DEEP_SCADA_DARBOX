namespace ScadaDarbox.Core.Model;

/// <summary>
/// A plant-side agent that acquires devices and ships their samples to the cloud over the
/// edge-to-cloud link (ADR-0017), and whose configuration the cloud derives from the devices
/// assigned to it and delivers over that same link (ADR-0019).
/// </summary>
/// <remarks>
/// Its name is its identity, not just a label: it is the name in the edge's client certificate
/// and the segment the broker carries its topics under (ADR-0017), so it is unique across the
/// tenant and cannot be display-only the way a folder or device name is.
/// </remarks>
public sealed class Edge
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Owning tenant (ADR-0004). An edge hangs off the tenant rather than a site, because its
    /// name is the identity in its certificate and must be unique across the whole deployment —
    /// which uniqueness within one site would not give.
    /// </summary>
    public required Guid TenantId { get; init; }

    /// <summary>Identity and display name in one. Unique within the tenant, ignoring case.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Gateway device that carries this edge's link — the pushing device subscribing to the
    /// topics the edge publishes under (ADR-0016, ADR-0017) — or null while the edge has no link
    /// yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The link is named here rather than by each device the edge reads, because one link is
    /// either silent or not, whatever it carries. That is also where the staleness limit lives:
    /// it is a property of the transport (ADR-0016), and for a device an edge reads the transport
    /// is this link — the Gateway never opens a connection to the device itself.
    /// </para>
    /// <para>
    /// An edge with no link device may have no devices assigned to it. A device an edge reads is
    /// not polled by the Gateway, so assigning one before there is a link to carry its tags would
    /// leave them with no source at all; the repository refuses that, by name, rather than
    /// leaving the system in a state where nothing reads them and nothing says so.
    /// </para>
    /// </remarks>
    public Guid? LinkDeviceId { get; set; }
}
