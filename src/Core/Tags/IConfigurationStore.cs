using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tags;

/// <summary>
/// Read access to the configured hierarchy: Tenant → Site → Device → Tag
/// (ADR-0001, ADR-0004).
/// </summary>
public interface IConfigurationStore
{
    Task<Tenant> GetTenantAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Site>> GetSitesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Folder>> GetFoldersAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Edge>> GetEdgesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AlarmDefinition>> GetAlarmDefinitionsAsync(CancellationToken cancellationToken);
}
