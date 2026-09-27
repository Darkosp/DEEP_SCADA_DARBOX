using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// An immutable in-memory projection of the configured hierarchy, used to resolve a
/// tag's identity to its device, its unit, and its display path.
/// </summary>
/// <remarks>
/// The path is computed here from the current entity names — it is never stored
/// alongside a tag and never used as a key (ADR-0001). Rebuilding this catalog after a
/// rename changes only what the operator sees.
/// </remarks>
public sealed class TagCatalog
{
    /// <summary>
    /// How deep a folder chain may be walked while building a display path. Folder
    /// nesting is deliberately unbounded (ADR-0001 §4), so this is not a modelling
    /// limit — it stops path building from hanging if a parent cycle ever reaches the
    /// database, which the composite foreign keys do not prevent.
    /// </summary>
    private const int MaxFolderDepth = 64;

    private readonly Dictionary<Guid, Tag> _tagsById;
    private readonly Dictionary<Guid, Device> _devicesById;
    private readonly Dictionary<Guid, Site> _sitesById;
    private readonly Dictionary<Guid, Folder> _foldersById;
    private readonly Dictionary<Guid, Edge> _edgesById;
    private readonly Dictionary<Guid, string> _pathsByTagId;
    private readonly Dictionary<Guid, List<AlarmDefinition>> _alarmsByTagId;

    public TagCatalog(
        Tenant tenant,
        IReadOnlyList<Site> sites,
        IReadOnlyList<Folder> folders,
        IReadOnlyList<Device> devices,
        IReadOnlyList<Tag> tags,
        IReadOnlyList<AlarmDefinition>? alarms = null,
        IReadOnlyList<Edge>? edges = null)
    {
        Tenant = tenant;
        _sitesById = sites.ToDictionary(s => s.Id);
        _foldersById = folders.ToDictionary(f => f.Id);
        _devicesById = devices.ToDictionary(d => d.Id);
        _tagsById = tags.ToDictionary(t => t.Id);

        // Edges are optional so that a caller with no edges to speak of — most tests, and any
        // catalogue built before an edge was configured — need not name the empty list.
        _edgesById = (edges ?? []).ToDictionary(e => e.Id);

        _alarmsByTagId = (alarms ?? [])
            .GroupBy(alarm => alarm.TagId)
            .ToDictionary(group => group.Key, group => group.ToList());

        _pathsByTagId = new Dictionary<Guid, string>(tags.Count);
        foreach (var tag in tags)
        {
            _pathsByTagId[tag.Id] = BuildPath(tag);
        }
    }

    public Tenant Tenant { get; }

    public IReadOnlyCollection<Tag> Tags => _tagsById.Values;

    public IReadOnlyCollection<Device> Devices => _devicesById.Values;

    public IReadOnlyCollection<Folder> Folders => _foldersById.Values;

    public IReadOnlyCollection<Site> Sites => _sitesById.Values;

    public IReadOnlyCollection<Edge> Edges => _edgesById.Values;

    public Tag? FindTag(Guid tagId) => _tagsById.GetValueOrDefault(tagId);

    public Device? FindDevice(Guid deviceId) => _devicesById.GetValueOrDefault(deviceId);

    public Edge? FindEdge(Guid edgeId) => _edgesById.GetValueOrDefault(edgeId);

    /// <summary>
    /// The edge that acquires a device, or null when the Gateway polls it itself (ADR-0019).
    /// </summary>
    public Edge? EdgeOfDevice(Guid deviceId) =>
        _devicesById.TryGetValue(deviceId, out var device) && device.EdgeId is { } edgeId
            ? _edgesById.GetValueOrDefault(edgeId)
            : null;

    public Folder? FindFolder(Guid folderId) => _foldersById.GetValueOrDefault(folderId);

    /// <summary>Every configured alarm, across all tags.</summary>
    public IReadOnlyCollection<AlarmDefinition> Alarms =>
        _alarmsByTagId.Values.SelectMany(list => list).ToList();

    /// <summary>
    /// The alarm conditions watching one tag. Empty for a tag with none, which is the
    /// common case, so the alarm engine can ask about every value cheaply.
    /// </summary>
    public IReadOnlyList<AlarmDefinition> AlarmsOfTag(Guid tagId) =>
        _alarmsByTagId.TryGetValue(tagId, out var alarms) ? alarms : [];

    /// <summary>Tags belonging to one device, in configuration order.</summary>
    public IReadOnlyList<Tag> TagsOfDevice(Guid deviceId) =>
        _tagsById.Values.Where(t => t.DeviceId == deviceId).ToList();

    /// <summary>
    /// The derived display path for a tag, e.g. <c>Skopje/Pump House/Discharge Pressure</c>,
    /// with any folders between site and device included. Presentation only.
    /// </summary>
    public string PathOf(Guid tagId) => _pathsByTagId.GetValueOrDefault(tagId, "<unknown>");

    /// <summary>
    /// The derived display path for a device, e.g. <c>Skopje/Pump House</c>. Presentation only.
    /// </summary>
    public string DevicePathOf(Guid deviceId) =>
        _devicesById.TryGetValue(deviceId, out var device) ? string.Join('/', DeviceSegments(device)) : "<unknown>";

    private string BuildPath(Tag tag)
    {
        if (!_devicesById.TryGetValue(tag.DeviceId, out var device))
        {
            return tag.Name;
        }

        var segments = DeviceSegments(device);
        segments.Add(tag.Name);

        return string.Join('/', segments);
    }

    private List<string> DeviceSegments(Device device)
    {
        var segments = new List<string>();

        if (_sitesById.TryGetValue(device.SiteId, out var site))
        {
            segments.Add(site.Name);
        }

        segments.AddRange(FolderNamesFromSite(device.FolderId));
        segments.Add(device.Name);
        return segments;
    }

    /// <summary>
    /// The names of the folder chain from the site down to <paramref name="folderId"/>.
    /// </summary>
    private List<string> FolderNamesFromSite(Guid? folderId)
    {
        var names = new List<string>();
        var seen = new HashSet<Guid>();
        var current = folderId;

        while (current is { } id && names.Count < MaxFolderDepth)
        {
            // A folder that is its own ancestor would otherwise loop forever. The
            // constraint that prevents cross-site parents cannot express "no cycles",
            // so reading has to be able to survive one.
            if (!seen.Add(id) || !_foldersById.TryGetValue(id, out var folder))
            {
                break;
            }

            names.Add(folder.Name);
            current = folder.ParentFolderId;
        }

        // Walked child-to-parent; the path reads parent-to-child.
        names.Reverse();
        return names;
    }
}
