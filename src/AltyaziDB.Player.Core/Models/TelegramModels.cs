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
    long Order) : System.ComponentModel.INotifyPropertyChanged
{
    // Source identity stays stable across folder reloads. Favorites notify WPF
    // immediately without rebuilding a selected ListBoxItem or opening media.
    private bool _isFavorite;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FavoriteGlyph)));
        }
    }
    // TDLib chatTypeSupergroup.is_channel distinguishes channels from groups;
    // avoid inferring type from a user's chat title or an avatar icon.
    public bool IsChannel { get; init; }
    public bool IsGroup => ChatType == "chatTypeBasicGroup" ||
                           (ChatType == "chatTypeSupergroup" && !IsChannel);
    public bool IsPrivate => !IsChannel && !IsGroup;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string UnreadBadgeText => UnreadCount > 999 ? "999+" : UnreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
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
    Subtitle,
    ArchivePart,
    SplitArchive
}

public enum TelegramArchiveFormat
{
    None,
    SplitZip,
    MultipartRar,
    Split7Zip,
    RawSplit,
    GenericSplit
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
    // MEDIA INFO is a Telegram message caption, not the uploaded document's
    // identity. Use the real file name for a stable one-file/one-card list.
    public string DisplayTitle => FileName;
    public string DateLabel => Date == DateTimeOffset.MinValue ? string.Empty : Date.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
    public string MetaLabel => Kind == TelegramMediaKind.SplitArchive
        ? $"{ArchiveFormatLabel} · {ArchiveParts?.Count ?? 0} parça · {FormatSize(Size)}" +
          (PartStatusCode is "missing" or "ambiguous" ? $" · {ArchiveStatusLabel}" : string.Empty)
        : Kind == TelegramMediaKind.ArchivePart
            ? $"{(Path.GetExtension(FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? "ZIP" : "Arşiv parçası")} · {FormatSize(Size)}"
            : $"{Kind} · {FormatSize(Size)} · {FormatDuration(DurationSeconds)}";
    // P4.15.2/P3: source-derived display facets, not filenames parsed from
    // MEDIA INFO captions. Keep these pure: localization belongs to the UI.
    public bool IsStandaloneArchive => !IsArchiveSet && Kind != TelegramMediaKind.ArchivePart &&
        (FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
         FileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
         FileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase));
    public string MediaCategory => Kind switch
    {
        TelegramMediaKind.SplitArchive or TelegramMediaKind.ArchivePart => "archive",
        _ when IsStandaloneArchive => "archive",
        TelegramMediaKind.Video or TelegramMediaKind.Animation => "video",
        TelegramMediaKind.Audio => "audio",
        TelegramMediaKind.Subtitle => "subtitle",
        _ => "document"
    };
    public string MediaIcon => MediaCategory switch
    {
        "video" => "▶",
        "archive" => "▣",
        "subtitle" => "≡",
        "audio" => "♫",
        _ => "▤"
    };
    public string MediaSizeLabel => FormatSize(Size);
    public string DurationLabel => DurationSeconds > 0 ? FormatDuration(DurationSeconds) : string.Empty;
    public bool HasKnownDuration => DurationSeconds > 0 && !IsArchiveSet;
    public int ArchivePartCount => ArchiveParts?.Count ?? (Kind == TelegramMediaKind.ArchivePart ? 1 : 0);
    public bool HasArchivePartCount => IsArchiveSet && ArchivePartCount > 0;
    // "Unverified end" must NEVER be presented as "ready" for raw/generic split.
    public string PartStatusCode => Kind == TelegramMediaKind.ArchivePart ? "standalone" :
        !IsArchiveSet ? "none" :
        ArchiveSetAmbiguous ? "ambiguous" :
        !ArchiveSetComplete ? "missing" :
        ArchiveFormat is TelegramArchiveFormat.RawSplit or TelegramArchiveFormat.GenericSplit
            ? "unverified" : "ready";

    public bool IsArchiveSet => Kind == TelegramMediaKind.SplitArchive;
    public bool NeedsArchiveRepair => IsArchiveSet && (!ArchiveSetComplete || ArchiveSetAmbiguous);
    public string ArchiveStatusLabel => ArchiveSetAmbiguous
        ? "Seçim gerekli"
        : !ArchiveSetComplete ? "Eksik parça"
        : ArchiveFormat is TelegramArchiveFormat.RawSplit or TelegramArchiveFormat.GenericSplit
            ? "Sıralı parçalar · son parça doğrulanamadı"
            : "Parçalar hazır";
    public string ArchiveFormatLabel => ArchiveFormat switch
    {
        TelegramArchiveFormat.SplitZip => "Bölünmüş ZIP",
        TelegramArchiveFormat.MultipartRar => "Çok parçalı RAR",
        TelegramArchiveFormat.Split7Zip => "Bölünmüş 7-Zip",
        TelegramArchiveFormat.RawSplit => "Bölünmüş video",
        TelegramArchiveFormat.GenericSplit => "Bölünmüş dosya",
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

/// <summary>
/// Approximate TDLib-managed file bytes plus app-owned archive temporary bytes.
/// Database storage is informational and is never part of file-cache cleanup.
/// </summary>
public sealed record TelegramCacheUsage(
    long FilesBytes,
    int FileCount,
    long DatabaseBytes,
    long ArchiveTemporaryBytes,
    string FilesDirectory)
{
    public long CacheBytes => checked(FilesBytes + ArchiveTemporaryBytes);
}

public sealed record TelegramDownloadProgress(
    int FileId,
    long DownloadedBytes,
    long TotalBytes,
    bool IsCompleted,
    string? LocalPath)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100);
}
