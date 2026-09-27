namespace ScadaDarbox.Core.Model;

/// <summary>
/// A source of tags with its own connection configuration (ADR-0001). Driver and
/// connection settings live here — never on a folder, and never inferred from a
/// tag's position in the browse tree.
/// </summary>
public sealed class Device
{
    public required Guid Id { get; init; }

    /// <summary>Owning site, and through it the tenant (ADR-0001, ADR-0004).</summary>
    public required Guid SiteId { get; init; }

    /// <summary>
    /// Folder this device appears in, or null when it sits directly under its site.
    /// Purely organisational (ADR-0001 §6) — the security and tenant scope is
    /// <see cref="SiteId"/>, never this. The database enforces that a folder named here
    /// belongs to the same site as the device.
    /// </summary>
    public Guid? FolderId { get; set; }

    /// <summary>Display name. Mutable and identity-neutral.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Which driver module serves this device, e.g. <c>modbus-tcp</c>. An opaque key to
    /// core: the tag engine never interprets it, and core carries no knowledge of any
    /// specific protocol (ADR-0002).
    /// </summary>
    public required string DriverKey { get; init; }

    /// <summary>
    /// Driver-specific connection settings, opaque to core. Each driver module defines
    /// and validates its own keys.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConnectionSettings { get; init; } =
        new Dictionary<string, string>();

    /// <summary>How often the driver polls this device's tags.</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The template this device was instantiated from, or null for a device configured
    /// directly. The reference is live (ADR-0010): editing the template changes this
    /// device's tags.
    /// </summary>
    public Guid? TemplateId { get; set; }

    /// <summary>
    /// What makes this instance differ from its siblings — the values its template's
    /// address placeholders resolve to. Opaque named strings as far as core is concerned.
    /// </summary>
    public IReadOnlyDictionary<string, string> TemplateParameters { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// The edge that acquires this device, or null when the Gateway polls it itself (ADR-0019).
    /// A device belongs to at most one edge. When this is set the device's driver, settings and
    /// tag addresses travel to that edge, which reads them and sends the values over the link —
    /// the Gateway does not poll it.
    /// </summary>
    public Guid? EdgeId { get; set; }
}
