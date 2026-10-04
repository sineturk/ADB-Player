namespace AltyaziDB.Player.Core.Models;

public sealed record LibraryFolder(
    long Id,
    string Path,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset? LastScanAtUtc,
    int ItemCount)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
        ? name
        : Path;

    public string ScanLabel => LastScanAtUtc is null
        ? "Henüz taranmadı"
        : $"Son tarama: {LastScanAtUtc.Value.LocalDateTime:g}";
}
