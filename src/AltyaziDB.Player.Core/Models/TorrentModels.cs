namespace AltyaziDB.Player.Core.Models;

public enum TorrentSessionState
{
    Preparing,
    Metadata,
    Ready,
    Downloading,
    Paused,
    Complete,
    Error,
    Removed
}

public sealed record TorrentEngineConfiguration(
    string CacheDirectory,
    int DownloadLimitKiB = 0,
    int UploadLimitKiB = 512,
    int CacheLimitGiB = 20);

public sealed record TorrentFileItem(
    int Index,
    string Name,
    string Path,
    long Length,
    long CompletedLength,
    bool IsSelected,
    bool IsVideo)
{
    public double ProgressPercent => Length <= 0 ? 0 : Math.Clamp(CompletedLength * 100d / Length, 0, 100);
    public string SizeLabel => FormatSize(Length);

    private static string FormatSize(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}

public sealed record TorrentSessionSnapshot(
    string SessionId,
    string Name,
    string Source,
    TorrentSessionState State,
    IReadOnlyList<TorrentFileItem> Files,
    long CompletedLength = 0,
    long TotalLength = 0,
    long DownloadSpeed = 0,
    long UploadSpeed = 0,
    int Seeders = 0,
    string? ErrorMessage = null)
{
    public double ProgressPercent => TotalLength <= 0 ? 0 : Math.Clamp(CompletedLength * 100d / TotalLength, 0, 100);
    public string StatusLabel => ErrorMessage is not null
        ? ErrorMessage
        : $"{State} · %{ProgressPercent:0.0} · ↓ {FormatRate(DownloadSpeed)} · ↑ {FormatRate(UploadSpeed)} · {Seeders} kaynak";

    private static string FormatRate(long value)
    {
        string[] units = ["B/sn", "KB/sn", "MB/sn", "GB/sn"];
        double rate = Math.Max(0, value);
        var unit = 0;
        while (rate >= 1024 && unit < units.Length - 1)
        {
            rate /= 1024;
            unit++;
        }
        return $"{rate:0.##} {units[unit]}";
    }
}

public sealed record TorrentPlaybackSource(
    string SessionId,
    int FileIndex,
    string LocalPath,
    string DisplayName);
