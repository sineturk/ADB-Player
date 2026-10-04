using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface ITorrentService : IAsyncDisposable
{
    event EventHandler<TorrentSessionSnapshot>? SessionChanged;

    bool IsEngineAvailable { get; }
    string EngineDiagnostic { get; }

    Task InitializeAsync(
        TorrentEngineConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<TorrentSessionSnapshot> PrepareMagnetAsync(
        string magnetUri,
        CancellationToken cancellationToken = default);

    Task<TorrentSessionSnapshot> PrepareTorrentFileAsync(
        string torrentFilePath,
        CancellationToken cancellationToken = default);

    Task<TorrentPlaybackSource> StartSelectedFileAsync(
        string sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default);

    Task PauseAsync(string sessionId, CancellationToken cancellationToken = default);
    Task ResumeAsync(string sessionId, CancellationToken cancellationToken = default);
    Task RemoveAsync(string sessionId, bool deleteFiles, CancellationToken cancellationToken = default);
}
