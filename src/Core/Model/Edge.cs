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
}
