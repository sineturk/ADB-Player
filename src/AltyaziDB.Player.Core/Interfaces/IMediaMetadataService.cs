using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface IMediaMetadataService
{
    Task<MediaMetadataResult> ResolveAsync(
        MediaIdentity identity,
        string? clientId = null,
        CancellationToken cancellationToken = default,
        bool includeDetail = false);

    Task<IReadOnlyList<MediaMetadataResult>> ResolveBatchAsync(
        IReadOnlyList<MediaIdentity> identities,
        CancellationToken cancellationToken = default);
}
