using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface ITelegramService : IAsyncDisposable
{
    event EventHandler<TelegramAuthorizationSnapshot>? AuthorizationChanged;
    event EventHandler<TelegramDownloadProgress>? DownloadProgressChanged;
    event EventHandler<IReadOnlyList<TelegramChatFolderItem>>? ChatFoldersChanged;

    bool IsNativeAvailable { get; }
    string NativeDiagnostic { get; }
    TelegramAuthorizationSnapshot Authorization { get; }
    IReadOnlyList<TelegramChatFolderItem> ChatFolders { get; }

    // Fail closed for alternate providers/fakes that cannot establish a verified
    // TDLib account. Callers must never infer identity from a phone number or folder.
    Task<long?> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<long?>(null);

    Task StartAsync(
        TelegramClientConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task SubmitPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default);
    Task SubmitEmailAddressAsync(string emailAddress, CancellationToken cancellationToken = default);
    Task SubmitEmailCodeAsync(string code, CancellationToken cancellationToken = default);
    Task SubmitCodeAsync(string code, CancellationToken cancellationToken = default);
    Task SubmitPasswordAsync(string password, CancellationToken cancellationToken = default);
    Task RegisterUserAsync(string firstName, string lastName, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TelegramChatItem>> GetChatsAsync(
        int limit = 200,
        int? chatFolderId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TelegramForumTopicItem>> GetForumTopicsAsync(
        long chatId,
        string? query = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TelegramMediaItem>> GetChatMediaAsync(
        long chatId,
        string? query = null,
        int limit = 100,
        CancellationToken cancellationToken = default,
        int? forumTopicId = null);

    Task<IReadOnlyList<TelegramMediaItem>> SearchMediaAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<string> DownloadMediaAsync(
        TelegramMediaItem media,
        CancellationToken cancellationToken = default);

    // Stops active TDLib downloads and blocks new Range fetches until
    // explicitly resumed. Cached file bytes are not deleted.
    Task StopMediaTransferAsync(TelegramMediaItem media, CancellationToken cancellationToken = default);
    void ResumeMediaTransfer(TelegramMediaItem media);

    Task<RemoteOpenRequest> CreateStreamRequestAsync(
        TelegramMediaItem media,
        CancellationToken cancellationToken = default);

    Task<TelegramArchiveInspection> InspectArchiveAsync(
        TelegramMediaItem archive,
        CancellationToken cancellationToken = default);

    Task<RemoteOpenRequest> CreateArchiveStreamRequestAsync(
        TelegramMediaItem archive,
        TelegramArchiveEntry entry,
        CancellationToken cancellationToken = default);

    // TDLib-managed cache operations never delete the Telegram session database.
    Task<TelegramCacheUsage> GetCacheUsageAsync(
        CancellationToken cancellationToken = default);

    // Manual cleanup: targetBytes=0. Automatic maintenance: target=selected quota.
    // The UI must ensure playback has stopped before either operation.
    Task<TelegramCacheUsage> OptimizeCacheAsync(
        long targetBytes,
        bool manual,
        CancellationToken cancellationToken = default);

    Task<string> ExtractArchiveSubtitleAsync(
        TelegramMediaItem archive,
        TelegramArchiveEntry entry,
        CancellationToken cancellationToken = default);
}
