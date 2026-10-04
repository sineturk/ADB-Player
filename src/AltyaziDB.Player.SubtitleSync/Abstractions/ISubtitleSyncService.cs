using AltyaziDB.Player.SubtitleSync.Models;

namespace AltyaziDB.Player.SubtitleSync.Abstractions;

public interface ISubtitleSyncService
{
    bool IsAvailable { get; }
    string EngineName { get; }
    string AvailabilityMessage { get; }

    Task<SubtitleSyncResult> SynchronizeAsync(
        SubtitleSyncRequest request,
        IProgress<SubtitleSyncProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
