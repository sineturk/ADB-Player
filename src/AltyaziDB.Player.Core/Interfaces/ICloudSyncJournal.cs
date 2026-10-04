using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface ICloudSyncJournal
{
    Task QueueWatchProgressAsync(
        CloudWatchProgressMutation mutation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudWatchProgressMutation>> GetPendingWatchProgressAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task AcknowledgeWatchProgressAsync(
        IReadOnlyList<CloudWatchProgressMutation> sent,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveLocalSourceAsync(
        CloudWatchProgressEntry entry,
        CancellationToken cancellationToken = default);

    Task ApplyRemoteWatchProgressAsync(
        string source,
        CloudWatchProgressEntry entry,
        CancellationToken cancellationToken = default);
}
