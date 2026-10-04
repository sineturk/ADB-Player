using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface ICloudAccountService
{
    bool IsConfigured { get; }
    bool IsCloudCoreConfigured { get; }
    string AuthBaseUrl { get; }
    string ApiBaseUrl { get; }

    Task<CloudAccountSnapshot?> RestoreSessionAsync(CancellationToken cancellationToken = default);

    Task<CloudAccountResult> SignUpAsync(
        string displayName,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    Task<CloudAccountResult> SignInAsync(
        string email,
        string password,
        bool rememberMe = true,
        CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);

    Task<CloudAccountCoreResult> BootstrapAccountCoreAsync(
        CloudAccountSnapshot account,
        string locale,
        CancellationToken cancellationToken = default);

    Task<CloudAccountCoreResult> RefreshAccountCoreAsync(
        CancellationToken cancellationToken = default);

    Task<CloudAccountCoreResult> UpdateProfileAsync(
        string displayName,
        string? avatarUrl,
        string locale,
        CancellationToken cancellationToken = default);

    Task<CloudAccountCoreResult> RemoveDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);
    Task<CloudWatchSyncResult> SyncLibraryAsync(
        IReadOnlyList<CloudLibraryManifestItem> items,
        CancellationToken cancellationToken = default);

    Task<CloudWatchSyncResult> SyncWatchProgressAsync(
        IReadOnlyList<CloudWatchProgressMutation> mutations,
        CancellationToken cancellationToken = default);

    Task<CloudWatchSyncResult> GetWatchProgressAsync(
        CancellationToken cancellationToken = default);

    Task<CloudCollectionsResult> GetCollectionsAsync(
        CancellationToken cancellationToken = default);

    Task<CloudCollectionsResult> CreateCollectionAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<CloudCollectionsResult> RenameCollectionAsync(
        Guid collectionId,
        string name,
        CancellationToken cancellationToken = default);

    Task<CloudCollectionsResult> DeleteCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default);

    Task<CloudCollectionItemsResult> GetCollectionItemsAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default);

    Task<CloudCollectionItemsResult> AddCollectionItemAsync(
        Guid collectionId,
        CloudLibraryManifestItem item,
        CancellationToken cancellationToken = default);

    Task<CloudCollectionItemsResult> RemoveCollectionItemAsync(
        Guid collectionId,
        string mediaKey,
        CancellationToken cancellationToken = default);

    Task<CloudPlaylistResult> GetPlaylistAsync(
        CancellationToken cancellationToken = default);

    Task<CloudPlaylistResult> SavePlaylistAsync(
        IReadOnlyList<CloudLibraryManifestItem> items,
        string? currentMediaKey,
        CancellationToken cancellationToken = default);

    Task<CloudPreferencesResult> GetPreferencesAsync(
        CancellationToken cancellationToken = default);

    Task<CloudPreferencesResult> SavePreferencesAsync(
        CloudUserPreferences preferences,
        CancellationToken cancellationToken = default);

}
