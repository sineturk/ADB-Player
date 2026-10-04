using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface IRcloneCloudService : IAsyncDisposable
{
    Task<RcloneRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<string> ConnectAsync(
        RcloneCloudProvider provider,
        string displayName,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(
        string remoteName,
        CancellationToken cancellationToken = default);

    Task<RemoteBrowseResult> BrowseAsync(
        SavedCloudAccount account,
        string? path = null,
        CancellationToken cancellationToken = default);

    Task<RemoteOpenRequest> CreateOpenRequestAsync(
        SavedCloudAccount account,
        RemoteSourceItem item,
        CancellationToken cancellationToken = default);

    Task DownloadToFileAsync(
        SavedCloudAccount account,
        RemoteSourceItem item,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
