using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface IRemoteSourceService : IDisposable
{
    Task<RemoteBrowseResult> ResolvePublicLinkAsync(
        string url,
        CancellationToken cancellationToken = default);

    Task<RemoteBrowseResult> BrowseWebDavAsync(
        WebDavConnection connection,
        string? path = null,
        CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(
        RemoteSourceItem item,
        CancellationToken cancellationToken = default);

    Task DownloadToFileAsync(
        RemoteSourceItem item,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
