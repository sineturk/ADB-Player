using AltyaziDB.Player.Addons.Models;

namespace AltyaziDB.Player.Addons;

public interface IAddonCatalogService : IDisposable
{
    Task<IReadOnlyList<AddonRegistration>> GetAddonsAsync(CancellationToken cancellationToken = default);

    Task<AddonRegistration> AddAddonAsync(
        string manifestUrl,
        bool allowLocalHttp,
        CancellationToken cancellationToken = default);

    Task<AddonRegistration> RestoreAddonAsync(
        string manifestUrl,
        bool allowLocalHttp,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task RemoveAddonAsync(string addonId, CancellationToken cancellationToken = default);

    Task SetAddonEnabledAsync(
        string addonId,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<AddonCatalogSnapshot> GetLatestAsync(
        CatalogContentType contentType = CatalogContentType.All,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogEpisode>> GetEpisodesAsync(
        CatalogItem item,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogStream>> GetStreamsAsync(
        CatalogItem item,
        CatalogEpisode? episode = null,
        CancellationToken cancellationToken = default);

    Task<string> DownloadTorrentAsync(
        CatalogStream stream,
        string targetDirectory,
        CancellationToken cancellationToken = default);
}
