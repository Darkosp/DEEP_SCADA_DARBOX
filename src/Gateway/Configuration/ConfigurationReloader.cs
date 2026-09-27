using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Rebuilds the tag catalogue from stored configuration and installs it.
/// </summary>
/// <remarks>
/// Called after every configuration write, so a device added through the UI is scanned
/// and browsable immediately rather than at the next gateway restart. The whole
/// hierarchy is re-read rather than patched: configuration is small, and a full rebuild
/// cannot drift from the database the way a series of in-place edits can.
/// </remarks>
public sealed class ConfigurationReloader
{
    private readonly IConfigurationStore _store;
    private readonly TagCatalogSource _catalogSource;

    public ConfigurationReloader(IConfigurationStore store, TagCatalogSource catalogSource)
    {
        _store = store;
        _catalogSource = catalogSource;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var catalog = await BuildAsync(_store, cancellationToken).ConfigureAwait(false);
        _catalogSource.Set(catalog);
    }

    /// <summary>Reads the whole hierarchy into a fresh catalogue.</summary>
    public static async Task<TagCatalog> BuildAsync(
        IConfigurationStore store,
        CancellationToken cancellationToken) =>
        new(
            await store.GetTenantAsync(cancellationToken).ConfigureAwait(false),
            await store.GetSitesAsync(cancellationToken).ConfigureAwait(false),
            await store.GetFoldersAsync(cancellationToken).ConfigureAwait(false),
            await store.GetDevicesAsync(cancellationToken).ConfigureAwait(false),
            await store.GetTagsAsync(cancellationToken).ConfigureAwait(false),
            await store.GetAlarmDefinitionsAsync(cancellationToken).ConfigureAwait(false),
            await store.GetEdgesAsync(cancellationToken).ConfigureAwait(false));
}
