using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface IMediaLibrary
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<LibraryFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default);
    Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default);
    Task ScanFolderAsync(
        long folderId,
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task ScanAllAsync(
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryMediaItem>> SearchAsync(
        string? searchText,
        LibraryMediaFilter filter,
        int limit = 1000,
        CancellationToken cancellationToken = default);
}
