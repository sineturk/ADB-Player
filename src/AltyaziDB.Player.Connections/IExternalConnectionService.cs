using AltyaziDB.Player.Connections.Models;

namespace AltyaziDB.Player.Connections;

public interface IExternalConnectionService : IDisposable
{
    Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<ConnectionConfiguration?> GetConfigurationAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestDraftAsync(SaveConnectionRequest request, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ConnectionProfile> SaveAsync(SaveConnectionRequest request, CancellationToken cancellationToken = default);
    Task RemoveAsync(string profileId, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(string profileId, bool enabled, CancellationToken cancellationToken = default);
    Task<ExternalSearchResult> SearchAsync(ExternalSearchRequest request, CancellationToken cancellationToken = default);
    Task<string?> ResolveMagnetAsync(ExternalRelease release, CancellationToken cancellationToken = default);
    Task<string> DownloadTorrentAsync(ExternalRelease release, string targetDirectory, CancellationToken cancellationToken = default);
}
