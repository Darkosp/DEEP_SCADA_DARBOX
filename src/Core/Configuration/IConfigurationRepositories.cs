using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Templates;

namespace ScadaDarbox.Core.Configuration;

/// <summary>
/// Reads and writes the folder tree of one site (ADR-0001 §4).
/// </summary>
/// <remarks>
/// Every read here returns live configuration only. Deletion is soft (ADR-0009): the row
/// survives so a historian sample can still resolve a name, but it is gone from every
/// browsing and lookup path.
/// </remarks>
public interface IFolderRepository
{
    Task<IReadOnlyList<Folder>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task AddAsync(Folder folder, CancellationToken cancellationToken);

    /// <summary>
    /// Renames a folder and/or moves it under a different parent.
    /// </summary>
    /// <exception cref="ConfigurationConflictException">
    /// The move would make the folder its own ancestor. The composite foreign keys
    /// cannot express that, so it is checked here.
    /// </exception>
    Task UpdateAsync(Folder folder, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes an empty folder.
    /// </summary>
    /// <exception cref="ConfigurationConflictException">
    /// The folder still holds a live child folder or device. There is deliberately no
    /// cascade and no implicit reparenting: a position in the browse tree must never
    /// change as a side effect of something else (ADR-0001 §6), so the operator moves
    /// the contents out explicitly first.
    /// </exception>
    Task DeleteAsync(Guid folderId, CancellationToken cancellationToken);
}

/// <summary>Reads and writes device configuration.</summary>
public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken);

    Task AddAsync(Device device, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a device, including which edge acquires it — the only way the assignment ever
    /// changes (ADR-0019).
    /// </summary>
    /// <exception cref="ConfigurationConflictException">
    /// The device was assigned to an edge that has no link device, or a device that carries an
    /// edge's link was assigned to an edge. A device an edge reads is not polled by the Gateway,
    /// so the link is the only thing feeding its tags; an assignment with no link would leave
    /// them with no source at all, and nothing would say so.
    /// </exception>
    Task UpdateAsync(Device device, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes a device and, in the same transaction, the tags it owns.
    /// </summary>
    /// <remarks>
    /// Unlike a folder, a device does cascade. A tag has no placement of its own to
    /// preserve — its device owns it (ADR-0001 §3) — so there is nothing an operator
    /// could usefully do with the tags first, and no choice worth forcing them to make.
    /// </remarks>
    /// <exception cref="ConfigurationConflictException">
    /// The device carries an edge's link (ADR-0019). Deleting it would leave the tags of every
    /// device assigned to that edge with no source, so the edge is repointed first, explicitly.
    /// </exception>
    Task DeleteAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes the edges of the tenant (ADR-0019).
/// </summary>
/// <remarks>
/// An edge's name is its identity — the name in its certificate and the segment the broker
/// carries its topics under (ADR-0017) — so it is unique across the tenant rather than within a
/// site, and it is not display-only the way a folder's or a device's name is. Deletion is soft
/// (ADR-0009), like every other configuration entity.
/// </remarks>
public interface IEdgeRepository
{
    Task<IReadOnlyList<Edge>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(Edge edge, CancellationToken cancellationToken);

    /// <summary>
    /// Renames an edge and, when no device is assigned to it, sets which device carries its link.
    /// </summary>
    /// <exception cref="ConfigurationConflictException">
    /// The link was being cleared or repointed while a live device is still assigned to this
    /// edge. A device an edge reads is not polled by the Gateway, so that link is the only thing
    /// feeding its tags; moving it would leave them with no source at all. The operator unassigns
    /// the devices explicitly first.
    /// </exception>
    Task UpdateAsync(Edge edge, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes an edge that no live device is assigned to.
    /// </summary>
    /// <exception cref="ConfigurationConflictException">
    /// A live device is still assigned to this edge. There is deliberately no cascade and no
    /// implicit unassignment, the rule a folder already follows: what an edge reads must never
    /// change as a side effect of something else (ADR-0001 §6, ADR-0019), so the operator
    /// unassigns its devices explicitly first.
    /// </exception>
    Task DeleteAsync(Guid edgeId, CancellationToken cancellationToken);
}

/// <summary>
/// Device templates and the instances made from them (ADR-0010).
/// </summary>
/// <remarks>
/// Every operation that touches more than one row does so in a single transaction. A
/// template edit changes every instance, so a half-applied one would leave some devices
/// carrying the new shape and others the old, with nothing to say which — the kind of
/// quiet inconsistency the schema-level rules elsewhere exist to prevent.
/// </remarks>
public interface IDeviceTemplateRepository
{
    Task<IReadOnlyList<DeviceTemplate>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceTemplateTag>> GetTagsAsync(Guid templateId, CancellationToken cancellationToken);

    Task AddAsync(DeviceTemplate template, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a device from a template, materialising one real tag per template tag with
    /// its address resolved from this device's parameters (ADR-0001, ADR-0010).
    /// </summary>
    /// <exception cref="TemplateParameterMissingException">
    /// A template address names a parameter this device did not supply.
    /// </exception>
    Task InstantiateAsync(Device device, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a tag to a template and materialises it on every existing instance.
    /// </summary>
    /// <returns>How many instances gained a tag.</returns>
    Task<int> AddTagAsync(DeviceTemplateTag templateTag, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a tag from a template and soft-deletes the materialised tag on every
    /// instance (ADR-0009). Historian rows are untouched.
    /// </summary>
    /// <returns>How many instances lost a tag.</returns>
    Task<int> RemoveTagAsync(Guid templateTagId, CancellationToken cancellationToken);
}

/// <summary>Reads and writes the alarm conditions watching a tag.</summary>
public interface IAlarmDefinitionRepository
{
    Task<IReadOnlyList<AlarmDefinition>> GetByTagAsync(Guid tagId, CancellationToken cancellationToken);

    Task AddAsync(AlarmDefinition definition, CancellationToken cancellationToken);

    Task UpdateAsync(AlarmDefinition definition, CancellationToken cancellationToken);

    /// <summary>Soft-deletes one definition (ADR-0009).</summary>
    Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken);
}

/// <summary>
/// A tag's names as recorded, whether or not it is still live.
/// </summary>
/// <remarks>
/// Exists so history outlives configuration in a usable form (ADR-0001, ADR-0009): a
/// trend for a device retired last year should read as its name, not as a UUID.
/// </remarks>
/// <param name="SiteId">
/// The Site the tag belonged to, resolved past any deletion — what authorises reading its
/// history once it is no longer in the live catalogue (ADR-0011).
/// </param>
public sealed record TagIdentity(Guid TagId, string TagName, string DeviceName, Guid SiteId, bool IsDeleted);

/// <summary>Reads and writes the tags of a device.</summary>
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetByDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task AddAsync(Tag tag, CancellationToken cancellationToken);

    Task UpdateAsync(Tag tag, CancellationToken cancellationToken);

    /// <summary>Soft-deletes one tag.</summary>
    Task DeleteAsync(Guid tagId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a tag's name and its device's name even after either has been deleted.
    /// The one read path that deliberately looks past the active-row views.
    /// </summary>
    Task<TagIdentity?> FindIdentityIncludingDeletedAsync(Guid tagId, CancellationToken cancellationToken);
}
