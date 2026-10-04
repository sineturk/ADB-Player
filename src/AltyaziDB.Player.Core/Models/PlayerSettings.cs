namespace AltyaziDB.Player.Core.Models;

public sealed class PlayerSettings
{
    public string UiLanguage { get; set; } = "tr-TR";
    public double Volume { get; set; } = 80;
    public int VolumeBoostLimit { get; set; } = 150;
    public double PlaybackSpeed { get; set; } = 1;
    public string HardwareDecoding { get; set; } = "auto-safe";
    public string VideoEnhancementMode { get; set; } = "off";
    public string VideoProfileMode { get; set; } = "auto";
    public double VideoBrightness { get; set; }
    public double VideoContrast { get; set; }
    public double VideoSaturation { get; set; }
    public double VideoGamma { get; set; }
    public string HdrOutputMode { get; set; } = "auto";
    public string ToneMappingMode { get; set; } = "auto";
    public string AudioDeviceName { get; set; } = "auto";
    public Dictionary<string, double> AudioDeviceDelays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int ContactSheetColumns { get; set; } = 4;
    public int ContactSheetRows { get; set; } = 4;
    public string ShortcutPlayPause { get; set; } = "Space";
    public string ShortcutSeekBackward { get; set; } = "Left";
    public string ShortcutSeekForward { get; set; } = "Right";
    public string ShortcutSeekBackwardMedium { get; set; } = "Shift+Left";
    public string ShortcutSeekForwardMedium { get; set; } = "Shift+Right";
    public string ShortcutSeekBackwardLong { get; set; } = "Ctrl+Left";
    public string ShortcutSeekForwardLong { get; set; } = "Ctrl+Right";
    public string ShortcutScreenshot { get; set; } = "Ctrl+S";
    public string ShortcutFrameBackward { get; set; } = "OemComma";
    public string ShortcutFrameForward { get; set; } = "OemPeriod";
    public string ShortcutFullscreen { get; set; } = "F";
    public string ShortcutMiniPlayer { get; set; } = "M";
    public string ShortcutPlaylist { get; set; } = "P";
    public bool AutoExternalAudioSync { get; set; } = false;
    public int AudioSyncMaxSearchSeconds { get; set; } = 45;
    public bool AutoSubtitleSync { get; set; } = false;
    public int SubtitleSyncMaxOffsetSeconds { get; set; } = 120;
    public bool RememberPlaybackPosition { get; set; } = true;
    public bool KeepWindowOnTop { get; set; }
    public bool AutoPlayNext { get; set; } = true;
    public bool AutoAddFolderVideos { get; set; } = true;
    public string LibraryViewMode { get; set; } = "list";
    public string LibrarySortMode { get; set; } = "smart";
    public string LibraryBrowseScope { get; set; } = "all";
    public int SeekShortSeconds { get; set; } = 10;
    public int SeekMediumSeconds { get; set; } = 30;
    public int SeekLongSeconds { get; set; } = 60;
    public double PrimarySubtitleDelaySeconds { get; set; }
    public double SecondarySubtitleDelaySeconds { get; set; }
    public double PrimarySubtitleScale { get; set; } = 1.0;
    public double SecondarySubtitleScale { get; set; } = 0.85;
    public double PrimarySubtitlePosition { get; set; } = 100;
    public double SecondarySubtitlePosition { get; set; } = 10;
    public bool PreservePrimaryAssStyle { get; set; } = true;
    public bool PreserveSecondaryAssStyle { get; set; } = true;
    public bool PlaylistVisible { get; set; } = true;
    public bool MediaCenterSidebarPreferenceSet { get; set; }
    public int TimeDisplayPrecision { get; set; }
    public List<SavedMediaAttachmentState> MediaAttachmentStates { get; set; } = new();
    public string? LastOpenedDirectory { get; set; }
    public string AltyaziDbApiUrl { get; set; } = "https://altyazidb.com/api/v1";

    // W2'den geçiş için tutulur. W3 ilk açılışta DPAPI gizli deposuna taşır ve alanı temizler.
    public string AltyaziDbApiKey { get; set; } = string.Empty;
    public string SubtitleLanguage { get; set; } = "tr";
    public List<RecentSourceEntry> RecentSources { get; set; } = new();
    public List<SavedWebDavProfile> WebDavProfiles { get; set; } = new();
    public List<SavedPublicLink> SavedPublicLinks { get; set; } = new();

    // W6 rclone bulut hesapları. OAuth tokenları ayarlarda değil, DPAPI ile korunan rclone yapılandırmasında tutulur.
    public List<SavedCloudAccount> CloudAccounts { get; set; } = new();

    // Telegram telefon bilgisi ve torrent tercihleri. Telegram uygulama kimliği .env.local üzerinden derlemeye eklenir.
    public int TelegramApiId { get; set; }
    public string TelegramPhoneNumber { get; set; } = string.Empty;
    public int TorrentDownloadLimitKiB { get; set; }
    public int TorrentUploadLimitKiB { get; set; } = 512;
    public int TorrentCacheLimitGiB { get; set; } = 20;
    public bool TorrentLegalNoticeAccepted { get; set; }

    // W5 yayın ve güncelleme ayarları. Güncelleme manifesti yalnız HTTPS üzerinden okunur.
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable";
    public string UpdateManifestUrl { get; set; } = string.Empty;
}

