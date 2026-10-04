using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface ISubtitleService : IDisposable
{
    Task<AltyaziDbAccountStatus> GetAccountStatusAsync(
        SubtitleSearchOptions options,
        CancellationToken cancellationToken = default);

    Task<SubtitleSearchResponse> SearchAsync(
        MediaIdentity identity,
        SubtitleSearchOptions options,
        CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(
        SubtitleSearchItem item,
        SubtitleSearchOptions options,
        CancellationToken cancellationToken = default);
}
