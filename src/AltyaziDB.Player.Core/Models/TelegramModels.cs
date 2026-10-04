namespace AltyaziDB.Player.Core.Models;

public enum TelegramAuthorizationStage
{
    NotStarted,
    Starting,
    WaitingForParameters,
    WaitingForPhoneNumber,
    WaitingForEmailAddress,
    WaitingForEmailCode,
    WaitingForCode,
    WaitingForPassword,
    WaitingForRegistration,
    WaitingForOtherDeviceConfirmation,
    Ready,
    LoggingOut,
    Closing,
    Closed,
    Error
}

public sealed record TelegramAuthorizationSnapshot(
    TelegramAuthorizationStage Stage,
    string Message = "",
    bool IsReady = false)
{
    public static TelegramAuthorizationSnapshot NotStarted { get; } =
        new(TelegramAuthorizationStage.NotStarted, "Telegram başlatılmadı.");
}

public sealed record TelegramClientConfiguration(
    int ApiId,
    string ApiHash,
    string DatabaseDirectory,
    string FilesDirectory,
    string DatabaseEncryptionKey,
    string DeviceModel = "AltyazıDB Player Windows",
    string ApplicationVersion = "0.5.0");

public sealed record TelegramChatItem(
    long Id,
    string Title,
    string ChatType,
    int UnreadCount,
    long Order)
{
    public string AvatarText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return "T";
            var words = Title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return words.Length > 1
                ? string.Concat(words[0][0], words[1][0]).ToUpperInvariant()
                : words[0][0].ToString().ToUpperInvariant();
        }
    }

    public override string ToString() => Title;
}

public sealed record TelegramForumTopicItem(
    long ChatId,
    int Id,
    string Name,
    int UnreadCount,
    long Order,
    bool IsPinned,
    bool IsClosed,
    bool IsGeneral)
{
    public override string ToString() => Name;
}

public sealed record TelegramChatFolderItem(
    int? Id,
    string Name,
    int Position = 0)
{
    public bool IsArchive => Id == -1;
    public override string ToString() => Name;
}

public enum TelegramMediaKind
{
    Video,
    Animation,
    Document,
    Audio,
    ArchivePart,
    SplitArchive
}

public enum TelegramArchiveFormat
{
    None,
    SplitZip,
    MultipartRar,
    Split7Zip
}

public sealed record TelegramMediaItem(
    long ChatId,
    long MessageId,
    int FileId,
    string ChatTitle,
    string FileName,
    string Caption,
    string MimeType,
    long Size,
    int DurationSeconds,
    DateTimeOffset Date,
    TelegramMediaKind Kind,
    string? LocalPath = null,
    bool IsDownloaded = false,
    TelegramArchiveFormat ArchiveFormat = TelegramArchiveFormat.None,
    string? ArchiveBaseName = null,
    IReadOnlyList<TelegramMediaItem>? ArchiveParts = null,
    bool ArchiveSetComplete = true,
    bool ArchiveSetAmbiguous = false)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Caption) ? FileName : Caption;
    public string DateLabel => Date == DateTimeOffset.MinValue ? string.Empty : Date.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
    public string MetaLabel => Kind == TelegramMediaKind.SplitArchive
        ? $"{ArchiveFormatLabel} · {ArchiveParts?.Count ?? 0} parça · {FormatSize(Size)} · {ArchiveStatusLabel}"
        : Kind == TelegramMediaKind.ArchivePart
            ? $"Arşiv parçası · {FormatSize(Size)}"
            : $"{Kind} · {FormatSize(Size)} · {FormatDuration(DurationSeconds)}";
    public bool IsArchiveSet => Kind == TelegramMediaKind.SplitArchive;
    public bool NeedsArchiveRepair => IsArchiveSet && (!ArchiveSetComplete || ArchiveSetAmbiguous);
    public string ArchiveStatusLabel => ArchiveSetAmbiguous
        ? "Seçim gerekli"
        : ArchiveSetComplete ? "Parçalar hazır" : "Eksik parça";
    public string ArchiveFormatLabel => ArchiveFormat switch
    {
        TelegramArchiveFormat.SplitZip => "Bölünmüş ZIP",
        TelegramArchiveFormat.MultipartRar => "Çok parçalı RAR",
        TelegramArchiveFormat.Split7Zip => "Bölünmüş 7-Zip",
        _ => "Arşiv"
    };

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

    private static string FormatDuration(int seconds) =>
        seconds <= 0 ? "Süre bilinmiyor" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
}

public sealed record TelegramArchiveEntry(
    string Name,
    long UncompressedSize,
    long CompressedSize,
    int CompressionMethod,
    long DataOffset,
    bool IsEncrypted,
    bool IsPlayable)
{
    public bool UsesDirectRange => IsPlayable && !IsEncrypted && CompressionMethod == 0 && DataOffset >= 0;
    public bool CanInstantStream => IsPlayable && !IsEncrypted;
    public string SupportLabel => IsEncrypted
        ? "Parola gerekli"
        : UsesDirectRange ? "Anında oynatılabilir" : "Akış sırasında çözülür";
    public string DisplayLabel => $"{Name} · {FormatSize(UncompressedSize)} · {SupportLabel}";

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

public sealed record TelegramArchiveInspection(
    TelegramArchiveFormat Format,
    IReadOnlyList<TelegramArchiveEntry> Entries,
    bool IsComplete,
    string StatusMessage);

public sealed record TelegramDownloadProgress(
    int FileId,
    long DownloadedBytes,
    long TotalBytes,
    bool IsCompleted,
    string? LocalPath)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100);
}
