using AltyaziDB.Player.AudioSync.Models;

namespace AltyaziDB.Player.AudioSync.Abstractions;

public interface IAudioSyncService
{
    bool IsAvailable { get; }
    bool SupportsFullSync { get; }
    bool SupportsLiveProgressiveSync { get; }
    string AvailabilityMessage { get; }
    string EngineName { get; }

    Task<AudioSyncResult> AnalyzeAsync(
        AudioSyncRequest request,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AudioSyncPreparedAudio?> PrepareCorrectedAudioAsync(
        AudioSyncRequest request,
        string outputDirectory,
        AudioSyncResult? analysisHint = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AudioSyncProgressiveRegion?> AnalyzeLiveProgressiveRegionAsync(
        AudioSyncRequest request,
        AudioSyncProgressiveRegion seedRegion,
        double targetPositionSeconds,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
