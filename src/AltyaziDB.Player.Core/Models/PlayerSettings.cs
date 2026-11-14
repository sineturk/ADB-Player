namespace AltyaziDB.Player.Core.Models;

public sealed class PlayerSettings
{
    public const string OfficialStableUpdateManifestUrl =
        "https://github.com/sineturk/ADB-Player/releases/latest/download/update-manifest.json";

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
    public string ShortcutSubtitleDelayDecrease { get; set; } = "Z";
    public string ShortcutSubtitleDelayIncrease { get; set; } = "X";
    public string ShortcutAudioDelayDecrease { get; set; } = "Shift+Z";
    public string ShortcutAudioDelayIncrease { get; set; } = "Shift+X";
    // R4.1.1: All additional playback keys are user-editable (set to "None"
    // to disable). Existing primary shortcuts remain unchanged.
    public string ShortcutSpeedIncrease { get; set; } = "OemPlus";
    public string ShortcutSpeedDecrease { get; set; } = "OemMinus";
    public string ShortcutSpeedReset { get; set; } = "Ctrl+D0";
    public string ShortcutNextMedia { get; set; } = "N";
    public string ShortcutCycleAudioTrack { get; set; } = "B";
    public string ShortcutCycleSubtitleTrack { get; set; } = "V";
    public string ShortcutMute { get; set; } = "Ctrl+M";
    public string ShortcutVolumeIncrease { get; set; } = "Ctrl+Up";
    public string ShortcutVolumeDecrease { get; set; } = "Ctrl+Down";
    public string ShortcutScreenshotAlternate { get; set; } = "Shift+S";
    public string ShortcutSubtitleDelayDecreaseAlternate { get; set; } = "G";
    public string ShortcutSubtitleDelayIncreaseAlternate { get; set; } = "H";
    public string ShortcutAudioDelayDecreaseAlternate { get; set; } = "J";
    public string ShortcutAudioDelayIncreaseAlternate { get; set; } = "K";
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
    // P4.14: limited to TDLib file cache; database location and encryption key never move.
    public int TelegramCacheLimitGiB { get; set; } = 10;
    public bool TelegramCacheAutoCleanup { get; set; } = true;
    // Empty means the normal AppPaths.TelegramFilesDirectory.
    // New directory is applied only on the next application startup.
    public string TelegramCacheDirectory { get; set; } = string.Empty;
    // P4.16.6: bounded rclone cache cleanup, never OAuth runtime/config or user downloads.
    public int CloudCacheLimitGiB { get; set; } = 2;
    public bool CloudCacheAutoCleanup { get; set; } = true;
    // Optional parent directory; app uses a dedicated marked child on restart.
    public string CloudCacheDirectory { get; set; } = string.Empty;
    // P4.15: favorites belong to this device and never contain authentication secrets.
    public List<long> TelegramFavoriteChatIds { get; set; } = new();
    // P4.15.4: source ownership is local-only metadata, never ADB Cloud auth.
    // Keys are persistent Telegram media identities; no API token or TDLib database is stored.
    public Dictionary<string, long> TelegramResumeSourceOwners { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    // Legacy P5 resumes did not carry account IDs. Claim once for the first
    // verified TDLib identity; never silently reassign on a later account switch.
    public bool TelegramLegacyResumeOwnershipMigrated { get; set; }
    public int TorrentDownloadLimitKiB { get; set; }
    public int TorrentUploadLimitKiB { get; set; } = 512;
    public int TorrentCacheLimitGiB { get; set; } = 20;
    public bool TorrentLegalNoticeAccepted { get; set; }
    // R5/P4.11 qBittorrent handoff. Password is NEVER stored in JSON settings;
    // it is kept separately in the DPAPI-backed ISecretStore.
    // Optional user-managed download location. Empty keeps the existing cache directory.
    // This changes the target of new torrent sessions; existing downloaded data is not moved.
    public string TorrentDownloadDirectory { get; set; } = string.Empty;
    public string QBittorrentWebUiUrl { get; set; } = "http://127.0.0.1:8080";
    public string QBittorrentUsername { get; set; } = string.Empty;
    public bool QBittorrentAutoHandoff { get; set; }


    // W5 yayın ve güncelleme ayarları. Güncelleme manifesti yalnız HTTPS üzerinden okunur.
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable";
    public string UpdateManifestUrl { get; set; } = OfficialStableUpdateManifestUrl;
}

