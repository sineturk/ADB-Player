namespace AltyaziDB.Player.Core.Models;

public sealed record LibraryScanProgress(
    string FolderPath,
    int ScannedFiles,
    int AddedOrUpdated,
    string? CurrentFile,
    bool IsCompleted = false)
{
    public string StatusText => IsCompleted
        ? $"Tarama tamamlandı · {AddedOrUpdated} video"
        : $"Taranıyor · {ScannedFiles} dosya";
}
