using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using AltyaziDB.Player.Addons;
using AltyaziDB.Player.Addons.Models;
using AltyaziDB.Player.AudioSync.Abstractions;
using AltyaziDB.Player.AudioSync.Models;
using AltyaziDB.Player.SubtitleSync.Abstractions;
using AltyaziDB.Player.SubtitleSync.Models;
using AltyaziDB.Player.Connections;
using AltyaziDB.Player.Connections.Models;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Core.Parsing;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".webm", ".mov", ".m4v", ".ts", ".m2ts", ".mts",
        ".mpeg", ".mpg", ".wmv", ".flv", ".ogv", ".vob", ".3gp"
    };

    // Rev10.4: Otomatik profil yalnız güçlü dosya/release işaretlerine güvenir.
    // Japonca ses tek başına anime kabul edilmez; canlı çekim Japon yapımlarında
    // agresif deband / shader açılması istenmez.
    private static readonly string[] AnimeProfileKeywords =
    [
        "subsplease", "erai raws", "crunchyroll", "cr web dl", "adn web dl",
        "hidive", "funimation", "animelab", "b global", "bilibili", "abema",
        "wakanim", "muse asia", "anime time"
    ];

    private static readonly string[] DocumentaryProfileKeywords =
    [
        "belgesel", "documentary", "documentaries", "natgeo", "national geographic",
        "bbc earth", "bbc natural", "bbc horizon", "discovery channel",
        "curiositystream", "smithsonian", "pbs nova", "planet earth", "blue planet",
        "our planet", "frozen planet", "green planet", "attenborough", "trt belgesel"
    ];

    private static readonly string[] RemuxSourceKeywords =
    [
        "remux", "bluray", "blu ray", "bdremux", "bdmv", "uhd bluray", "uhd blu ray"
    ];

    private static readonly string[] WebSourceKeywords =
    [
        "web dl", "webdl", "webrip", "web rip", "hdtv", "amzn", "dsnp", "atvp",
        "hmax", "pcok", "itunes", "nf web", "blutv", "exxen", "tabii", "bein",
        "todtv", "720p web", "1080p web", "2160p web", "web h264", "web h265",
        "web x264", "web x265"
    ];

    private readonly IPlaybackEngine _playback;
    private readonly IAudioSyncService _audioSyncService;
    private readonly ISubtitleSyncService _subtitleSyncService;
    private readonly ISettingsService _settingsService;
    private readonly IResumeStore _resumeStore;
    private readonly ICloudAccountService _cloudAccountService;
    private readonly IMediaMetadataService _mediaMetadataService;
    private readonly ICloudSyncJournal? _cloudSyncJournal;
    private readonly IMediaLibrary _mediaLibrary;
    private readonly ReleaseParser _releaseParser;
    private readonly IAppLogger _logger;
    private readonly IUserDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly PlayerSettings _settings;
    private readonly SemaphoreSlim _resumeSaveGate = new(1, 1);
    private readonly SemaphoreSlim _playbackInitGate = new(1, 1);
    private readonly SemaphoreSlim _cloudSyncGate = new(1, 1);
    private readonly MediaArtworkService _mediaArtwork;

    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private string _statusMessage = "Bir kaynak seçin.";
    private bool _isReady;
    private bool _isMediaCenterVisible = true;
    private nint _videoWindowHandle;
    private MediaTrack? _selectedAudioTrack;
    private MediaTrack? _selectedSubtitleTrack;
    private MediaTrack? _selectedSecondarySubtitleTrack;
    private long? _selectedSecondarySubtitleTrackId;
    private string _goToTimeText = string.Empty;
    private PlaylistItem? _selectedPlaylistItem;
    private ChapterInfo? _selectedChapter;
    private bool _suppressTrackSelection;
    private bool _suppressChapterSelection;
    private DateTimeOffset _lastResumeWrite = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCloudSyncAttempt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _cloudPlaylistSyncCts;
    private string _cloudPlaylistStatus = string.Empty;
    private int _cloudPlaylistCount;
    private bool _suppressCloudPlaylistSync;
    private bool _cloudPlaylistSessionInitialized;
    private bool _cloudPlaylistSyncQueuedBeforeInitialization;
    private bool _cloudPlaylistHasUnresolvedItems;
    private string? _cloudPlaylistInitializedUserId;
    private readonly SemaphoreSlim _cloudPlaylistInitializationGate = new(1, 1);
    private CancellationTokenSource? _cloudPreferencesSyncCts;
    private CancellationTokenSource? _continueWatchingArtworkCts;
    private CancellationTokenSource? _historyArtworkCts;
    private bool _suppressCloudPreferencesSync;
    private string _cloudPreferencesStatus = string.Empty;
    private DateTimeOffset? _cloudPreferencesLastSyncAt;
    private string? _currentResumeKey;
    private bool _disposed;
    private LanguageOption _selectedLanguage = LocalizationManager.SupportedLanguages[0];
    private int _selectedSidebarIndex;
    private bool _resumeAfterExternalAudioSelection;
    private string? _currentPlaybackSource;
    private IReadOnlyDictionary<string, string>? _currentPlaybackHeaders;
    private VideoEnhancementOption? _selectedVideoEnhancement;
    private VideoProfileOption? _selectedVideoProfile;
    private PlayerChoiceOption? _selectedHdrOutputMode;
    private PlayerChoiceOption? _selectedToneMappingMode;
    private PlayerChoiceOption? _selectedContactSheetSize;
    private AudioDeviceInfo? _selectedAudioDevice;
    private string _effectiveVideoProfileCode = "film";
    private string _videoProfileSummary = "Görüntü profili hazır.";
    private double _audioDeviceDelaySeconds;
    private bool _suppressAudioDeviceSelection;
    private CancellationTokenSource? _audioSyncCts;
    private AudioSyncResult? _audioSyncResult;
    private AudioSyncSafetyAssessment? _audioSyncSafety;
    private string? _externalAudioSource;
    private IReadOnlyDictionary<string, string>? _externalAudioHeaders;
    private string _audioSyncStatus = "Harici ses eklendiğinde yalnızca istenirse senkronlanır.";
    private bool _isAudioSyncAnalyzing;
    private int _audioSyncProgress;
    private DateTimeOffset _audioSyncProgressStartedAt = DateTimeOffset.MinValue;
    private double _manualAudioDelaySeconds;
    private double _appliedAudioDelaySeconds;
    private double _audioSyncTempoFactor = 1.0;
    private AudioSyncApplicationKind _audioSyncApplicationKind = AudioSyncApplicationKind.None;
    private bool _isAudioSyncPreparing;
    private AudioSyncPreparedAudio? _preparedAudio;
    private string? _preparedAudioPath;
    private bool _audioSyncFinalized;
    private IReadOnlyList<AudioSyncProgressiveRegion> _progressiveAudioRegions = Array.Empty<AudioSyncProgressiveRegion>();
    private int _progressiveAudioRegionIndex = -1;
    private double _progressiveLastPositionSeconds = double.NaN;
    private CancellationTokenSource? _liveProgressiveAudioCts;
    private int _liveProgressiveAnalyzingRegionIndex = -1;
    private double _liveProgressiveAnalysisTargetSeconds = double.NaN;
    private int _liveProgressiveGeneration;
    private readonly Dictionary<int, int> _liveProgressiveAttempts = new();
    private readonly Dictionary<int, double> _liveProgressiveLastAttemptTarget = new();
    private readonly Dictionary<int, DateTimeOffset> _liveProgressiveRetryAfter = new();
    private bool _isLiveProgressiveAnalyzing;
    private CancellationTokenSource? _subtitleSyncCts;
    private SubtitleSyncResult? _subtitleSyncResult;
    private bool _isSubtitleSyncBusy;
    private bool _hasSubtitleSyncSession;
    private int _subtitleSyncProgress;
    private string _subtitleSyncStage = "Altyazı senkronu hazır";
    private string _subtitleSyncStatus = "Altyazı eklendiğinde yalnızca istenirse senkronlanır.";
    private string? _subtitleSyncWorkingPath;
    private string? _primarySubtitleOriginalPath;
    private string? _secondarySubtitlePath;
    private bool _subtitleSyncFinalized;

    public MainViewModel(
        IPlaybackEngine playback,
        IAudioSyncService audioSyncService,
        ISubtitleSyncService subtitleSyncService,
        ISettingsService settingsService,
        IResumeStore resumeStore,
        IAppLogger logger,
        IUserDialogService dialogs,
        AppPaths paths,
        IMediaLibrary mediaLibrary,
        ISubtitleService subtitleService,
        IRemoteSourceService remoteSourceService,
        IRcloneCloudService cloudService,
        ITelegramService telegramService,
        ITorrentService torrentService,
        IUpdateService updateService,
        ISecretStore secretStore,
        ICloudAccountService cloudAccountService,
        IExternalConnectionService externalConnectionService,
        IAddonCatalogService addonCatalogService,
        ReleaseParser releaseParser,
        CrashReportService crashReports,
        IMediaMetadataService mediaMetadataService)
    {
        _playback = playback;
        _audioSyncService = audioSyncService;
        _subtitleSyncService = subtitleSyncService;
        _settingsService = settingsService;
        _resumeStore = resumeStore;
        _cloudAccountService = cloudAccountService;
        _mediaMetadataService = mediaMetadataService;
        _mediaArtwork = new MediaArtworkService(mediaMetadataService, paths, logger);
        _cloudSyncJournal = resumeStore as ICloudSyncJournal;
        _mediaLibrary = mediaLibrary;
        _releaseParser = releaseParser;
        _logger = logger;
        _dialogs = dialogs;
        _paths = paths;
        _settings = _settingsService.LoadAsync().GetAwaiter().GetResult();
        _settings.RecentSources ??= new List<RecentSourceEntry>();
        _settings.MediaAttachmentStates ??= new List<SavedMediaAttachmentState>();
        if (!_settings.MediaCenterSidebarPreferenceSet)
        {
            // Rev10.6: video playback starts distraction-free. The user can
            // reveal the Media Center sidebar with the edge chevron and that
            // preference is persisted from then on.
            _settings.PlaylistVisible = false;
            _settings.MediaCenterSidebarPreferenceSet = true;
        }
        _settings.TimeDisplayPrecision = Math.Clamp(_settings.TimeDisplayPrecision, 0, 2);
        _settings.UiLanguage = string.IsNullOrWhiteSpace(_settings.UiLanguage) ? "tr-TR" : _settings.UiLanguage;
        LanguageOptions = LocalizationManager.SupportedLanguages;
        _selectedLanguage = LanguageOptions.FirstOrDefault(item => item.Code.Equals(_settings.UiLanguage, StringComparison.OrdinalIgnoreCase))
            ?? LanguageOptions[0];
        _selectedSidebarIndex = 0;

        Library = new LibraryPanelViewModel(
            mediaLibrary,
            cloudAccountService,
            releaseParser,
            dialogs,
            logger,
            paths,
            _mediaArtwork,
            _settings,
            SaveSettingsAsync,
            OpenSourceArgumentAsync);
        Subtitles = new SubtitlePanelViewModel(
            subtitleService,
            secretStore,
            releaseParser,
            _settings,
            SaveSettingsAsync,
            AttachSubtitlePath,
            dialogs,
            logger,
            paths);
        Sources = new SourcePanelViewModel(
            remoteSourceService,
            cloudService,
            secretStore,
            _settings,
            SaveSettingsAsync,
            OpenRemoteSourceAsync,
            AttachSubtitlePath,
            AttachRemoteAudioAsync,
            dialogs,
            logger,
            paths);
        Telegram = new TelegramPanelViewModel(
            telegramService,
            secretStore,
            _settings,
            SaveSettingsAsync,
            OpenRemoteSourceAsync,
            AttachRemoteAudioAsync,
            dialogs,
            logger,
            paths);
        Torrent = new TorrentPanelViewModel(
            torrentService,
            _settings,
            SaveSettingsAsync,
            OpenTorrentPlaybackSourceAsync,
            dialogs,
            logger,
            paths);
        Connections = new ConnectionsPanelViewModel(
            externalConnectionService,
            OpenExternalReleaseAsync,
            dialogs,
            logger,
            paths);
        Addons = new AddonsPanelViewModel(
            addonCatalogService,
            cloudAccountService,
            externalConnectionService.SearchAsync,
            OpenAddonStreamAsync,
            OpenExternalReleaseAsync,
            dialogs,
            logger,
            paths);
        Addons.PortablePreferencesChanged += AddonsOnPortablePreferencesChanged;
        Library.CloudCollectionsChanged += CloudCollectionsOnChanged;
        Addons.CloudCollectionsChanged += CloudCollectionsOnChanged;
        Updates = new UpdatePanelViewModel(
            updateService,
            _settings,
            SaveSettingsAsync,
            dialogs,
            logger,
            paths,
            crashReports);
        ApiSettings = new ApiSettingsPanelViewModel(
            secretStore,
            _settings,
            SaveSettingsAsync,
            Telegram.ReloadCredentialsAsync,
            Subtitles.ReloadApiConfigurationAsync,
            logger);
        Account = new AccountPanelViewModel(cloudAccountService, mediaMetadataService, logger);
        Account.CloudSessionReady += AccountOnCloudSessionReady;

        AudioTracks = new ObservableCollection<MediaTrack>();
        SubtitleTracks = new ObservableCollection<MediaTrack> { MediaTrack.DisabledSubtitle() };
        SecondarySubtitleTracks = new ObservableCollection<MediaTrack> { MediaTrack.DisabledSubtitle() };
        Chapters = new ObservableCollection<ChapterInfo>();
        Playlist = new ObservableCollection<PlaylistItem>();
        RecentSources = new ObservableCollection<RecentSourceEntry>(
            _settings.RecentSources
                .OrderByDescending(item => item.LastOpenedUtc)
                .Take(15));
        HistoryItems = new ObservableCollection<HistoryItem>(
            RecentSources.Select(CreateHistoryItem));
        ContinueWatching = new ObservableCollection<ContinueWatchingItem>();
        SpeedOptions = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0 };
        VideoProfileOptions = CreateVideoProfileOptions();
        _selectedVideoProfile = VideoProfileOptions.FirstOrDefault(option =>
            option.Code.Equals(_settings.VideoProfileMode, StringComparison.OrdinalIgnoreCase))
            ?? VideoProfileOptions[0];
        VideoEnhancementOptions = CreateVideoEnhancementOptions();
        _selectedVideoEnhancement = VideoEnhancementOptions.FirstOrDefault(option =>
            option.Code.Equals(_settings.VideoEnhancementMode, StringComparison.OrdinalIgnoreCase))
            ?? VideoEnhancementOptions[0];
        HdrOutputOptions = CreateHdrOutputOptions();
        _selectedHdrOutputMode = HdrOutputOptions.FirstOrDefault(option =>
            option.Code.Equals(_settings.HdrOutputMode, StringComparison.OrdinalIgnoreCase))
            ?? HdrOutputOptions[0];
        ToneMappingOptions = CreateToneMappingOptions();
        _selectedToneMappingMode = ToneMappingOptions.FirstOrDefault(option =>
            option.Code.Equals(_settings.ToneMappingMode, StringComparison.OrdinalIgnoreCase))
            ?? ToneMappingOptions[0];
        ContactSheetSizeOptions = CreateContactSheetSizeOptions();
        var contactSheetCode = $"{Math.Clamp(_settings.ContactSheetColumns, 4, 6)}x{Math.Clamp(_settings.ContactSheetRows, 4, 6)}";
        _selectedContactSheetSize = ContactSheetSizeOptions.FirstOrDefault(option => option.Code == contactSheetCode)
            ?? ContactSheetSizeOptions[0];
        AudioDevices = new ObservableCollection<AudioDeviceInfo> { new AudioDeviceInfo("auto", LocalizationManager.Get("Player.AudioOutput.Auto")) };
        _selectedAudioDevice = AudioDevices[0];
        _settings.AudioDeviceDelays ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync);
        OpenUrlCommand = new AsyncRelayCommand(OpenUrlAsync);
        AttachSubtitleCommand = new AsyncRelayCommand(AttachSubtitleAsync);
        AttachSecondarySubtitleCommand = new AsyncRelayCommand(AttachSecondarySubtitleAsync);
        AttachAudioCommand = new AsyncRelayCommand(AttachAudioAsync);
        BrowseRemoteAudioCommand = new RelayCommand(BeginRemoteAudioSelection);
        BrowseTelegramAudioCommand = new RelayCommand(BeginTelegramAudioSelection);
        ResyncAudioCommand = new AsyncRelayCommand(ResyncExternalAudioAsync);
        ResyncSubtitleCommand = new AsyncRelayCommand(ResyncExternalSubtitleAsync);
        AudioDelayDecreaseCommand = new RelayCommand(() => AdjustManualAudioDelay(-0.05));
        AudioDelayIncreaseCommand = new RelayCommand(() => AdjustManualAudioDelay(0.05));
        PlayPauseCommand = new RelayCommand(() => SafePlaybackAction(_playback.TogglePause));
        SeekBackwardCommand = new RelayCommand(() => SeekRelative(-SeekShortSeconds));
        SeekForwardCommand = new RelayCommand(() => SeekRelative(SeekShortSeconds));
        SeekBackwardMediumCommand = new RelayCommand(() => SeekRelative(-SeekMediumSeconds));
        SeekForwardMediumCommand = new RelayCommand(() => SeekRelative(SeekMediumSeconds));
        SeekBackwardLongCommand = new RelayCommand(() => SeekRelative(-SeekLongSeconds));
        SeekForwardLongCommand = new RelayCommand(() => SeekRelative(SeekLongSeconds));
        FrameBackwardCommand = new RelayCommand(() => SafePlaybackAction(() => _playback.StepFrame(true)));
        FrameForwardCommand = new RelayCommand(() => SafePlaybackAction(() => _playback.StepFrame(false)));
        GoToTimeCommand = new RelayCommand(GoToTime);
        PrimarySubtitleDelayDecreaseCommand = new RelayCommand(() => PrimarySubtitleDelaySeconds -= 0.05);
        PrimarySubtitleDelayIncreaseCommand = new RelayCommand(() => PrimarySubtitleDelaySeconds += 0.05);
        PrimarySubtitleDelayResetCommand = new RelayCommand(() => PrimarySubtitleDelaySeconds = 0);
        SecondarySubtitleDelayDecreaseCommand = new RelayCommand(() => SecondarySubtitleDelaySeconds -= 0.05);
        SecondarySubtitleDelayIncreaseCommand = new RelayCommand(() => SecondarySubtitleDelaySeconds += 0.05);
        SecondarySubtitleDelayResetCommand = new RelayCommand(() => SecondarySubtitleDelaySeconds = 0);
        PreviousCommand = new AsyncRelayCommand(PlayPreviousAsync);
        NextCommand = new AsyncRelayCommand(PlayNextAsync);
        PlaySelectedCommand = new AsyncRelayCommand(PlaySelectedAsync);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected);
        ClearPlaylistCommand = new RelayCommand(ClearPlaylist);
        SyncPlaylistToCloudCommand = new AsyncRelayCommand(() => SyncPlaylistToCloudAsync(force: true));
        LoadCloudPlaylistCommand = new AsyncRelayCommand(() => LoadCloudPlaylistAsync());
        UploadCloudPreferencesCommand = new AsyncRelayCommand(() => SaveCloudPreferencesAsync(explicitRequest: true));
        LoadCloudPreferencesCommand = new AsyncRelayCommand(() => LoadCloudPreferencesAsync(explicitRequest: true));
        ScreenshotCommand = new RelayCommand(TakeScreenshot);
        ResetVideoEqualizerCommand = new RelayCommand(ResetVideoEqualizer);
        AudioDeviceDelayDecreaseCommand = new RelayCommand(() => AdjustAudioDeviceDelay(-0.01));
        AudioDeviceDelayIncreaseCommand = new RelayCommand(() => AdjustAudioDeviceDelay(0.01));
        AudioDeviceDelayResetCommand = new RelayCommand(() => SetAudioDeviceDelay(0));
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
        ContactSheetCommand = new AsyncRelayCommand(CreateContactSheetAsync);
        ResetShortcutsCommand = new RelayCommand(ResetShortcuts);
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(_paths.LogDirectory, "Tanılama klasörü açılamadı."));
        OpenScreenshotFolderCommand = new RelayCommand(() => OpenFolder(_paths.ScreenshotDirectory, "Ekran görüntüsü klasörü açılamadı."));
        ShowMediaCenterCommand = new RelayCommand(ShowMediaCenter);
        ShowSourcesCommand = new RelayCommand(() => SelectedSidebarIndex = 0);
        ShowDiscoverCommand = new RelayCommand(() => SelectedSidebarIndex = 1);
        ShowLibraryCommand = new RelayCommand(() => SelectedSidebarIndex = 2);
        ShowPlaylistCommand = new RelayCommand(() => SelectedSidebarIndex = 3);
        ShowTelegramCommand = new RelayCommand(() => SelectedSidebarIndex = 5);
        ShowAddonsCommand = new RelayCommand(() => SelectedSidebarIndex = 7);
        ShowTorrentCommand = new RelayCommand(() => SelectedSidebarIndex = 8);
        ShowSettingsCommand = new RelayCommand(() => SelectedSidebarIndex = 10);
        ShowAccountCommand = new RelayCommand(() => SelectedSidebarIndex = 11);
        ShowCloudListsCommand = new AsyncRelayCommand(ShowCloudListsAsync);
        AddContentToSelectedCloudListCommand = new AsyncRelayCommand(OpenDiscoverForSelectedCloudListAsync);
        OpenDashboardLinkCommand = new RelayCommand(() => { SelectedSidebarIndex = 0; Sources.OpenPublicLinkCommand.Execute(null); });
        OpenDashboardMagnetCommand = new RelayCommand(() => { SelectedSidebarIndex = 8; Torrent.PrepareMagnetCommand.Execute(null); });
        OpenDashboardCloudCommand = new RelayCommand(() => { SelectedSidebarIndex = 0; Sources.OpenCloudCommand.Execute(null); });
        OpenDashboardSavedLinkCommand = new RelayCommand(() => { SelectedSidebarIndex = 0; Sources.OpenSavedLinkCommand.Execute(null); });
        OpenDashboardWebDavCommand = new RelayCommand(() => { SelectedSidebarIndex = 0; Sources.ConnectWebDavCommand.Execute(null); });

        _playback.SnapshotChanged += PlaybackOnSnapshotChanged;
        _playback.TracksChanged += PlaybackOnTracksChanged;
        _playback.ChaptersChanged += PlaybackOnChaptersChanged;
        _playback.PlaybackEnded += PlaybackOnPlaybackEnded;
        _playback.ErrorOccurred += PlaybackOnErrorOccurred;
        _ = RefreshContinueWatchingAsync();
        _ = EnrichHistoryArtworkAsync(HistoryItems.ToArray());
    }

    public ObservableCollection<MediaTrack> AudioTracks { get; }
    public ObservableCollection<MediaTrack> SubtitleTracks { get; }
    public ObservableCollection<MediaTrack> SecondarySubtitleTracks { get; }
    public ObservableCollection<ChapterInfo> Chapters { get; }
    public ObservableCollection<PlaylistItem> Playlist { get; }
    public ObservableCollection<RecentSourceEntry> RecentSources { get; }
    public ObservableCollection<HistoryItem> HistoryItems { get; }
    public ObservableCollection<ContinueWatchingItem> ContinueWatching { get; }
    public IReadOnlyList<double> SpeedOptions { get; }
    public IReadOnlyList<VideoProfileOption> VideoProfileOptions { get; }
    public IReadOnlyList<VideoEnhancementOption> VideoEnhancementOptions { get; }
    public IReadOnlyList<PlayerChoiceOption> HdrOutputOptions { get; }
    public IReadOnlyList<PlayerChoiceOption> ToneMappingOptions { get; }
    public IReadOnlyList<PlayerChoiceOption> ContactSheetSizeOptions { get; }
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; }
    public string? CurrentPlaybackSource => _currentPlaybackSource;
    public IReadOnlyDictionary<string, string>? CurrentPlaybackHeaders => _currentPlaybackHeaders;
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }
    public LibraryPanelViewModel Library { get; }
    public SubtitlePanelViewModel Subtitles { get; }
    public SourcePanelViewModel Sources { get; }
    public TelegramPanelViewModel Telegram { get; }
    public TorrentPanelViewModel Torrent { get; }
    public ConnectionsPanelViewModel Connections { get; }
    public AddonsPanelViewModel Addons { get; }
    public UpdatePanelViewModel Updates { get; }
    public ApiSettingsPanelViewModel ApiSettings { get; }
    public AccountPanelViewModel Account { get; }

    public ICommand OpenFileCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand AttachSubtitleCommand { get; }
    public ICommand AttachSecondarySubtitleCommand { get; }
    public ICommand AttachAudioCommand { get; }
    public ICommand BrowseRemoteAudioCommand { get; }
    public ICommand BrowseTelegramAudioCommand { get; }
    public ICommand ResyncAudioCommand { get; }
    public ICommand ResyncSubtitleCommand { get; }
    public ICommand AudioDelayDecreaseCommand { get; }
    public ICommand AudioDelayIncreaseCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand SeekBackwardCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand SeekBackwardMediumCommand { get; }
    public ICommand SeekForwardMediumCommand { get; }
    public ICommand SeekBackwardLongCommand { get; }
    public ICommand SeekForwardLongCommand { get; }
    public ICommand FrameBackwardCommand { get; }
    public ICommand FrameForwardCommand { get; }
    public ICommand GoToTimeCommand { get; }
    public ICommand PrimarySubtitleDelayDecreaseCommand { get; }
    public ICommand PrimarySubtitleDelayIncreaseCommand { get; }
    public ICommand PrimarySubtitleDelayResetCommand { get; }
    public ICommand SecondarySubtitleDelayDecreaseCommand { get; }
    public ICommand SecondarySubtitleDelayIncreaseCommand { get; }
    public ICommand SecondarySubtitleDelayResetCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PlaySelectedCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ClearPlaylistCommand { get; }
    public ICommand SyncPlaylistToCloudCommand { get; }
    public ICommand LoadCloudPlaylistCommand { get; }
    public ICommand UploadCloudPreferencesCommand { get; }
    public ICommand LoadCloudPreferencesCommand { get; }
    public ICommand ScreenshotCommand { get; }
    public ICommand ResetVideoEqualizerCommand { get; }
    public ICommand AudioDeviceDelayDecreaseCommand { get; }
    public ICommand AudioDeviceDelayIncreaseCommand { get; }
    public ICommand AudioDeviceDelayResetCommand { get; }
    public ICommand RefreshAudioDevicesCommand { get; }
    public ICommand ContactSheetCommand { get; }
    public ICommand ResetShortcutsCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenScreenshotFolderCommand { get; }
    public ICommand ShowMediaCenterCommand { get; }
    public ICommand ShowSourcesCommand { get; }
    public ICommand ShowDiscoverCommand { get; }
    public ICommand ShowLibraryCommand { get; }
    public ICommand ShowPlaylistCommand { get; }
    public ICommand ShowTelegramCommand { get; }
    public ICommand ShowAddonsCommand { get; }
    public ICommand ShowTorrentCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowAccountCommand { get; }
    public ICommand ShowCloudListsCommand { get; }
    public ICommand AddContentToSelectedCloudListCommand { get; }
    public ICommand OpenDashboardLinkCommand { get; }
    public ICommand OpenDashboardMagnetCommand { get; }
    public ICommand OpenDashboardCloudCommand { get; }
    public ICommand OpenDashboardSavedLinkCommand { get; }
    public ICommand OpenDashboardWebDavCommand { get; }

    public VideoProfileOption? SelectedVideoProfile
    {
        get => _selectedVideoProfile;
        set
        {
            if (!SetProperty(ref _selectedVideoProfile, value) || value is null) return;
            _settings.VideoProfileMode = value.Code;
            if (IsReady) ApplyVideoProfileForCurrentSource();
            _ = SaveSettingsAsync();
        }
    }

    public string VideoProfileSummary
    {
        get => _videoProfileSummary;
        private set => SetProperty(ref _videoProfileSummary, value);
    }

    public VideoEnhancementOption? SelectedVideoEnhancement
    {
        get => _selectedVideoEnhancement;
        set
        {
            if (!SetProperty(ref _selectedVideoEnhancement, value) || value is null) return;
            _settings.VideoEnhancementMode = value.Code;
            if (IsReady) ApplyVideoEnhancement(value);
            _ = SaveSettingsAsync();
        }
    }

    public double VideoBrightness
    {
        get => _settings.VideoBrightness;
        set => SetVideoEqualizerSetting(nameof(VideoBrightness), Math.Clamp(value, -100, 100));
    }

    public double VideoContrast
    {
        get => _settings.VideoContrast;
        set => SetVideoEqualizerSetting(nameof(VideoContrast), Math.Clamp(value, -100, 100));
    }

    public double VideoSaturation
    {
        get => _settings.VideoSaturation;
        set => SetVideoEqualizerSetting(nameof(VideoSaturation), Math.Clamp(value, -100, 100));
    }

    public double VideoGamma
    {
        get => _settings.VideoGamma;
        set => SetVideoEqualizerSetting(nameof(VideoGamma), Math.Clamp(value, -100, 100));
    }

    public PlayerChoiceOption? SelectedHdrOutputMode
    {
        get => _selectedHdrOutputMode;
        set
        {
            if (!SetProperty(ref _selectedHdrOutputMode, value) || value is null) return;
            _settings.HdrOutputMode = value.Code;
            if (IsReady) ApplyVideoProfileForCurrentSource();
            _ = SaveSettingsAsync();
        }
    }

    public PlayerChoiceOption? SelectedToneMappingMode
    {
        get => _selectedToneMappingMode;
        set
        {
            if (!SetProperty(ref _selectedToneMappingMode, value) || value is null) return;
            _settings.ToneMappingMode = value.Code;
            if (IsReady) ApplyVideoProfileForCurrentSource();
            _ = SaveSettingsAsync();
        }
    }

    public AudioDeviceInfo? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (!SetProperty(ref _selectedAudioDevice, value) || value is null || _suppressAudioDeviceSelection) return;
            _settings.AudioDeviceName = value.Name;
            if (IsReady) SafePlaybackAction(() => _playback.SetAudioDevice(value.Name));
            LoadAudioDeviceDelay(value.Name);
            _ = SaveSettingsAsync();
        }
    }

    public double AudioDeviceDelaySeconds
    {
        get => _audioDeviceDelaySeconds;
        set => SetAudioDeviceDelay(value);
    }

    public string AudioDeviceDelayText => $"{_audioDeviceDelaySeconds * 1000:+0;-0;0} ms";

    public PlayerChoiceOption? SelectedContactSheetSize
    {
        get => _selectedContactSheetSize;
        set
        {
            if (!SetProperty(ref _selectedContactSheetSize, value) || value is null) return;
            var parts = value.Code.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var columns) && int.TryParse(parts[1], out var rows))
            {
                _settings.ContactSheetColumns = Math.Clamp(columns, 4, 6);
                _settings.ContactSheetRows = Math.Clamp(rows, 4, 6);
                _ = SaveSettingsAsync();
            }
        }
    }

    public string ShortcutPlayPause { get => _settings.ShortcutPlayPause; set => SetShortcut(_settings.ShortcutPlayPause, value, nameof(ShortcutPlayPause), v => _settings.ShortcutPlayPause = v); }
    public string ShortcutSeekBackward { get => _settings.ShortcutSeekBackward; set => SetShortcut(_settings.ShortcutSeekBackward, value, nameof(ShortcutSeekBackward), v => _settings.ShortcutSeekBackward = v); }
    public string ShortcutSeekForward { get => _settings.ShortcutSeekForward; set => SetShortcut(_settings.ShortcutSeekForward, value, nameof(ShortcutSeekForward), v => _settings.ShortcutSeekForward = v); }
    public string ShortcutSeekBackwardMedium { get => _settings.ShortcutSeekBackwardMedium; set => SetShortcut(_settings.ShortcutSeekBackwardMedium, value, nameof(ShortcutSeekBackwardMedium), v => _settings.ShortcutSeekBackwardMedium = v); }
    public string ShortcutSeekForwardMedium { get => _settings.ShortcutSeekForwardMedium; set => SetShortcut(_settings.ShortcutSeekForwardMedium, value, nameof(ShortcutSeekForwardMedium), v => _settings.ShortcutSeekForwardMedium = v); }
    public string ShortcutSeekBackwardLong { get => _settings.ShortcutSeekBackwardLong; set => SetShortcut(_settings.ShortcutSeekBackwardLong, value, nameof(ShortcutSeekBackwardLong), v => _settings.ShortcutSeekBackwardLong = v); }
    public string ShortcutSeekForwardLong { get => _settings.ShortcutSeekForwardLong; set => SetShortcut(_settings.ShortcutSeekForwardLong, value, nameof(ShortcutSeekForwardLong), v => _settings.ShortcutSeekForwardLong = v); }
    public string ShortcutScreenshot { get => _settings.ShortcutScreenshot; set => SetShortcut(_settings.ShortcutScreenshot, value, nameof(ShortcutScreenshot), v => _settings.ShortcutScreenshot = v); }
    public string ShortcutFrameBackward { get => _settings.ShortcutFrameBackward; set => SetShortcut(_settings.ShortcutFrameBackward, value, nameof(ShortcutFrameBackward), v => _settings.ShortcutFrameBackward = v); }
    public string ShortcutFrameForward { get => _settings.ShortcutFrameForward; set => SetShortcut(_settings.ShortcutFrameForward, value, nameof(ShortcutFrameForward), v => _settings.ShortcutFrameForward = v); }
    public string ShortcutFullscreen { get => _settings.ShortcutFullscreen; set => SetShortcut(_settings.ShortcutFullscreen, value, nameof(ShortcutFullscreen), v => _settings.ShortcutFullscreen = v); }
    public string ShortcutMiniPlayer { get => _settings.ShortcutMiniPlayer; set => SetShortcut(_settings.ShortcutMiniPlayer, value, nameof(ShortcutMiniPlayer), v => _settings.ShortcutMiniPlayer = v); }
    public string ShortcutPlaylist { get => _settings.ShortcutPlaylist; set => SetShortcut(_settings.ShortcutPlaylist, value, nameof(ShortcutPlaylist), v => _settings.ShortcutPlaylist = v); }

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value) || value is null)
            {
                return;
            }

            _settings.UiLanguage = value.Code;
            LocalizationManager.ApplyLanguage(value.Code);
            RefreshLocalization();
            if (_playback.IsInitialized) RefreshAudioDevices();
            _ = SaveSettingsAsync();
            QueueCloudPreferencesSync();
        }
    }

    public int SelectedSidebarIndex
    {
        get => _selectedSidebarIndex;
        set
        {
            var normalized = Math.Clamp(value, 0, 12);
            if (!SetProperty(ref _selectedSidebarIndex, normalized))
            {
                return;
            }

            if (normalized == 0)
            {
                _ = RefreshContinueWatchingAsync();
            }
            else if (normalized == 1)
            {
                _ = Addons.RefreshCloudCollectionsAsync();
            }
            else if (normalized == 2)
            {
                _ = Library.ShowLocalLibraryAsync();
            }

            if (normalized == 6 && !Connections.IsBusy)
            {
                _ = Connections.EnsureLoadedAsync();
            }

            if ((normalized == 1 || normalized == 7) && !Addons.IsBusy)
            {
                _ = Addons.EnsureLoadedAsync();
            }
        }
    }

    public PlaybackSnapshot Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetProperty(ref _snapshot, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(DurationSeconds));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(PlayPauseText));
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(IsBuffering));
            OnPropertyChanged(nameof(HasMedia));
        }
    }

    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    public bool IsMediaCenterVisible
    {
        get => _isMediaCenterVisible;
        private set => SetProperty(ref _isMediaCenterVisible, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool AutoAudioSyncEnabled
    {
        get => _settings.AutoExternalAudioSync;
        set
        {
            if (_settings.AutoExternalAudioSync == value) return;
            _settings.AutoExternalAudioSync = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
            if (!value)
            {
                CancelAudioSyncAnalysis();
                _audioSyncResult = null;
                _manualAudioDelaySeconds = 0;
                ClearAudioSyncCorrection();
                ApplyAudioDelay(0, force: true);
                AudioSyncStatus = "Otomatik ses senkronu kapalı.";
            }
            else if (!string.IsNullOrWhiteSpace(_externalAudioSource))
            {
                AudioSyncStatus = LocalizationManager.Get("Player.AudioSync.ManualReady");
            }
        }
    }

    public bool AutoSubtitleSyncEnabled
    {
        get => _settings.AutoSubtitleSync;
        set
        {
            if (_settings.AutoSubtitleSync == value) return;
            _settings.AutoSubtitleSync = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
            QueueCloudPreferencesSync();
            if (!value)
            {
                CancelSubtitleSync();
                SubtitleSyncStatus = LocalizationManager.Get("Player.SubtitleSync.State.Disabled");
            }
        }
    }

    public bool IsSubtitleSyncBusy
    {
        get => _isSubtitleSyncBusy;
        private set
        {
            if (SetProperty(ref _isSubtitleSyncBusy, value))
            {
                OnPropertyChanged(nameof(SubtitleSyncProgressSummaryText));
                OnPropertyChanged(nameof(SubtitleSyncGlyphText));
                OnPropertyChanged(nameof(SubtitleSyncActionText));
                OnPropertyChanged(nameof(IsSubtitleSyncActionComplete));
                OnPropertyChanged(nameof(IsSubtitleSyncActionRetry));
            }
        }
    }

    public bool IsSubtitleSyncVisible => _hasSubtitleSyncSession;

    public string SubtitleSyncActionText
    {
        get
        {
            if (IsSubtitleSyncBusy)
            {
                return LocalizationManager.Get("Player.SubtitleSync.Action.Running");
            }

            return _subtitleSyncResult?.Verdict switch
            {
                SubtitleSyncVerdict.Synchronized or SubtitleSyncVerdict.AlreadyAligned =>
                    LocalizationManager.Get("Player.SubtitleSync.Action.Resync"),
                SubtitleSyncVerdict.Uncertain or SubtitleSyncVerdict.NoMatch or SubtitleSyncVerdict.Unavailable =>
                    LocalizationManager.Get("Player.SubtitleSync.Action.Retry"),
                _ => LocalizationManager.Get("Player.SubtitleSync.Action"),
            };
        }
    }

    public bool IsSubtitleSyncActionComplete =>
        !IsSubtitleSyncBusy &&
        _subtitleSyncResult?.Verdict is SubtitleSyncVerdict.Synchronized or SubtitleSyncVerdict.AlreadyAligned;

    public bool IsSubtitleSyncActionRetry =>
        !IsSubtitleSyncBusy &&
        _subtitleSyncResult?.Verdict is SubtitleSyncVerdict.Uncertain or SubtitleSyncVerdict.NoMatch or SubtitleSyncVerdict.Unavailable;

    public int SubtitleSyncProgress
    {
        get => _subtitleSyncProgress;
        private set
        {
            if (SetProperty(ref _subtitleSyncProgress, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(SubtitleSyncProgressSummaryText));
            }
        }
    }

    public string SubtitleSyncStageText
    {
        get => _subtitleSyncStage;
        private set => SetProperty(ref _subtitleSyncStage, value);
    }

    public string SubtitleSyncStatus
    {
        get => _subtitleSyncStatus;
        private set => SetProperty(ref _subtitleSyncStatus, value);
    }

    public string SubtitleSyncProgressSummaryText
    {
        get
        {
            if (IsSubtitleSyncBusy)
            {
                return $"{LocalizationManager.Get("Player.SubtitleSync.State.Analyzing")} · {SubtitleSyncProgress}%";
            }

            return _subtitleSyncResult?.Verdict switch
            {
                SubtitleSyncVerdict.Synchronized => LocalizationManager.Get("Player.SubtitleSync.State.Synchronized"),
                SubtitleSyncVerdict.AlreadyAligned => LocalizationManager.Get("Player.SubtitleSync.State.AlreadyAligned"),
                SubtitleSyncVerdict.NoMatch => LocalizationManager.Get("Player.SubtitleSync.State.NoMatch"),
                SubtitleSyncVerdict.Uncertain => LocalizationManager.Get("Player.SubtitleSync.State.Uncertain"),
                SubtitleSyncVerdict.Unavailable => LocalizationManager.Get("Player.SubtitleSync.State.Unavailable"),
                _ => LocalizationManager.Get("Player.SubtitleSync.State.Ready"),
            };
        }
    }

    public string SubtitleSyncGlyphText => IsSubtitleSyncBusy ? "↻" : _subtitleSyncResult?.Verdict switch
    {
        SubtitleSyncVerdict.Synchronized or SubtitleSyncVerdict.AlreadyAligned => "✓",
        SubtitleSyncVerdict.NoMatch => "×",
        SubtitleSyncVerdict.Uncertain or SubtitleSyncVerdict.Unavailable => "!",
        _ => "S",
    };

    public string AudioSyncStatus
    {
        get => _audioSyncStatus;
        private set
        {
            if (SetProperty(ref _audioSyncStatus, value))
            {
                OnPropertyChanged(nameof(AudioSyncDiagnosticsText));
            }
        }
    }

    public bool IsAudioSyncAnalyzing
    {
        get => _isAudioSyncAnalyzing;
        private set
        {
            if (SetProperty(ref _isAudioSyncAnalyzing, value))
            {
                OnPropertyChanged(nameof(IsAudioSyncBusy));
                NotifyAudioSyncDiagnosticsChanged();
            }
        }
    }

    public bool IsAudioSyncPreparing
    {
        get => _isAudioSyncPreparing;
        private set
        {
            if (SetProperty(ref _isAudioSyncPreparing, value))
            {
                OnPropertyChanged(nameof(IsAudioSyncBusy));
                NotifyAudioSyncDiagnosticsChanged();
            }
        }
    }

    public bool IsAudioSyncBusy => IsAudioSyncAnalyzing || IsAudioSyncPreparing;

    public int AudioSyncProgress
    {
        get => _audioSyncProgress;
        private set
        {
            if (SetProperty(ref _audioSyncProgress, Math.Clamp(value, 0, 100)))
            {
                NotifyAudioSyncDiagnosticsChanged();
                OnPropertyChanged(nameof(AudioSyncProgressSummaryText));
                OnPropertyChanged(nameof(AudioSyncStageText));
                OnPropertyChanged(nameof(AudioSyncEtaText));
            }
        }
    }

    public bool HasExternalAudio => !string.IsNullOrWhiteSpace(_externalAudioSource);

    public string AudioSyncActionText
    {
        get
        {
            if (IsAudioSyncBusy)
            {
                return LocalizationManager.Get("Player.AudioSync.Action.Running");
            }

            if (_audioSyncFinalized &&
                _audioSyncSafety?.Classification == AudioSyncSafetyClass.Reliable)
            {
                return LocalizationManager.Get("Player.AudioSync.Action.Resync");
            }

            if (_audioSyncFinalized && _audioSyncResult is not null)
            {
                return LocalizationManager.Get("Player.AudioSync.Action.Retry");
            }

            return LocalizationManager.Get("Player.AudioSync.Action");
        }
    }

    public bool IsAudioSyncActionComplete =>
        !IsAudioSyncBusy &&
        _audioSyncFinalized &&
        _audioSyncSafety?.Classification == AudioSyncSafetyClass.Reliable;

    public bool IsAudioSyncActionRetry =>
        !IsAudioSyncBusy &&
        _audioSyncFinalized &&
        _audioSyncResult is not null &&
        _audioSyncSafety?.Classification != AudioSyncSafetyClass.Reliable;

    public string AudioSyncEngineText
    {
        get
        {
            if (_audioSyncResult is not null && !string.IsNullOrWhiteSpace(_audioSyncResult.Engine))
            {
                return _audioSyncResult.Engine.StartsWith("AudioSyncTool", StringComparison.OrdinalIgnoreCase)
                    ? "AST 2.5"
                    : "C# yedek";
            }

            return _audioSyncService.EngineName.StartsWith("AudioSyncTool", StringComparison.OrdinalIgnoreCase)
                ? "AST 2.5"
                : "C# yedek";
        }
    }

    public string AudioSyncOffsetText
    {
        get
        {
            if (IsAudioSyncBusy)
            {
                return "—";
            }

            if (_audioSyncApplicationKind == AudioSyncApplicationKind.None &&
                _audioSyncResult is not { CanApplyLive: true } &&
                Math.Abs(_manualAudioDelaySeconds) < 0.0005)
            {
                return "—";
            }

            return Math.Abs(_appliedAudioDelaySeconds) < 0.0005
                ? "0 ms"
                : $"{_appliedAudioDelaySeconds * 1000:+0;-0;0} ms";
        }
    }

    public string AudioSyncStateText
    {
        get
        {
            if (!HasExternalAudio) return LocalizationManager.Get("Player.AudioSync.State.NoAudio");
            if (_audioSyncApplicationKind == AudioSyncApplicationKind.Progressive) return "Bölgesel senkron";
            if (IsAudioSyncAnalyzing)
            {
                return LocalizationManager.Get("Player.AudioSync.State.Analyzing");
            }
            if (IsAudioSyncPreparing)
            {
                return LocalizationManager.Get("Player.AudioSync.State.Preparing");
            }

            if (_audioSyncSafety?.Classification == AudioSyncSafetyClass.DifferentCut)
                return "Farklı kurgu";
            if (_audioSyncSafety?.Classification == AudioSyncSafetyClass.Rejected)
                return "Güvenli değil";

            return _audioSyncResult?.Verdict switch
            {
                AudioSyncVerdict.Reliable => LocalizationManager.Get("Player.AudioSync.State.Synchronized"),
                AudioSyncVerdict.Uncertain => LocalizationManager.Get("Player.AudioSync.State.Uncertain"),
                AudioSyncVerdict.NoMatch => LocalizationManager.Get("Player.AudioSync.State.NoMatch"),
                AudioSyncVerdict.Unavailable => LocalizationManager.Get("Player.AudioSync.State.Unavailable"),
                _ => LocalizationManager.Get("Player.AudioSync.State.Ready"),
            };
        }
    }

    public string AudioSyncProgressText => IsAudioSyncBusy ? $"{LocalizationManager.Get("Player.AudioSync.Progress")} {AudioSyncProgress}%" : string.Empty;

    public string AudioSyncProgressSummaryText => IsAudioSyncBusy
        ? $"{AudioSyncStateText} · {AudioSyncProgress}%"
        : AudioSyncStateText;

    public string AudioSyncStageText
    {
        get
        {
            if (IsAudioSyncPreparing)
                return LocalizationManager.Get("Player.AudioSync.Phase.PreparingVerified");
            if (IsAudioSyncAnalyzing)
                return GetAudioSyncPhaseText();
            return AudioSyncUserMessage;
        }
    }

    public string AudioSyncEtaText
    {
        get
        {
            if (!IsAudioSyncBusy) return string.Empty;
            if (_audioSyncProgressStartedAt == DateTimeOffset.MinValue || AudioSyncProgress < 3)
                return LocalizationManager.Get("Player.AudioSync.Eta.Calculating");

            var elapsed = DateTimeOffset.UtcNow - _audioSyncProgressStartedAt;
            if (elapsed.TotalSeconds < 2)
                return LocalizationManager.Get("Player.AudioSync.Eta.Calculating");

            var rate = AudioSyncProgress / elapsed.TotalSeconds;
            if (rate <= 0.0001)
                return LocalizationManager.Get("Player.AudioSync.Eta.Calculating");

            var remaining = Math.Clamp((100 - AudioSyncProgress) / rate, 0, 60 * 60);
            if (remaining < 2)
                return LocalizationManager.Get("Player.AudioSync.Eta.FinalStage");

            if (remaining < 60)
            {
                var seconds = Math.Max(5, (int)(Math.Ceiling(remaining / 5.0) * 5));
                return $"{LocalizationManager.Get("Player.AudioSync.Eta.About")} {seconds} {LocalizationManager.Get("Player.AudioSync.Eta.SecondsRemaining")}";
            }

            var minutes = Math.Max(1, (int)Math.Ceiling(remaining / 60.0));
            return $"{LocalizationManager.Get("Player.AudioSync.Eta.About")} {minutes} {LocalizationManager.Get("Player.AudioSync.Eta.MinutesRemaining")}";
        }
    }

    public string AudioSyncGlyphText
    {
        get
        {
            if (IsAudioSyncBusy) return "↻";
            if (_audioSyncFinalized &&
                _audioSyncSafety?.Classification is AudioSyncSafetyClass.DifferentCut or AudioSyncSafetyClass.Rejected)
                return "!";
            return _audioSyncResult?.Verdict switch
            {
                AudioSyncVerdict.Reliable => "✓",
                AudioSyncVerdict.Uncertain => "!",
                AudioSyncVerdict.NoMatch => "!",
                AudioSyncVerdict.Unavailable => "!",
                _ => "♪",
            };
        }
    }

    public string AudioSyncUserMessage
    {
        get
        {
            if (!HasExternalAudio) return LocalizationManager.Get("Player.AudioSync.User.NoAudio");
            if (_audioSyncApplicationKind == AudioSyncApplicationKind.Progressive) return AudioSyncStatus;
            if (IsAudioSyncAnalyzing) return LocalizationManager.Get("Player.AudioSync.User.Analyzing");
            if (IsAudioSyncPreparing) return LocalizationManager.Get("Player.AudioSync.User.Preparing");

            if (_audioSyncResult is null)
                return LocalizationManager.Get("Player.AudioSync.User.Ready");

            if (_audioSyncFinalized &&
                _audioSyncSafety?.Classification is AudioSyncSafetyClass.DifferentCut or AudioSyncSafetyClass.Rejected)
                return _audioSyncSafety.UserMessage;

            if (_audioSyncResult.Verdict == AudioSyncVerdict.NoMatch)
            {
                if (AudioSyncStatus.StartsWith("Hybrid Edit Recovery tamamlanamadı", StringComparison.OrdinalIgnoreCase) ||
                    AudioSyncStatus.StartsWith("Zorlu senkron tamamlanamadı", StringComparison.OrdinalIgnoreCase))
                {
                    return AudioSyncStatus;
                }
                return LocalizationManager.Get("Player.AudioSync.User.NoMatch");
            }
            if (_audioSyncResult.Verdict == AudioSyncVerdict.Uncertain)
                return LocalizationManager.Get("Player.AudioSync.User.Uncertain");
            if (_audioSyncResult.Verdict == AudioSyncVerdict.Unavailable)
                return LocalizationManager.Get("Player.AudioSync.User.Unavailable");

            return _audioSyncApplicationKind switch
            {
                AudioSyncApplicationKind.FixedDelay => LocalizationManager.Get("Player.AudioSync.User.Fixed"),
                AudioSyncApplicationKind.LiveTempo => LocalizationManager.Get("Player.AudioSync.User.LiveTempo"),
                AudioSyncApplicationKind.PreparedTrack => LocalizationManager.Get("Player.AudioSync.User.Prepared"),
                _ => LocalizationManager.Get("Player.AudioSync.User.Synchronized"),
            };
        }
    }

    public string AudioSyncDiagnosticsText
    {
        get
        {
            if (!HasExternalAudio) return string.Empty;
            if (IsAudioSyncAnalyzing) return GetAudioSyncPhaseText();
            if (IsAudioSyncPreparing) return $"AudioSyncTool tam senkron dosyası hazırlanıyor · {AudioSyncProgress}%";
            if (_audioSyncResult is null) return AudioSyncStatus;

            var result = _audioSyncResult;
            var parts = new List<string>
            {
                result.Engine,
                $"{LocalizationManager.Get("Player.AudioSync.Label.Model")}: {GetAudioSyncModelText(result.Model)}",
                $"{LocalizationManager.Get("Player.AudioSync.Label.Offset")}: {result.BaseDelaySeconds * 1000:+0;-0;0} ms",
            };

            if (_audioSyncSafety is not null)
                parts.Add($"Güvenlik: {_audioSyncSafety.Label} ({_audioSyncSafety.TechnicalReason})");

            if (_audioSyncApplicationKind == AudioSyncApplicationKind.LiveTempo)
                parts.Add($"AST tempo: {_audioSyncTempoFactor:0.#########}x");
            else if (_audioSyncApplicationKind == AudioSyncApplicationKind.PreparedTrack)
                parts.Add("AST tam düzeltme: aktif");
            else if (_audioSyncApplicationKind == AudioSyncApplicationKind.Progressive)
            {
                var trusted = _progressiveAudioRegions.Count(region => IsProgressiveRegionTrusted(region));
                var matched = _progressiveAudioRegions.Count(region => !region.MissingSource);
                parts.Add($"Progressive Adaptive Sync: {trusted}/{matched} doğrulanmış Türkçe bölge");
                if (_isLiveProgressiveAnalyzing && _liveProgressiveAnalyzingRegionIndex >= 0)
                    parts.Add($"Live Progressive: bölge {_liveProgressiveAnalyzingRegionIndex + 1} arka planda analiz ediliyor");
                if (_progressiveAudioRegionIndex >= 0 && _progressiveAudioRegionIndex < _progressiveAudioRegions.Count)
                {
                    var current = _progressiveAudioRegions[_progressiveAudioRegionIndex];
                    parts.Add(current.MissingSource || !IsProgressiveRegionTrusted(current)
                        ? "Geçerli bölge: orijinal ses"
                        : $"Geçerli bölge: {FormatTime(current.TargetStartSeconds)}–{FormatTime(current.TargetEndSeconds)}");
                }
            }

            if (_preparedAudio?.VerifiedResidualSeconds is double residual)
                parts.Add($"Kalan fark: {residual * 1000:+0.0;-0.0;0.0} ms/{_preparedAudio.VerifiedProbes}");
            if (_preparedAudio?.HybridRecovery == true)
            {
                var recoveryLabel = _preparedAudio.RecoveryKind.ToLowerInvariant() switch
                {
                    "multimodal" => "Multi-Modal Timeline",
                    "audio_timeline" => "Waveform Timeline",
                    "visual" => "Visual Timeline",
                    _ => "Hybrid Recovery",
                };
                parts.Add($"{recoveryLabel}: %{_preparedAudio.HybridCoverage * 100:0}");
                if (_preparedAudio.TimelineAnchors > 0)
                    parts.Add($"Timeline anchor: {_preparedAudio.TimelineAnchors}");
                if (_preparedAudio.VisualAnchors > 0)
                    parts.Add($"Görsel anchor: {_preparedAudio.VisualAnchors}");
                if (_preparedAudio.WaveformAnchors > 0)
                    parts.Add($"Waveform anchor: {_preparedAudio.WaveformAnchors}");
                if (_preparedAudio.DtwRegions > 0)
                    parts.Add($"DTW bölgesi: {_preparedAudio.DtwRegions}");
                if (_preparedAudio.HybridMissingRegions > 0)
                    parts.Add($"Orijinal ses kullanılan bölge: {_preparedAudio.HybridMissingRegions}");
            }

            if (result.Model == AudioSyncModelKind.LinearDrift || Math.Abs(result.DriftMillisecondsPerMinute) >= 0.05)
            {
                parts.Add($"{LocalizationManager.Get("Player.AudioSync.Label.Drift")}: {result.DriftMillisecondsPerMinute:+0.00;-0.00;0.00} {LocalizationManager.Get("Player.AudioSync.Unit.MsPerMin")}");
            }

            var confidence = result.RawConfidence > 0 ? result.RawConfidence : result.Confidence;
            parts.Add($"{LocalizationManager.Get("Player.AudioSync.Label.Confidence")}: {confidence:0.00}");

            if (result.TotalSegments > 0)
            {
                parts.Add($"{LocalizationManager.Get("Player.AudioSync.Label.Segments")}: {result.UsedSegments}/{result.TotalSegments}");
            }

            if (result.PhatProbes > 0)
            {
                parts.Add($"GCC-PHAT: {result.PhatProbes}");
            }

            if (!string.IsNullOrWhiteSpace(result.SuspectedFpsConversion))
            {
                parts.Add($"FPS: {result.SuspectedFpsConversion}");
            }

            if (result.Regions.Count > 1)
            {
                parts.Add($"{LocalizationManager.Get("Player.AudioSync.Label.Regions")}: {result.Regions.Count}");
            }

            if (result.VerdictReasons is { Count: > 0 })
            {
                parts.Add(string.Join(", ", result.VerdictReasons.Take(2)));
            }

            if (result.Verdict == AudioSyncVerdict.NoMatch &&
                !string.IsNullOrWhiteSpace(AudioSyncStatus) &&
                !AudioSyncStatus.Equals(result.Message, StringComparison.Ordinal))
            {
                parts.Add(AudioSyncStatus);
            }

            return string.Join(" · ", parts);
        }
    }

    public string CurrentTitle => Snapshot.Title;
    public double PositionSeconds => Snapshot.PositionSeconds;
    public double DurationSeconds => Math.Max(1, Snapshot.DurationSeconds);
    public string PositionText => FormatDisplayTime(Snapshot.PositionSeconds, _settings.TimeDisplayPrecision);
    public string DurationText => FormatDisplayTime(Snapshot.DurationSeconds, _settings.TimeDisplayPrecision);
    public int TimeDisplayPrecision => _settings.TimeDisplayPrecision;
    public string PlayPauseText => Snapshot.IsPaused ? "▶" : "Ⅱ";
    public bool IsBuffering => Snapshot.IsBuffering;
    public bool HasMedia => !string.IsNullOrWhiteSpace(Snapshot.Source);
    public string PlaylistSummary => Playlist.Count == 0 ? LocalizationManager.Get("Tab.Playlist") : $"{Playlist.Count}";


    public void ToggleTimeDisplayPrecision()
    {
        _settings.TimeDisplayPrecision = (_settings.TimeDisplayPrecision + 1) % 3;
        OnPropertyChanged(nameof(TimeDisplayPrecision));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        _ = SaveSettingsAsync();
        QueueCloudPreferencesSync();
    }

    public bool KeepWindowOnTop
    {
        get => _settings.KeepWindowOnTop;
        set
        {
            if (_settings.KeepWindowOnTop == value)
            {
                return;
            }

            _settings.KeepWindowOnTop = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public bool AutoPlayNext
    {
        get => _settings.AutoPlayNext;
        set
        {
            if (_settings.AutoPlayNext == value)
            {
                return;
            }

            _settings.AutoPlayNext = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
            QueueCloudPreferencesSync();
        }
    }

    public bool PlaylistVisible
    {
        get => _settings.PlaylistVisible;
        set
        {
            if (_settings.PlaylistVisible == value)
            {
                return;
            }

            _settings.PlaylistVisible = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public double Volume
    {
        get => _settings.Volume;
        set
        {
            var adjusted = Math.Clamp(value, 0, VolumeBoostLimit);
            if (Math.Abs(_settings.Volume - adjusted) < 0.01)
            {
                return;
            }

            _settings.Volume = adjusted;
            OnPropertyChanged();
            if (IsReady)
            {
                SafePlaybackAction(() => _playback.SetVolume(adjusted));
            }

            _ = SaveSettingsAsync();
        }
    }

    public int VolumeBoostLimit
    {
        get => Math.Clamp(_settings.VolumeBoostLimit, 100, 150);
        set
        {
            var adjusted = Math.Clamp(value, 100, 150);
            if (_settings.VolumeBoostLimit == adjusted) return;
            _settings.VolumeBoostLimit = adjusted;
            if (_settings.Volume > adjusted) Volume = adjusted;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public bool AutoAddFolderVideos
    {
        get => _settings.AutoAddFolderVideos;
        set
        {
            if (_settings.AutoAddFolderVideos == value) return;
            _settings.AutoAddFolderVideos = value;
            OnPropertyChanged();
            _ = SaveSettingsAsync();
        }
    }

    public int SeekShortSeconds
    {
        get => Math.Clamp(_settings.SeekShortSeconds, 1, 120);
        set { _settings.SeekShortSeconds = Math.Clamp(value, 1, 120); OnPropertyChanged(); OnPropertyChanged(nameof(SeekBackwardLabel)); OnPropertyChanged(nameof(SeekForwardLabel)); _ = SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public int SeekMediumSeconds
    {
        get => Math.Clamp(_settings.SeekMediumSeconds, 1, 600);
        set { _settings.SeekMediumSeconds = Math.Clamp(value, 1, 600); OnPropertyChanged(); _ = SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public int SeekLongSeconds
    {
        get => Math.Clamp(_settings.SeekLongSeconds, 1, 1800);
        set { _settings.SeekLongSeconds = Math.Clamp(value, 1, 1800); OnPropertyChanged(); _ = SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public string SeekBackwardLabel => $"−{SeekShortSeconds}";
    public string SeekForwardLabel => $"+{SeekShortSeconds}";

    public string GoToTimeText
    {
        get => _goToTimeText;
        set => SetProperty(ref _goToTimeText, value ?? string.Empty);
    }

    public double PrimarySubtitleDelaySeconds
    {
        get => _settings.PrimarySubtitleDelaySeconds;
        set { var v = Math.Clamp(value, -120, 120); if (Math.Abs(_settings.PrimarySubtitleDelaySeconds-v)<0.0001) return; _settings.PrimarySubtitleDelaySeconds=v; OnPropertyChanged(); OnPropertyChanged(nameof(PrimarySubtitleDelayText)); if (IsReady) SafePlaybackAction(() => _playback.SetSubtitleDelay(v)); _ = SaveSettingsAsync(); }
    }
    public double SecondarySubtitleDelaySeconds
    {
        get => _settings.SecondarySubtitleDelaySeconds;
        set { var v = Math.Clamp(value, -120, 120); if (Math.Abs(_settings.SecondarySubtitleDelaySeconds-v)<0.0001) return; _settings.SecondarySubtitleDelaySeconds=v; OnPropertyChanged(); OnPropertyChanged(nameof(SecondarySubtitleDelayText)); if (IsReady) SafePlaybackAction(() => _playback.SetSubtitleDelay(v, true)); _ = SaveSettingsAsync(); }
    }
    public string PrimarySubtitleDelayText => $"{PrimarySubtitleDelaySeconds:+0.00;-0.00;0.00} sn";
    public string SecondarySubtitleDelayText => $"{SecondarySubtitleDelaySeconds:+0.00;-0.00;0.00} sn";

    public double PrimarySubtitleScale
    {
        get => _settings.PrimarySubtitleScale;
        set { var v=Math.Clamp(value,0.5,2.0); if(Math.Abs(_settings.PrimarySubtitleScale-v)<0.001)return; _settings.PrimarySubtitleScale=v; OnPropertyChanged(); if(IsReady)SafePlaybackAction(()=>_playback.SetSubtitleScale(v)); _=SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public double SecondarySubtitleScale
    {
        get => _settings.SecondarySubtitleScale;
        set { var v=Math.Clamp(value,0.5,2.0); if(Math.Abs(_settings.SecondarySubtitleScale-v)<0.001)return; _settings.SecondarySubtitleScale=v; OnPropertyChanged(); if(IsReady)SafePlaybackAction(()=>_playback.SetSubtitleScale(v,true)); _=SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public double PrimarySubtitlePosition
    {
        get => _settings.PrimarySubtitlePosition;
        set { var v=Math.Clamp(value,0,150); if(Math.Abs(_settings.PrimarySubtitlePosition-v)<0.1)return; _settings.PrimarySubtitlePosition=v; OnPropertyChanged(); if(IsReady)SafePlaybackAction(()=>_playback.SetSubtitlePosition(v)); _=SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public double SecondarySubtitlePosition
    {
        get => _settings.SecondarySubtitlePosition;
        set { var v=Math.Clamp(value,0,150); if(Math.Abs(_settings.SecondarySubtitlePosition-v)<0.1)return; _settings.SecondarySubtitlePosition=v; OnPropertyChanged(); if(IsReady)SafePlaybackAction(()=>_playback.SetSubtitlePosition(v,true)); _=SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public bool PreservePrimaryAssStyle
    {
        get => _settings.PreservePrimaryAssStyle;
        set { if(_settings.PreservePrimaryAssStyle==value)return; _settings.PreservePrimaryAssStyle=value; OnPropertyChanged(); if(IsReady)SafePlaybackAction(()=>_playback.SetSubtitleAssOverride(value)); _=SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }
    public bool PreserveSecondaryAssStyle
    {
        get => _settings.PreserveSecondaryAssStyle;
        set { if(_settings.PreserveSecondaryAssStyle==value)return; _settings.PreserveSecondaryAssStyle=value; OnPropertyChanged(); if(IsReady)SafePlaybackAction(()=>_playback.SetSubtitleAssOverride(value,true)); _=SaveSettingsAsync(); QueueCloudPreferencesSync(); }
    }

    public double SelectedSpeed
    {
        get => _settings.PlaybackSpeed;
        set
        {
            var adjusted = Math.Clamp(value, 0.25, 4);
            if (Math.Abs(_settings.PlaybackSpeed - adjusted) < 0.001)
            {
                return;
            }

            _settings.PlaybackSpeed = adjusted;
            OnPropertyChanged();
            if (IsReady)
            {
                SafePlaybackAction(() => _playback.SetSpeed(adjusted));
            }

            _ = SaveSettingsAsync();
        }
    }

    public MediaTrack? SelectedAudioTrack
    {
        get => _selectedAudioTrack;
        set
        {
            if (!SetProperty(ref _selectedAudioTrack, value) || _suppressTrackSelection || value is null)
            {
                return;
            }

            SafePlaybackAction(() => _playback.SelectAudioTrack(value.Id));
        }
    }

    public MediaTrack? SelectedSubtitleTrack
    {
        get => _selectedSubtitleTrack;
        set
        {
            if (!SetProperty(ref _selectedSubtitleTrack, value) || _suppressTrackSelection || value is null)
            {
                return;
            }

            SafePlaybackAction(() => _playback.SelectSubtitleTrack(value.IsDisabled ? null : value.Id));
        }
    }

    public MediaTrack? SelectedSecondarySubtitleTrack
    {
        get => _selectedSecondarySubtitleTrack;
        set
        {
            if (!SetProperty(ref _selectedSecondarySubtitleTrack, value) || _suppressTrackSelection || value is null) return;
            var id = value.IsDisabled ? (long?)null : value.Id;
            if (id.HasValue && _selectedSubtitleTrack is { IsDisabled: false } primary && primary.Id == id.Value)
            {
                id = null;
                _selectedSecondarySubtitleTrack = SecondarySubtitleTracks.FirstOrDefault(track => track.IsDisabled);
                OnPropertyChanged();
            }
            _selectedSecondarySubtitleTrackId = id;
            SafePlaybackAction(() => _playback.SelectSecondarySubtitleTrack(id));
        }
    }

    public PlaylistItem? SelectedPlaylistItem
    {
        get => _selectedPlaylistItem;
        set
        {
            if (!SetProperty(ref _selectedPlaylistItem, value)) return;
            QueueCloudPlaylistSync();
        }
    }

    public string CloudPlaylistStatus
    {
        get => _cloudPlaylistStatus;
        private set => SetProperty(ref _cloudPlaylistStatus, value);
    }

    public string CloudPlaylistCountText =>
        LocalizationManager.Format("Cloud.Playlist.Count", _cloudPlaylistCount);

    public string CloudPreferencesStatus
    {
        get => _cloudPreferencesStatus;
        private set => SetProperty(ref _cloudPreferencesStatus, value);
    }

    public string CloudPreferencesLastSyncText => _cloudPreferencesLastSyncAt is null
        ? LocalizationManager.Get("Cloud.Preferences.NotSynced")
        : LocalizationManager.Format("Cloud.Preferences.LastSync", _cloudPreferencesLastSyncAt.Value.LocalDateTime);

    public ChapterInfo? SelectedChapter
    {
        get => _selectedChapter;
        set
        {
            if (!SetProperty(ref _selectedChapter, value) || _suppressChapterSelection || value is null)
            {
                return;
            }

            SafePlaybackAction(() => _playback.SelectChapter(value.Id));
        }
    }

    public async Task PrepareAsync(nint videoWindowHandle)
    {
        _videoWindowHandle = videoWindowHandle;
        try
        {
            try
            {
                await Library.InitializeAsync().ConfigureAwait(true);
                if (Account.IsSignedIn)
                {
                    _ = Library.RefreshCloudListsAsync();
                    _ = Addons.RefreshCloudCollectionsAsync();
                    await InitializeCloudPlaylistSessionAsync().ConfigureAwait(true);
                    _ = InitializeCloudPreferencesAsync();
                    _ = InitializeCloudSyncAsync();
                }
                StatusMessage = "Bir bulut hesabı, paylaşım bağlantısı, klasör veya torrent seçin.";
            }
            catch (Exception libraryException)
            {
                _logger.Error("Video kütüphanesi başlatılamadı.", libraryException);
                StatusMessage = "Kaynak merkezi hazır · Video kütüphanesi kullanılamıyor.";
            }

            await Updates.CheckOnStartupAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Kaynak merkezi hazırlanamadı.", exception);
            StatusMessage = "Kaynak merkezi hazırlanamadı.";
        }
    }

    private async Task<bool> EnsurePlaybackReadyAsync()
    {
        if (IsReady)
        {
            return true;
        }

        await _playbackInitGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (IsReady)
            {
                return true;
            }

            if (_videoWindowHandle == 0)
            {
                StatusMessage = "Oynatıcı penceresi henüz hazır değil.";
                return false;
            }

            StatusMessage = "Oynatıcı hazırlanıyor…";
            await _playback.InitializeAsync(_videoWindowHandle).ConfigureAwait(true);
            IsReady = true;
            _playback.SetVolume(Math.Min(_settings.Volume, VolumeBoostLimit));
            _playback.SetSpeed(_settings.PlaybackSpeed);
            ApplySubtitlePresentationSettings();
            RefreshAudioDevices();
            ApplyVideoProfileForCurrentSource();
            ApplyVideoEqualizer();
            return true;
        }
        catch (Exception exception)
        {
            IsReady = false;
            _logger.Error("Oynatma motoru başlatılamadı.", exception);
            StatusMessage = "Video oynatıcı başlatılamadı.";
            _dialogs.ShowError("Video oynatıcı başlatılamadı. Uygulamayı güncelleyip yeniden deneyin.");
            return false;
        }
        finally
        {
            _playbackInitGate.Release();
        }
    }

    public Task OpenPathAsync(string path)
    {
        if (AutoAddFolderVideos && File.Exists(path) && VideoExtensions.Contains(Path.GetExtension(path)))
        {
            return OpenFileWithFolderPlaylistAsync(path, replacePlaylist: true);
        }
        return OpenFilesAsync(new[] { path }, replacePlaylist: false);
    }

    public async Task OpenFilesAsync(IEnumerable<string> paths, bool replacePlaylist)
    {
        var sources = ExpandVideoSources(paths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0)
        {
            StatusMessage = "Desteklenen video dosyası bulunamadı.";
            return;
        }

        if (replacePlaylist)
        {
            Playlist.Clear();
        }

        PlaylistItem? firstAdded = null;
        foreach (var source in sources)
        {
            var existing = Playlist.FirstOrDefault(item =>
                string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                firstAdded ??= existing;
                continue;
            }

            var item = CreatePlaylistItem(source);
            Playlist.Add(item);
            firstAdded ??= item;
        }

        OnPropertyChanged(nameof(PlaylistSummary));
        QueueCloudPlaylistSync();
        SelectedPlaylistItem = firstAdded;
        if (firstAdded is not null)
        {
            await OpenPlaylistItemAsync(firstAdded).ConfigureAwait(true);
        }
    }

    public async Task OpenSourceArgumentAsync(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var item = Playlist.FirstOrDefault(existing =>
                       string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase))
                   ?? CreatePlaylistItem(source);

        if (!Playlist.Contains(item))
        {
            Playlist.Add(item);
            OnPropertyChanged(nameof(PlaylistSummary));
            QueueCloudPlaylistSync();
        }

        SelectedPlaylistItem = item;
        await OpenPlaylistItemAsync(item).ConfigureAwait(true);
    }

    private async Task OpenTorrentPlaybackSourceAsync(string source, string? displayTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var title = string.IsNullOrWhiteSpace(displayTitle)
            ? CreateDisplayTitle(source)
            : displayTitle.Trim();

        var item = Playlist.FirstOrDefault(existing =>
                       string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase))
                   ?? new PlaylistItem(source, title, false);

        if (!Playlist.Contains(item))
        {
            Playlist.Add(item);
            OnPropertyChanged(nameof(PlaylistSummary));
            QueueCloudPlaylistSync();
        }

        SelectedPlaylistItem = item;

        // The actual playback source is MonoTorrent's loopback HTTP stream
        // (127.0.0.1). Keep that transport URL for playback/resume identity, but
        // persist the human-readable release/video title for Recent/Continue Watching.
        await OpenSourceAsync(
                source,
                addRecent: true,
                recentTitle: title)
            .ConfigureAwait(true);
    }

    public Task PlayRecentAsync(RecentSourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return OpenPersistentSourceAsync(entry.Source);
    }

    public Task PlayHistoryAsync(HistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return OpenPersistentSourceAsync(item.Source);
    }

    public Task PlayContinueWatchingAsync(ContinueWatchingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return OpenPersistentSourceAsync(item.Source);
    }

    private Task OpenPersistentSourceAsync(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return Sources.OpenUrlAsync(source);
        }

        return OpenSourceArgumentAsync(source);
    }

    public void AttachSubtitlePath(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        CancelSubtitleSync();
        _subtitleSyncResult = null;
        _primarySubtitleOriginalPath = path;
        _subtitleSyncFinalized = false;
        _hasSubtitleSyncSession = !string.IsNullOrWhiteSpace(_currentPlaybackSource);
        OnPropertyChanged(nameof(IsSubtitleSyncVisible));
        OnPropertyChanged(nameof(SubtitleSyncProgressSummaryText));
        OnPropertyChanged(nameof(SubtitleSyncGlyphText));
        OnPropertyChanged(nameof(SubtitleSyncActionText));
        OnPropertyChanged(nameof(IsSubtitleSyncActionComplete));
        OnPropertyChanged(nameof(IsSubtitleSyncActionRetry));

        if (!_hasSubtitleSyncSession)
        {
            SafePlaybackAction(() => _playback.AddSubtitle(path));
            _subtitleSyncWorkingPath = path;
            _subtitleSyncFinalized = true;
            StatusMessage = $"Altyazı eklendi: {Path.GetFileName(path)}";
            PersistCurrentMediaAttachmentState();
            return;
        }

        var syncDirectory = Path.Combine(
            _paths.SubtitleDirectory,
            "sync-cache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(syncDirectory);
        var extension = Path.GetExtension(path);
        var safeName = MakeSafeFileName(Path.GetFileNameWithoutExtension(path));
        _subtitleSyncWorkingPath = Path.Combine(syncDirectory, $"{safeName}.autosync{extension}");

        try
        {
            File.Copy(path, _subtitleSyncWorkingPath, overwrite: true);
            SafePlaybackAction(() => _playback.AddSubtitle(_subtitleSyncWorkingPath));
        }
        catch (Exception exception)
        {
            _logger.Error("Altyazı senkron çalışma kopyası hazırlanamadı.", exception);
            _subtitleSyncWorkingPath = null;
            _hasSubtitleSyncSession = false;
            OnPropertyChanged(nameof(IsSubtitleSyncVisible));
            SafePlaybackAction(() => _playback.AddSubtitle(path));
            _subtitleSyncWorkingPath = path;
            _subtitleSyncFinalized = true;
            StatusMessage = $"Altyazı eklendi: {Path.GetFileName(path)}";
            PersistCurrentMediaAttachmentState();
            return;
        }

        SubtitleSyncProgress = 0;
        SubtitleSyncStageText = LocalizationManager.Get("Player.SubtitleSync.State.Ready");
        SubtitleSyncStatus = _subtitleSyncService.IsAvailable
            ? LocalizationManager.Get("Player.SubtitleSync.ManualReady")
            : _subtitleSyncService.AvailabilityMessage;
        StatusMessage = $"Altyazı eklendi: {Path.GetFileName(path)}";
        _subtitleSyncFinalized = false;
        PersistCurrentMediaAttachmentState();
    }

    public void AttachAudioPath(string path)
    {
        if (!File.Exists(path) &&
            (!Uri.TryCreate(path, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            return;
        }

        SafePlaybackAction(() => _playback.AddAudio(path));
        SetExternalAudioForSync(path, null);
        StatusMessage = $"Harici ses eklendi: {CreateDisplayTitle(path)}";
    }

    private Task AttachRemoteAudioAsync(RemoteOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SafePlaybackAction(() => _playback.AddAudio(request.Source, request.HttpHeaders));
        SetExternalAudioForSync(request.Source, request.HttpHeaders);
        StatusMessage = $"Harici ses akışı açıldı; veri arka planda önbelleğe alınıyor: {request.DisplayName}";
        Sources.EndAudioSelection();
        Telegram.EndAudioSelection();
        if (HasMedia)
        {
            SelectedSidebarIndex = 3;
            IsMediaCenterVisible = false;
            if (_resumeAfterExternalAudioSelection && Snapshot.IsPaused)
                SafePlaybackAction(_playback.TogglePause);
        }
        _resumeAfterExternalAudioSelection = false;
        return Task.CompletedTask;
    }

    private void SetExternalAudioForSync(
        string source,
        IReadOnlyDictionary<string, string>? httpHeaders)
    {
        CancelAudioSyncAnalysis();
        CancelLiveProgressiveAudioAnalysis();
        _externalAudioSource = source;
        _externalAudioHeaders = httpHeaders is null
            ? null
            : httpHeaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _audioSyncResult = null;
        _audioSyncSafety = null;
        _audioSyncFinalized = false;
        _manualAudioDelaySeconds = 0;
        _preparedAudio = null;
        _preparedAudioPath = null;
        _progressiveAudioRegions = Array.Empty<AudioSyncProgressiveRegion>();
        _progressiveAudioRegionIndex = -1;
        _progressiveLastPositionSeconds = double.NaN;
        _liveProgressiveAttempts.Clear();
        _liveProgressiveLastAttemptTarget.Clear();
        _liveProgressiveRetryAfter.Clear();
        ClearAudioSyncCorrection();
        NotifyAudioSyncDiagnosticsChanged();
        ApplyAudioDelay(0, force: true);

        AudioSyncStatus = _audioSyncService.IsAvailable
            ? LocalizationManager.Get("Player.AudioSync.ManualReady")
            : _audioSyncService.AvailabilityMessage;
        _audioSyncFinalized = false;
        PersistCurrentMediaAttachmentState();
    }

    private Task ResyncExternalAudioAsync()
    {
        if (string.IsNullOrWhiteSpace(_externalAudioSource))
        {
            AudioSyncStatus = LocalizationManager.Get("Player.AudioSync.ManualMissing");
            return Task.CompletedTask;
        }

        return StartAudioSyncAsync();
    }

    private Task ResyncExternalSubtitleAsync()
    {
        if (string.IsNullOrWhiteSpace(_primarySubtitleOriginalPath) ||
            string.IsNullOrWhiteSpace(_subtitleSyncWorkingPath) ||
            string.IsNullOrWhiteSpace(_currentPlaybackSource))
        {
            SubtitleSyncStatus = LocalizationManager.Get("Player.SubtitleSync.ManualMissing");
            return Task.CompletedTask;
        }

        if (!_subtitleSyncService.IsAvailable)
        {
            SubtitleSyncStatus = _subtitleSyncService.AvailabilityMessage;
            return Task.CompletedTask;
        }

        var syncDirectory = Path.GetDirectoryName(_subtitleSyncWorkingPath);
        if (string.IsNullOrWhiteSpace(syncDirectory))
        {
            SubtitleSyncStatus = LocalizationManager.Get("Player.SubtitleSync.ManualMissing");
            return Task.CompletedTask;
        }

        _subtitleSyncFinalized = false;
        return StartSubtitleSyncAsync(
            _primarySubtitleOriginalPath,
            _subtitleSyncWorkingPath,
            syncDirectory);
    }

    private async Task StartAudioSyncAsync()
    {
        if (string.IsNullOrWhiteSpace(_externalAudioSource) ||
            string.IsNullOrWhiteSpace(_currentPlaybackSource) ||
            Snapshot.DurationSeconds < 45)
        {
            return;
        }

        CancelAudioSyncAnalysis();
        ClearAudioSyncCorrection();
        var cts = new CancellationTokenSource();
        _audioSyncCts = cts;
        IsAudioSyncAnalyzing = true;
        _audioSyncProgressStartedAt = DateTimeOffset.UtcNow;
        AudioSyncProgress = 0;
        AudioSyncStatus = $"{_audioSyncService.EngineName} ile ses senkronu analiz ediliyor…";

        var request = new AudioSyncRequest(
            new AudioSyncMediaSource(_currentPlaybackSource, _currentPlaybackHeaders),
            new AudioSyncMediaSource(_externalAudioSource, _externalAudioHeaders),
            Snapshot.DurationSeconds,
            _settings.AudioSyncMaxSearchSeconds);

        try
        {
            var progress = new Progress<int>(value =>
            {
                AudioSyncProgress = value;
                AudioSyncStatus = $"{_audioSyncService.EngineName} · {GetAudioSyncPhaseText()}";
            });
            var result = await _audioSyncService.AnalyzeAsync(
                request,
                progress,
                cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested || !ReferenceEquals(_audioSyncCts, cts)) return;
            _audioSyncResult = result;
            IsAudioSyncAnalyzing = false;
            AudioSyncProgress = 0;
            NotifyAudioSyncDiagnosticsChanged();
            AudioSyncStatus = result.Message;

            var hybridRecoveryCandidate = HasStrongHybridRecoveryEvidence(result);
            _audioSyncSafety = AudioSyncSafetyPolicy.AssessAnalysis(result, hybridRecoveryCandidate);
            ApplyAnalyzedAudioSync(result);

            var shouldPrepare = _audioSyncSafety.AllowPreparation &&
                                _audioSyncService.SupportsFullSync &&
                                File.Exists(request.Reference.Source) &&
                                File.Exists(request.ExternalAudio.Source);

            if (!shouldPrepare)
            {
                _audioSyncFinalized = true;
                if (_audioSyncSafety.Classification is AudioSyncSafetyClass.DifferentCut or AudioSyncSafetyClass.Rejected)
                    AudioSyncStatus = _audioSyncSafety.UserMessage;
                PersistCurrentMediaAttachmentState();
                NotifyAudioSyncDiagnosticsChanged();
            }

            if (shouldPrepare)
            {
                if (!_audioSyncSafety.AllowDirectCorrection)
                    TrySelectEmbeddedAudioWhileRecovering();

                AudioSyncStatus = hybridRecoveryCandidate
                    ? "Farklı kurgu adayı güvenli tam senkron için doğrulanıyor… Sonuç beklenirken orijinal ses kullanılıyor."
                    : "Ses kaynağı güvenli tam senkron için doğrulanıyor… Sonuç beklenirken orijinal ses kullanılıyor.";

                await PrepareFullAudioSyncAsync(request, result, cts).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // A new media or external audio selection superseded this analysis.
        }
        catch (Exception exception)
        {
            _logger.Error("Harici ses senkronu uygulanamadı.", exception);
            if (_audioSyncResult is null)
            {
                NotifyAudioSyncDiagnosticsChanged();
                AudioSyncStatus = $"{LocalizationManager.Get("Player.AudioSync.State.Failed")}: {CreateAudioSyncErrorSummary(exception)}";
                ClearAudioSyncCorrection();
                ApplyAudioDelay(_manualAudioDelaySeconds, force: true);
            }
            else
            {
                var directCorrectionIsSafe =
                    _audioSyncSafety?.AllowDirectCorrection == true &&
                    _audioSyncApplicationKind is AudioSyncApplicationKind.FixedDelay or AudioSyncApplicationKind.LiveTempo;

                if (!directCorrectionIsSafe)
                {
                    TrySelectEmbeddedAudioWhileRecovering();
                    ClearAudioSyncCorrection();
                    ApplyAudioDelay(_manualAudioDelaySeconds, force: true);

                    _audioSyncSafety = new AudioSyncSafetyAssessment(
                        HasStrongHybridRecoveryEvidence(_audioSyncResult)
                            ? AudioSyncSafetyClass.DifferentCut
                            : AudioSyncSafetyClass.Rejected,
                        false, false, false,
                        "Bu ses kaynağı güvenli otomatik senkron eşiğini geçemedi. Harici ses otomatik uygulanmadı.",
                        "full_sync_prepare_failed");
                    AudioSyncStatus = $"{_audioSyncSafety.UserMessage} {CreateAudioSyncErrorSummary(exception)}";
                }
                else
                {
                    AudioSyncStatus = $"AST tam düzeltme hazırlanamadı; mevcut güvenli sabit/drift düzeltmesi korunuyor: {CreateAudioSyncErrorSummary(exception)}";
                }

                _audioSyncFinalized = true;
                PersistCurrentMediaAttachmentState();
                NotifyAudioSyncDiagnosticsChanged();
            }
        }
        finally
        {
            if (ReferenceEquals(_audioSyncCts, cts))
            {
                _audioSyncCts = null;
                IsAudioSyncAnalyzing = false;
                IsAudioSyncPreparing = false;
                AudioSyncProgress = 0;
                _audioSyncProgressStartedAt = DateTimeOffset.MinValue;
            }
            cts.Dispose();
        }
    }

    private void ApplyAnalyzedAudioSync(AudioSyncResult result)
    {
        if (_audioSyncSafety?.AllowDirectCorrection != true)
        {
            ClearAudioSyncCorrection();
            ApplyAudioDelay(_manualAudioDelaySeconds, force: true);
            return;
        }

        if (result.CanApplyFixedDelay)
        {
            SetAudioTempoCorrection(1.0);
            _audioSyncApplicationKind = AudioSyncApplicationKind.FixedDelay;
            ApplyAudioDelay(result.BaseDelaySeconds + _manualAudioDelaySeconds, force: true);
            AudioSyncStatus = $"{result.Message} · Sabit gecikme uygulandı.";
            NotifyAudioSyncDiagnosticsChanged();
            return;
        }

        if (result.CanApplyLiveTempo)
        {
            // Rev9.9 uses AudioSyncTool's own drift formula instead of changing
            // audio-delay as the seek position moves. This keeps one constant
            // t=0 offset and corrects clock drift with FFmpeg atempo in libmpv.
            var factor = result.TempoFactor;
            SetAudioTempoCorrection(factor);
            _audioSyncApplicationKind = AudioSyncApplicationKind.LiveTempo;
            ApplyAudioDelay(result.EffectiveBaseDelaySeconds + _manualAudioDelaySeconds, force: true);
            AudioSyncStatus = $"{result.Message} · AST tempo {factor:0.#########}x aktif.";
            NotifyAudioSyncDiagnosticsChanged();
            return;
        }

        ClearAudioSyncCorrection();
        ApplyAudioDelay(_manualAudioDelaySeconds, force: true);
    }

    private static bool HasStrongHybridRecoveryEvidence(AudioSyncResult result)
    {
        if (result.Verdict == AudioSyncVerdict.Unavailable) return false;

        // A plain NoMatch must remain a hard safety stop. Hybrid is permitted only
        // when the first AST pass already found substantial, monotonic structure
        // but fell below its global confidence threshold. This prevents unrelated
        // audio from being forced onto a video.
        var segmentRatio = result.TotalSegments > 0
            ? result.UsedSegments / (double)result.TotalSegments
            : 0.0;
        var confidenceFloor = result.VerdictReasons?.Any(reason =>
            reason.Contains("confidence_floor", StringComparison.OrdinalIgnoreCase)) == true;
        var structuralEvidence = result.Model == AudioSyncModelKind.Piecewise &&
                                 result.Regions.Count >= 2 &&
                                 result.UsedSegments >= 6 &&
                                 segmentRatio >= 0.45 &&
                                 (result.PhatProbes >= 2 || result.RawConfidence >= 1.25);

        if (result.Verdict == AudioSyncVerdict.NoMatch)
            return structuralEvidence && (confidenceFloor || result.RawConfidence >= 1.75);

        return result.Model == AudioSyncModelKind.Piecewise ||
               result.Verdict == AudioSyncVerdict.Uncertain;
    }

    private void TrySelectEmbeddedAudioWhileRecovering()
    {
        var embedded = AudioTracks.FirstOrDefault(track => !track.IsExternal);
        if (embedded is null) return;
        SafePlaybackAction(() => _playback.SelectAudioTrack(embedded.Id));
    }

    private async Task PrepareFullAudioSyncAsync(AudioSyncRequest request, AudioSyncResult analysisHint, CancellationTokenSource cts)
    {
        if (!_audioSyncService.SupportsFullSync || cts.IsCancellationRequested) return;

        IsAudioSyncPreparing = true;
        _audioSyncProgressStartedAt = DateTimeOffset.UtcNow;
        AudioSyncProgress = 0;
        AudioSyncStatus = "AudioSyncTool tam senkron pipeline hazırlanıyor…";
        var progress = new Progress<int>(value =>
        {
            AudioSyncProgress = value;
            AudioSyncStatus = value is >= 42 and < 96
                ? $"Multi-Modal Timeline Sync · {value}%"
                : $"AudioSyncTool tam düzeltme hazırlanıyor · {value}%";
        });

        var outputDirectory = Path.Combine(_paths.RemoteCacheDirectory, "audio-sync");
        var prepared = await _audioSyncService.PrepareCorrectedAudioAsync(
            request,
            outputDirectory,
            analysisHint,
            progress,
            cts.Token).ConfigureAwait(true);

        if (prepared is null || cts.IsCancellationRequested || !ReferenceEquals(_audioSyncCts, cts)) return;

        // Rev10.11 Safe Sync Gate:
        // - Normal prepared tracks keep the strict 80 ms verification gate.
        // - Hybrid/different-cut material must also have no missing source regions,
        //   high coverage/confidence and uniformly trusted mapping.
        // - Progressive language switching is intentionally suspended in production.
        var preparedSafety = AudioSyncSafetyPolicy.AssessPrepared(prepared);
        _audioSyncSafety = preparedSafety;

        if (!preparedSafety.AllowPreparedTrack)
        {
            var safeDirectCorrectionAlreadyActive =
                _audioSyncApplicationKind is AudioSyncApplicationKind.FixedDelay or AudioSyncApplicationKind.LiveTempo;

            TryDeletePreparedAudio(prepared.Path);

            if (!safeDirectCorrectionAlreadyActive)
            {
                ClearAudioSyncCorrection();
                ApplyAudioDelay(_manualAudioDelaySeconds, force: true);
                TrySelectEmbeddedAudioWhileRecovering();
            }

            AudioSyncStatus = safeDirectCorrectionAlreadyActive
                ? $"{preparedSafety.UserMessage} Mevcut güvenli sabit/drift düzeltmesi korunuyor."
                : preparedSafety.UserMessage;
            _audioSyncFinalized = true;
            PersistCurrentMediaAttachmentState();
            NotifyAudioSyncDiagnosticsChanged();
            return;
        }

        _audioSyncSafety = preparedSafety;
        _preparedAudio = prepared;
        _preparedAudioPath = prepared.Path;
        SetAudioTempoCorrection(1.0);
        _audioSyncApplicationKind = AudioSyncApplicationKind.PreparedTrack;
        ApplyAudioDelay(_manualAudioDelaySeconds, force: true);
        SafePlaybackAction(() => _playback.AddAudio(prepared.Path));
        if (prepared.HybridRecovery && _audioSyncResult is not null)
        {
            _audioSyncResult = _audioSyncResult with
            {
                Verdict = AudioSyncVerdict.Reliable,
                Message = prepared.Message,
            };
        }
        AudioSyncStatus = prepared.Message + " · Doğrulanmış ses oynatılıyor.";
        _audioSyncFinalized = true;
        PersistCurrentMediaAttachmentState();
        NotifyAudioSyncDiagnosticsChanged();
    }

    private bool TryActivateProgressiveAudioSync(AudioSyncPreparedAudio prepared, double wholeTrackResidualSeconds)
    {
        var regions = prepared.ProgressiveRegions ?? Array.Empty<AudioSyncProgressiveRegion>();
        var trusted = regions.Count(IsProgressiveRegionTrusted);
        var matched = regions.Count(region => !region.MissingSource);
        var canLiveRecover = _audioSyncService.SupportsLiveProgressiveSync &&
                             !string.IsNullOrWhiteSpace(_currentPlaybackSource) &&
                             !string.IsNullOrWhiteSpace(_externalAudioSource) &&
                             File.Exists(_currentPlaybackSource) &&
                             File.Exists(_externalAudioSource);
        if (regions.Count == 0 || matched == 0 || (trusted == 0 && !canLiveRecover))
        {
            return false;
        }

        // Critical compatibility rule: this mode is entered only after the normal
        // hard-cut/full-track correction failed its 180 ms safety gate. Reliable
        // Fixed/Drift/PreparedTrack paths never pass through here.
        _preparedAudio = prepared;
        _preparedAudioPath = null;
        _progressiveAudioRegions = regions;
        _progressiveAudioRegionIndex = -1;
        _progressiveLastPositionSeconds = double.NaN;
        _liveProgressiveAttempts.Clear();
        _liveProgressiveLastAttemptTarget.Clear();
        _liveProgressiveRetryAfter.Clear();
        _audioSyncApplicationKind = AudioSyncApplicationKind.Progressive;
        _audioSyncFinalized = true;

        AudioSyncStatus = trusted > 0
            ? $"Zorlu kurgu: bölgesel senkron aktif · {trusted}/{matched} Türkçe bölge doğrulandı · " +
              $"tüm-film kalan farkı {wholeTrackResidualSeconds * 1000:0} ms olduğu için sorunlu sahnelerde orijinal ses kullanılacak."
            : $"Zorlu kurgu: Live Progressive Sync aktif · {matched} aday Türkçe bölge bulundu · " +
              "izlediğiniz bölüm ve birkaç dakika ilerisi arka planda doğrulanacak.";

        UpdateProgressiveAudioSync(Snapshot.PositionSeconds, force: true, userSeek: false);
        ScheduleLiveProgressiveAudioSync(Snapshot.PositionSeconds, userSeek: true);
        PersistCurrentMediaAttachmentState();
        NotifyAudioSyncDiagnosticsChanged();
        return true;
    }

    private static bool IsProgressiveRegionTrusted(AudioSyncProgressiveRegion region)
    {
        if (region.MissingSource || !region.Trusted) return false;
        if (region.Rate is < 0.75 or > 1.25) return false;

        if (region.VerifiedResidualSeconds is double verified)
            return region.VerifiedProbes > 0 && Math.Abs(verified) <= 0.180;

        return region.PlanResidualSeconds is double planned &&
               Math.Abs(planned) <= 0.120 &&
               region.Confidence >= 0.35;
    }

    private void UpdateProgressiveAudioSync(double positionSeconds, bool force, bool userSeek)
    {
        if (_audioSyncApplicationKind != AudioSyncApplicationKind.Progressive ||
            _progressiveAudioRegions.Count == 0)
        {
            return;
        }

        var position = Math.Max(0, positionSeconds);
        var index = -1;
        for (var i = 0; i < _progressiveAudioRegions.Count; i++)
        {
            var region = _progressiveAudioRegions[i];
            if (position >= region.TargetStartSeconds - 0.05 &&
                position < region.TargetEndSeconds + 0.05)
            {
                index = i;
                break;
            }
        }

        if (!force && index == _progressiveAudioRegionIndex) return;
        _progressiveAudioRegionIndex = index;

        if (index < 0)
        {
            SelectEmbeddedAudioForProgressive();
            if (userSeek)
                AudioSyncStatus = "Bu bölüm için güvenilir Türkçe senkron bölgesi bulunamadı; orijinal ses kullanılıyor.";
            return;
        }

        var current = _progressiveAudioRegions[index];
        if (!IsProgressiveRegionTrusted(current))
        {
            SelectEmbeddedAudioForProgressive();
            AudioSyncStatus = current.MissingSource
                ? "Zorlu kurgu: Türkçe kaynakta bulunmayan/kırpılmış sahne · orijinal ses kullanılıyor."
                : _audioSyncService.SupportsLiveProgressiveSync
                    ? "Bu bölüm için Türkçe ses senkronu ayarlanıyor… Şimdilik orijinal ses kullanılıyor."
                    : "Bu bölüm için Türkçe ses senkronu güvenli eşikte değil; orijinal ses kullanılıyor.";
            return;
        }

        var externalTrack = AudioTracks.FirstOrDefault(track => track.IsExternal);
        if (externalTrack is null)
        {
            SelectEmbeddedAudioForProgressive();
            AudioSyncStatus = "Türkçe harici ses bölgesi hazırlanıyor…";
            return;
        }

        var rate = Math.Clamp(current.Rate, 0.75, 1.25);
        var tempo = Math.Clamp(1.0 / rate, 0.5, 2.0);
        // Timeline relation is target = rate * source + intercept. With mpv
        // atempo=1/rate, the same intercept is the required constant audio-delay
        // for this edit region. We only change it at edit boundaries, never every
        // playback tick (the old moving-delay drift bug remains forbidden).
        var intercept = current.TargetStartSeconds - rate * current.SourceStartSeconds;

        SafePlaybackAction(() => _playback.SelectAudioTrack(externalTrack.Id));
        SetAudioTempoCorrection(tempo);
        ApplyAudioDelay(intercept + _manualAudioDelaySeconds, force: true);

        var residualText = current.VerifiedResidualSeconds is double residual
            ? $" · bölgesel fark {Math.Abs(residual) * 1000:0} ms"
            : string.Empty;
        AudioSyncStatus = userSeek
            ? $"Bu bölüm için Türkçe ses senkronu hazır{residualText}."
            : $"Zorlu kurgu: doğrulanmış Türkçe bölge oynatılıyor{residualText}.";
        NotifyAudioSyncDiagnosticsChanged();
    }

    private void ScheduleLiveProgressiveAudioSync(double positionSeconds, bool userSeek)
    {
        if (_audioSyncApplicationKind != AudioSyncApplicationKind.Progressive ||
            !_audioSyncService.SupportsLiveProgressiveSync ||
            _progressiveAudioRegions.Count == 0 ||
            string.IsNullOrWhiteSpace(_currentPlaybackSource) ||
            string.IsNullOrWhiteSpace(_externalAudioSource) ||
            !File.Exists(_currentPlaybackSource) ||
            !File.Exists(_externalAudioSource))
        {
            return;
        }

        var position = Math.Max(0, positionSeconds);
        var candidateIndex = FindProgressiveRegionIndex(position);
        if (candidateIndex >= 0)
        {
            var current = _progressiveAudioRegions[candidateIndex];
            if (current.MissingSource || IsProgressiveRegionTrusted(current))
                candidateIndex = -1;
        }

        // If the current region is already safe, preflight the first unresolved
        // region within roughly five minutes. This keeps analysis ahead of playback.
        if (candidateIndex < 0)
        {
            var horizon = position + 300.0;
            for (var i = 0; i < _progressiveAudioRegions.Count; i++)
            {
                var region = _progressiveAudioRegions[i];
                if (region.TargetEndSeconds < position + 8.0 || region.TargetStartSeconds > horizon)
                    continue;
                if (region.MissingSource || IsProgressiveRegionTrusted(region))
                    continue;
                candidateIndex = i;
                break;
            }
        }

        if (candidateIndex < 0) return;
        var candidate = _progressiveAudioRegions[candidateIndex];
        var target = Math.Clamp(
            userSeek || position >= candidate.TargetStartSeconds
                ? position
                : Math.Max(candidate.TargetStartSeconds + 12.0, candidate.TargetStartSeconds),
            candidate.TargetStartSeconds,
            Math.Max(candidate.TargetStartSeconds, candidate.TargetEndSeconds - 0.1));

        if (_isLiveProgressiveAnalyzing)
        {
            var sameRegion = _liveProgressiveAnalyzingRegionIndex == candidateIndex;
            var closeTarget = !double.IsNaN(_liveProgressiveAnalysisTargetSeconds) &&
                              Math.Abs(_liveProgressiveAnalysisTargetSeconds - target) < 35.0;
            if (sameRegion && closeTarget) return;
            if (!userSeek) return;
            CancelLiveProgressiveAudioAnalysis();
        }

        // A long coarse edit region may contain speech-heavy stretches where no
        // useful cross-language waveform event exists. Do not blacklist that entire
        // region after two misses: once playback has moved roughly one local window,
        // allow a fresh pair of attempts at the new position.
        if (_liveProgressiveLastAttemptTarget.TryGetValue(candidateIndex, out var lastAttemptTarget) &&
            Math.Abs(lastAttemptTarget - target) >= 60.0)
        {
            _liveProgressiveAttempts[candidateIndex] = 0;
            _liveProgressiveRetryAfter.Remove(candidateIndex);
        }

        var attempts = _liveProgressiveAttempts.TryGetValue(candidateIndex, out var attemptCount)
            ? attemptCount
            : 0;
        var maximumAttempts = userSeek ? 4 : 2;
        if (attempts >= maximumAttempts) return;

        if (!userSeek &&
            _liveProgressiveRetryAfter.TryGetValue(candidateIndex, out var retryAfter) &&
            DateTimeOffset.UtcNow < retryAfter)
        {
            return;
        }

        _ = AnalyzeLiveProgressiveAudioRegionAsync(candidateIndex, target, userSeek);
    }

    private int FindProgressiveRegionIndex(double positionSeconds)
    {
        for (var i = 0; i < _progressiveAudioRegions.Count; i++)
        {
            var region = _progressiveAudioRegions[i];
            if (positionSeconds >= region.TargetStartSeconds - 0.05 &&
                positionSeconds < region.TargetEndSeconds + 0.05)
            {
                return i;
            }
        }
        return -1;
    }

    private async Task AnalyzeLiveProgressiveAudioRegionAsync(int regionIndex, double targetPositionSeconds, bool userSeek)
    {
        if (regionIndex < 0 || regionIndex >= _progressiveAudioRegions.Count ||
            string.IsNullOrWhiteSpace(_currentPlaybackSource) ||
            string.IsNullOrWhiteSpace(_externalAudioSource))
        {
            return;
        }

        var sourceAtStart = _currentPlaybackSource;
        var externalAtStart = _externalAudioSource;
        var seed = _progressiveAudioRegions[regionIndex];
        if (seed.MissingSource || IsProgressiveRegionTrusted(seed)) return;

        var cts = new CancellationTokenSource();
        _liveProgressiveAudioCts = cts;
        _liveProgressiveAnalyzingRegionIndex = regionIndex;
        _liveProgressiveAnalysisTargetSeconds = targetPositionSeconds;
        _isLiveProgressiveAnalyzing = true;
        var generation = ++_liveProgressiveGeneration;
        _liveProgressiveAttempts[regionIndex] = _liveProgressiveAttempts.TryGetValue(regionIndex, out var attempts)
            ? attempts + 1
            : 1;
        _liveProgressiveLastAttemptTarget[regionIndex] = targetPositionSeconds;

        if (FindProgressiveRegionIndex(Snapshot.PositionSeconds) == regionIndex && !IsProgressiveRegionTrusted(seed))
        {
            AudioSyncStatus = userSeek
                ? "Atladığınız bölüm için Türkçe ses senkronu ayarlanıyor… Oynatma orijinal sesle devam ediyor."
                : "Bu bölüm için Türkçe ses senkronu arka planda ayarlanıyor… Oynatma orijinal sesle devam ediyor.";
        }
        NotifyAudioSyncDiagnosticsChanged();

        try
        {
            var request = new AudioSyncRequest(
                new AudioSyncMediaSource(sourceAtStart, _currentPlaybackHeaders),
                new AudioSyncMediaSource(externalAtStart, _externalAudioHeaders),
                Snapshot.DurationSeconds,
                MaxSearchSeconds: 45);
            var progress = new Progress<int>(_ => { });
            var updated = await _audioSyncService.AnalyzeLiveProgressiveRegionAsync(
                request,
                seed,
                targetPositionSeconds,
                progress,
                cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested || generation != _liveProgressiveGeneration ||
                !string.Equals(sourceAtStart, _currentPlaybackSource, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(externalAtStart, _externalAudioSource, StringComparison.OrdinalIgnoreCase) ||
                updated is null || regionIndex >= _progressiveAudioRegions.Count)
            {
                return;
            }

            var list = _progressiveAudioRegions.ToArray();
            list[regionIndex] = updated;
            _progressiveAudioRegions = list;

            if (IsProgressiveRegionTrusted(updated))
            {
                _liveProgressiveAttempts.Remove(regionIndex);
                _liveProgressiveLastAttemptTarget.Remove(regionIndex);
                _liveProgressiveRetryAfter.Remove(regionIndex);
                AudioSyncStatus =
                    $"Live Progressive: Türkçe ses bölgesi doğrulandı · " +
                    $"{Math.Abs(updated.VerifiedResidualSeconds ?? 0) * 1000:0} ms / {updated.VerifiedProbes} probe.";
            }
            else
            {
                _liveProgressiveRetryAfter[regionIndex] = DateTimeOffset.UtcNow.AddSeconds(userSeek ? 15 : 35);
                if (FindProgressiveRegionIndex(Snapshot.PositionSeconds) == regionIndex)
                {
                    AudioSyncStatus = updated.VerifiedResidualSeconds is double residual
                        ? $"Bu bölüm henüz güvenli senkron eşiğinde değil ({Math.Abs(residual) * 1000:0} ms); orijinal ses kullanılıyor."
                        : "Bu bölümde yeterli ortak ses olayı bulunamadı; orijinal ses kullanılıyor.";
                }
            }

            PersistCurrentMediaAttachmentState();
            UpdateProgressiveAudioSync(Snapshot.PositionSeconds, force: true, userSeek: userSeek);
            NotifyAudioSyncDiagnosticsChanged();
        }
        catch (OperationCanceledException)
        {
            // Seek/new media cancels obsolete local analysis by design.
        }
        catch (Exception exception)
        {
            _liveProgressiveRetryAfter[regionIndex] = DateTimeOffset.UtcNow.AddSeconds(40);
            _logger.Info($"Live Progressive bölge analizi tamamlanamadı: {CreateAudioSyncErrorSummary(exception)}");
            if (FindProgressiveRegionIndex(Snapshot.PositionSeconds) == regionIndex)
                AudioSyncStatus = "Bu bölüm için canlı senkron henüz hazırlanamadı; orijinal sesle devam ediliyor.";
        }
        finally
        {
            if (ReferenceEquals(_liveProgressiveAudioCts, cts))
            {
                _liveProgressiveAudioCts = null;
                _liveProgressiveAnalyzingRegionIndex = -1;
                _liveProgressiveAnalysisTargetSeconds = double.NaN;
                _isLiveProgressiveAnalyzing = false;
                NotifyAudioSyncDiagnosticsChanged();

                if (_audioSyncApplicationKind == AudioSyncApplicationKind.Progressive &&
                    !Snapshot.IsPaused && !cts.IsCancellationRequested)
                {
                    ScheduleLiveProgressiveAudioSync(Snapshot.PositionSeconds, userSeek: false);
                }
            }
            cts.Dispose();
        }
    }

    private void CancelLiveProgressiveAudioAnalysis()
    {
        var cts = _liveProgressiveAudioCts;
        _liveProgressiveAudioCts = null;
        _liveProgressiveAnalyzingRegionIndex = -1;
        _liveProgressiveAnalysisTargetSeconds = double.NaN;
        _isLiveProgressiveAnalyzing = false;
        _liveProgressiveGeneration++;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }
        NotifyAudioSyncDiagnosticsChanged();
    }

    private void SelectEmbeddedAudioForProgressive()
    {
        var embedded = AudioTracks.FirstOrDefault(track => !track.IsExternal);
        SetAudioTempoCorrection(1.0);
        ApplyAudioDelay(0, force: true);
        if (embedded is not null)
            SafePlaybackAction(() => _playback.SelectAudioTrack(embedded.Id));
    }

    private void SetAudioTempoCorrection(double factor)
    {
        var bounded = Math.Clamp(factor, 0.5, 2.0);
        _audioSyncTempoFactor = bounded;
        if (Math.Abs(bounded - 1.0) < 0.0000005)
        {
            SafePlaybackAction(_playback.ClearAudioTempoCorrection);
        }
        else
        {
            SafePlaybackAction(() => _playback.SetAudioTempoCorrection(bounded));
        }
        OnPropertyChanged(nameof(AudioSyncDiagnosticsText));
    }

    private void ClearAudioSyncCorrection()
    {
        _audioSyncTempoFactor = 1.0;
        _audioSyncApplicationKind = AudioSyncApplicationKind.None;
        if (IsReady) SafePlaybackAction(_playback.ClearAudioTempoCorrection);
        OnPropertyChanged(nameof(AudioSyncDiagnosticsText));
    }

    private void ApplyAudioDelay(double seconds, bool force = false)
    {
        var bounded = Math.Clamp(seconds, -120, 120);
        if (!force && Math.Abs(bounded - _appliedAudioDelaySeconds) < 0.008) return;
        _appliedAudioDelaySeconds = bounded;
        var outputDelay = Math.Clamp(bounded + _audioDeviceDelaySeconds, -120, 120);
        OnPropertyChanged(nameof(AudioSyncOffsetText));
        OnPropertyChanged(nameof(AudioSyncDiagnosticsText));
        SafePlaybackAction(() => _playback.SetAudioDelay(outputDelay));
    }

    private void AdjustManualAudioDelay(double deltaSeconds)
    {
        _manualAudioDelaySeconds = Math.Clamp(_manualAudioDelaySeconds + deltaSeconds, -10, 10);
        if (_audioSyncApplicationKind == AudioSyncApplicationKind.Progressive)
        {
            UpdateProgressiveAudioSync(Snapshot.PositionSeconds, force: true, userSeek: false);
        }
        else if (_audioSyncApplicationKind == AudioSyncApplicationKind.PreparedTrack)
        {
            ApplyAudioDelay(_manualAudioDelaySeconds, force: true);
        }
        else if (_audioSyncResult is { CanApplyLiveTempo: true } drift)
        {
            ApplyAudioDelay(drift.EffectiveBaseDelaySeconds + _manualAudioDelaySeconds, force: true);
        }
        else if (_audioSyncResult is { CanApplyFixedDelay: true } fixedResult)
        {
            ApplyAudioDelay(fixedResult.BaseDelaySeconds + _manualAudioDelaySeconds, force: true);
        }
        else
        {
            ApplyAudioDelay(_manualAudioDelaySeconds, force: true);
        }
        AudioSyncStatus = $"Ses gecikmesi elle ayarlandı: {AudioSyncOffsetText}.";
        PersistCurrentMediaAttachmentState();
    }

    private string GetAudioSyncPhaseText()
    {
        return AudioSyncProgress switch
        {
            < 24 => LocalizationManager.Get("Player.AudioSync.Phase.Reference"),
            < 46 => LocalizationManager.Get("Player.AudioSync.Phase.External"),
            < 94 => LocalizationManager.Get("Player.AudioSync.Phase.Analyzing"),
            _ => LocalizationManager.Get("Player.AudioSync.Phase.Finalizing"),
        };
    }

    private static string GetAudioSyncModelText(AudioSyncModelKind model)
    {
        return model switch
        {
            AudioSyncModelKind.LinearDrift => LocalizationManager.Get("Player.AudioSync.Model.Drift"),
            AudioSyncModelKind.Piecewise => LocalizationManager.Get("Player.AudioSync.Model.Piecewise"),
            _ => LocalizationManager.Get("Player.AudioSync.Model.Fixed"),
        };
    }

    private static string CreateAudioSyncErrorSummary(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(message)) return exception.GetType().Name;
        return message.Length <= 180 ? message : message[..180] + "…";
    }

    private void NotifyAudioSyncDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(HasExternalAudio));
        OnPropertyChanged(nameof(AudioSyncEngineText));
        OnPropertyChanged(nameof(AudioSyncOffsetText));
        OnPropertyChanged(nameof(AudioSyncStateText));
        OnPropertyChanged(nameof(AudioSyncProgressText));
        OnPropertyChanged(nameof(AudioSyncProgressSummaryText));
        OnPropertyChanged(nameof(AudioSyncStageText));
        OnPropertyChanged(nameof(AudioSyncEtaText));
        OnPropertyChanged(nameof(AudioSyncGlyphText));
        OnPropertyChanged(nameof(AudioSyncUserMessage));
        OnPropertyChanged(nameof(AudioSyncDiagnosticsText));
        OnPropertyChanged(nameof(AudioSyncActionText));
        OnPropertyChanged(nameof(IsAudioSyncActionComplete));
        OnPropertyChanged(nameof(IsAudioSyncActionRetry));
    }

    private void ResetAudioSyncForNewMedia()
    {
        CancelAudioSyncAnalysis();
        CancelLiveProgressiveAudioAnalysis();
        _audioSyncResult = null;
        _audioSyncSafety = null;
        _externalAudioSource = null;
        _externalAudioHeaders = null;
        _manualAudioDelaySeconds = 0;
        _appliedAudioDelaySeconds = 0;
        _preparedAudio = null;
        _preparedAudioPath = null;
        _progressiveAudioRegions = Array.Empty<AudioSyncProgressiveRegion>();
        _progressiveAudioRegionIndex = -1;
        _progressiveLastPositionSeconds = double.NaN;
        _liveProgressiveAttempts.Clear();
        _liveProgressiveLastAttemptTarget.Clear();
        _liveProgressiveRetryAfter.Clear();
        ClearAudioSyncCorrection();
        NotifyAudioSyncDiagnosticsChanged();
        AudioSyncStatus = "Harici ses eklendiğinde yalnızca istenirse senkronlanır.";
        if (IsReady)
        {
            ApplyAudioDelay(0, force: true);
        }
    }

    private void CancelAudioSyncAnalysis()
    {
        CancelLiveProgressiveAudioAnalysis();
        var cts = _audioSyncCts;
        _audioSyncCts = null;
        IsAudioSyncAnalyzing = false;
        IsAudioSyncPreparing = false;
        AudioSyncProgress = 0;
        _audioSyncProgressStartedAt = DateTimeOffset.MinValue;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
    }

    private void TryDeletePreparedAudio(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            _logger.Info($"Geçici senkron ses daha sonra temizlenecek: {exception.Message}");
        }
    }

    private int? ResolveSubtitleSyncReferenceAudioStreamIndex()
    {
        var selection = OriginalAudioResolver.Resolve(AudioTracks, SelectedAudioTrack);
        if (selection is null)
        {
            _logger.Warning("SubtitleSync Original Audio Resolver: gömülü ses bulunamadı.");
            return null;
        }

        _logger.Info(
            $"SubtitleSync Original Audio Resolver: a:{selection.StreamIndex} · " +
            $"{selection.Track.DisplayName} · {selection.Reason}");
        return selection.StreamIndex;
    }

    private async Task StartSubtitleSyncAsync(string originalPath, string workingPath, string syncDirectory)
    {
        if (string.IsNullOrWhiteSpace(_currentPlaybackSource)) return;

        CancelSubtitleSync();
        var cts = new CancellationTokenSource();
        _subtitleSyncCts = cts;
        IsSubtitleSyncBusy = true;
        SubtitleSyncProgress = 1;
        SubtitleSyncStageText = LocalizationManager.Get("Player.SubtitleSync.Stage.Preparing");
        SubtitleSyncStatus = LocalizationManager.Get("Player.SubtitleSync.User.Analyzing");

        var extension = Path.GetExtension(workingPath);
        var outputPath = Path.Combine(
            syncDirectory,
            Path.GetFileNameWithoutExtension(workingPath) + ".synced" + extension);
        var request = new SubtitleSyncRequest(
            new SubtitleSyncMediaSource(_currentPlaybackSource, _currentPlaybackHeaders),
            originalPath,
            outputPath,
            _settings.SubtitleSyncMaxOffsetSeconds,
            ReferenceAudioStreamIndex: ResolveSubtitleSyncReferenceAudioStreamIndex());
        var progress = new Progress<SubtitleSyncProgress>(value =>
        {
            SubtitleSyncProgress = value.Percent;
            SubtitleSyncStageText = value.Stage;
            SubtitleSyncStatus = value.Stage;
        });

        try
        {
            var result = await _subtitleSyncService
                .SynchronizeAsync(request, progress, cts.Token)
                .ConfigureAwait(true);
            if (!ReferenceEquals(_subtitleSyncCts, cts)) return;

            _subtitleSyncResult = result;
            SubtitleSyncStatus = result.Message;
            SubtitleSyncStageText = result.Message;
            SubtitleSyncProgress = result.Verdict is SubtitleSyncVerdict.Synchronized or SubtitleSyncVerdict.AlreadyAligned
                ? 100
                : 0;

            if (result.Verdict == SubtitleSyncVerdict.Synchronized &&
                result.CanApply &&
                !string.IsNullOrWhiteSpace(result.OutputPath) &&
                File.Exists(result.OutputPath) &&
                string.Equals(_subtitleSyncWorkingPath, workingPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(result.OutputPath, workingPath, overwrite: true);
                SafePlaybackAction(_playback.ReloadSubtitle);
                StatusMessage = LocalizationManager.Get("Player.SubtitleSync.User.Synchronized");
            }
            else if (result.Verdict == SubtitleSyncVerdict.AlreadyAligned)
            {
                StatusMessage = LocalizationManager.Get("Player.SubtitleSync.User.AlreadyAligned");
            }
            else if (result.Verdict is SubtitleSyncVerdict.Uncertain or SubtitleSyncVerdict.NoMatch)
            {
                StatusMessage = LocalizationManager.Get("Player.SubtitleSync.User.KeptOriginal");
            }

            _subtitleSyncFinalized = result.Verdict is SubtitleSyncVerdict.Synchronized
                or SubtitleSyncVerdict.AlreadyAligned
                or SubtitleSyncVerdict.Uncertain
                or SubtitleSyncVerdict.NoMatch;
            PersistCurrentMediaAttachmentState();
            OnPropertyChanged(nameof(SubtitleSyncProgressSummaryText));
            OnPropertyChanged(nameof(SubtitleSyncGlyphText));
            OnPropertyChanged(nameof(SubtitleSyncActionText));
            OnPropertyChanged(nameof(IsSubtitleSyncActionComplete));
            OnPropertyChanged(nameof(IsSubtitleSyncActionRetry));
        }
        catch (OperationCanceledException)
        {
            // A new subtitle/media replaced this analysis.
        }
        catch (Exception exception)
        {
            _logger.Error("Altyazı senkron sonucu uygulanamadı.", exception);
            SubtitleSyncStatus = LocalizationManager.Get("Player.SubtitleSync.State.Failed");
            SubtitleSyncStageText = SubtitleSyncStatus;
            SubtitleSyncProgress = 0;
        }
        finally
        {
            if (ReferenceEquals(_subtitleSyncCts, cts))
            {
                _subtitleSyncCts = null;
                IsSubtitleSyncBusy = false;
            }
            cts.Dispose();
        }
    }

    private void ResetSubtitleSyncForNewMedia()
    {
        CancelSubtitleSync();
        _subtitleSyncResult = null;
        _subtitleSyncWorkingPath = null;
        _primarySubtitleOriginalPath = null;
        _secondarySubtitlePath = null;
        _subtitleSyncFinalized = false;
        _hasSubtitleSyncSession = false;
        SubtitleSyncProgress = 0;
        SubtitleSyncStageText = LocalizationManager.Get("Player.SubtitleSync.State.Ready");
        SubtitleSyncStatus = LocalizationManager.Get("Player.SubtitleSync.User.Ready");
        OnPropertyChanged(nameof(IsSubtitleSyncVisible));
        OnPropertyChanged(nameof(SubtitleSyncProgressSummaryText));
        OnPropertyChanged(nameof(SubtitleSyncGlyphText));
        OnPropertyChanged(nameof(SubtitleSyncActionText));
        OnPropertyChanged(nameof(IsSubtitleSyncActionComplete));
        OnPropertyChanged(nameof(IsSubtitleSyncActionRetry));
    }

    private void CancelSubtitleSync()
    {
        var cts = _subtitleSyncCts;
        _subtitleSyncCts = null;
        IsSubtitleSyncBusy = false;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
    }

    public void SeekTo(double seconds)
    {
        if (!IsReady || Snapshot.DurationSeconds <= 0)
        {
            return;
        }

        SafePlaybackAction(() => _playback.SeekAbsolute(seconds));
    }

    public void AdjustVolume(double delta) => Volume += delta;

    public Task PlaySelectedAsync() =>
        SelectedPlaylistItem is null ? Task.CompletedTask : OpenPlaylistItemAsync(SelectedPlaylistItem);

    public async Task PlayNextAsync()
    {
        if (Playlist.Count == 0)
        {
            return;
        }

        var currentIndex = GetCurrentPlaylistIndex();
        var nextIndex = currentIndex < 0 ? 0 : currentIndex + 1;
        if (nextIndex >= Playlist.Count)
        {
            StatusMessage = "Oynatma listesinin sonuna ulaşıldı.";
            return;
        }

        SelectedPlaylistItem = Playlist[nextIndex];
        await OpenPlaylistItemAsync(SelectedPlaylistItem).ConfigureAwait(true);
    }

    public async Task PlayPreviousAsync()
    {
        if (Playlist.Count == 0)
        {
            return;
        }

        if (Snapshot.PositionSeconds > 5)
        {
            SeekTo(0);
            return;
        }

        var currentIndex = GetCurrentPlaylistIndex();
        var previousIndex = currentIndex <= 0 ? 0 : currentIndex - 1;
        SelectedPlaylistItem = Playlist[previousIndex];
        await OpenPlaylistItemAsync(SelectedPlaylistItem).ConfigureAwait(true);
    }

    public void RemoveSelected()
    {
        if (SelectedPlaylistItem is null)
        {
            return;
        }

        var index = Playlist.IndexOf(SelectedPlaylistItem);
        Playlist.Remove(SelectedPlaylistItem);
        SelectedPlaylistItem = Playlist.Count == 0
            ? null
            : Playlist[Math.Clamp(index, 0, Playlist.Count - 1)];
        OnPropertyChanged(nameof(PlaylistSummary));
        QueueCloudPlaylistSync();
    }

    public void ClearPlaylist()
    {
        _cloudPlaylistSyncCts?.Cancel();
        _cloudPlaylistSyncCts?.Dispose();
        _cloudPlaylistSyncCts = null;

        _suppressCloudPlaylistSync = true;
        try
        {
            Playlist.Clear();
            SelectedPlaylistItem = null;
            OnPropertyChanged(nameof(PlaylistSummary));
        }
        finally
        {
            _suppressCloudPlaylistSync = false;
        }

        StatusMessage = LocalizationManager.Get("Cloud.Playlist.LocalCleared");
        CloudPlaylistStatus = LocalizationManager.Format("Cloud.Playlist.CloudPreserved", _cloudPlaylistCount);
    }

    public void TakeScreenshot()
    {
        if (!IsReady || !HasMedia)
        {
            StatusMessage = "Ekran görüntüsü için önce bir video açın.";
            return;
        }

        try
        {
            var safeTitle = MakeSafeFileName(Path.GetFileNameWithoutExtension(CurrentTitle));
            var path = Path.Combine(
                _paths.ScreenshotDirectory,
                $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            _playback.TakeScreenshot(path);
            StatusMessage = $"Ekran görüntüsü kaydedildi: {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            _logger.Error("Ekran görüntüsü alınamadı.", exception);
            StatusMessage = "Ekran görüntüsü alınamadı.";
        }
    }

    private void SeekRelative(double seconds)
    {
        if (!IsReady || !HasMedia) return;
        SafePlaybackAction(() => _playback.SeekRelative(seconds));
    }

    private void GoToTime()
    {
        if (!TryParsePlaybackTime(GoToTimeText, out var seconds))
        {
            StatusMessage = LocalizationManager.Get("Player.GoToTime.Invalid");
            return;
        }
        SeekTo(seconds);
        StatusMessage = $"{LocalizationManager.Get("Player.GoToTime.Done")}: {FormatTime(seconds)}";
    }

    private static bool TryParsePlaybackTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (TimeSpan.TryParse(value, CultureInfo.CurrentCulture, out var span) || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out span))
        {
            seconds = Math.Max(0, span.TotalSeconds);
            return true;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var raw) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out raw))
        {
            seconds = Math.Max(0, raw);
            return true;
        }
        return false;
    }

    private void ApplySubtitlePresentationSettings()
    {
        SafePlaybackAction(() => _playback.SetSubtitleDelay(_settings.PrimarySubtitleDelaySeconds));
        SafePlaybackAction(() => _playback.SetSubtitleDelay(_settings.SecondarySubtitleDelaySeconds, true));
        SafePlaybackAction(() => _playback.SetSubtitleScale(_settings.PrimarySubtitleScale));
        SafePlaybackAction(() => _playback.SetSubtitleScale(_settings.SecondarySubtitleScale, true));
        SafePlaybackAction(() => _playback.SetSubtitlePosition(_settings.PrimarySubtitlePosition));
        SafePlaybackAction(() => _playback.SetSubtitlePosition(_settings.SecondarySubtitlePosition, true));
        SafePlaybackAction(() => _playback.SetSubtitleAssOverride(_settings.PreservePrimaryAssStyle));
        SafePlaybackAction(() => _playback.SetSubtitleAssOverride(_settings.PreserveSecondaryAssStyle, true));
    }

    private async Task OpenFileWithFolderPlaylistAsync(string file, bool replacePlaylist)
    {
        var fullPath = Path.GetFullPath(file);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            await OpenFilesAsync(new[] { fullPath }, replacePlaylist).ConfigureAwait(true);
            return;
        }
        string[] siblings;
        try
        {
            siblings = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(candidate => VideoExtensions.Contains(Path.GetExtension(candidate)))
                .OrderBy(NaturalSortKey, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(candidate => candidate, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            siblings = new[] { fullPath };
        }
        if (replacePlaylist) Playlist.Clear();
        foreach (var source in siblings)
        {
            if (Playlist.Any(item => string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase))) continue;
            Playlist.Add(CreatePlaylistItem(source));
        }
        OnPropertyChanged(nameof(PlaylistSummary));
        QueueCloudPlaylistSync();
        SelectedPlaylistItem = Playlist.FirstOrDefault(item => string.Equals(item.Source, fullPath, StringComparison.OrdinalIgnoreCase))
                               ?? Playlist.FirstOrDefault();
        if (SelectedPlaylistItem is not null) await OpenPlaylistItemAsync(SelectedPlaylistItem).ConfigureAwait(true);
    }

    private static string NaturalSortKey(string path) => Regex.Replace(
        Path.GetFileName(path),
        @"\d+",
        match => match.Value.PadLeft(20, '0'));

    private async Task OpenFileAsync()
    {
        var selected = _dialogs.PickVideos(_settings.LastOpenedDirectory);
        if (selected.Count == 0)
        {
            return;
        }

        _settings.LastOpenedDirectory = Path.GetDirectoryName(selected[0]);
        await SaveSettingsAsync().ConfigureAwait(true);
        if (AutoAddFolderVideos && selected.Count == 1)
        {
            await OpenFileWithFolderPlaylistAsync(selected[0], replacePlaylist: true).ConfigureAwait(true);
        }
        else
        {
            await OpenFilesAsync(selected, replacePlaylist: true).ConfigureAwait(true);
        }
    }

    private Task OpenUrlAsync()
    {
        ShowMediaCenter();
        Sources.ShowConnectionsHome();
        StatusMessage = "Web bağlantısını Medya Merkezi alanına yapıştırın.";
        return Task.CompletedTask;
    }

    private Task AttachSubtitleAsync()
    {
        var selected = _dialogs.PickSubtitle(_settings.LastOpenedDirectory);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            AttachSubtitlePath(selected);
        }

        return Task.CompletedTask;
    }

    private async Task AttachSecondarySubtitleAsync()
    {
        if (!HasMedia)
        {
            StatusMessage = LocalizationManager.Get("Player.SecondarySubtitle.RequiresMedia");
            return;
        }

        var selected = _dialogs.PickSubtitle(_settings.LastOpenedDirectory);
        if (string.IsNullOrWhiteSpace(selected)) return;
        try
        {
            _settings.LastOpenedDirectory = Path.GetDirectoryName(selected);
            await SaveSettingsAsync().ConfigureAwait(true);
            var existingIds = SecondarySubtitleTracks.Where(track => !track.IsDisabled).Select(track => track.Id).ToHashSet();
            SafePlaybackAction(() => _playback.AddSubtitle(selected, select: false));
            await Task.Delay(120).ConfigureAwait(true);
            var added = SecondarySubtitleTracks.LastOrDefault(track => !track.IsDisabled && !existingIds.Contains(track.Id))
                        ?? SecondarySubtitleTracks.LastOrDefault(track => !track.IsDisabled);
            if (added is not null) SelectedSecondarySubtitleTrack = added;
            _secondarySubtitlePath = selected;
            PersistCurrentMediaAttachmentState();
            StatusMessage = $"{LocalizationManager.Get("Player.SecondarySubtitle.Added")}: {Path.GetFileName(selected)}";
        }
        catch (Exception exception)
        {
            _logger.Error("İkinci altyazı eklenemedi.", exception);
            StatusMessage = LocalizationManager.Get("Player.SecondarySubtitle.Failed");
        }
    }

    private async Task AttachAudioAsync()
    {
        if (!HasMedia)
        {
            StatusMessage = "Harici ses eklemek için önce bir video açın.";
            return;
        }

        var selected = _dialogs.PickAudio(_settings.LastOpenedDirectory);
        if (string.IsNullOrWhiteSpace(selected)) return;

        try
        {
            _settings.LastOpenedDirectory = Path.GetDirectoryName(selected);
            await SaveSettingsAsync().ConfigureAwait(true);
            if (ArchiveAudioHelper.IsSupportedArchive(selected))
            {
                StatusMessage = "Ses arşivi inceleniyor…";
                var extracted = await ArchiveAudioHelper.ExtractAudioAsync(
                    selected,
                    Path.Combine(_paths.RemoteCacheDirectory, "audio-extracted"),
                    _dialogs).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    StatusMessage = "Arşivden ses seçimi iptal edildi.";
                    return;
                }
                AttachAudioPath(extracted);
                return;
            }

            AttachAudioPath(selected);
        }
        catch (Exception exception)
        {
            _logger.Error("Harici ses eklenemedi.", exception);
            StatusMessage = "Harici ses eklenemedi.";
            _dialogs.ShowError($"Harici ses eklenemedi.\n\n{exception.Message}");
        }
    }

    private void BeginRemoteAudioSelection()
    {
        if (!HasMedia)
        {
            StatusMessage = "Harici ses eklemek için önce bir video açın.";
            return;
        }

        _resumeAfterExternalAudioSelection = !Snapshot.IsPaused;
        ShowMediaCenter();
        SelectedSidebarIndex = 0;
        Sources.BeginAudioSelection();
        StatusMessage = "Bulut, paylaşım bağlantısı veya WebDAV üzerinden harici ses seçin.";
    }

    private void BeginTelegramAudioSelection()
    {
        if (!HasMedia)
        {
            StatusMessage = "Harici ses eklemek için önce bir video açın.";
            return;
        }

        _resumeAfterExternalAudioSelection = !Snapshot.IsPaused;
        ShowMediaCenter();
        SelectedSidebarIndex = 5;
        Telegram.BeginAudioSelection();
        StatusMessage = "Telegram üzerinden bir ses dosyası veya ses arşivi seçin.";
    }

    private Task OpenPlaylistItemAsync(PlaylistItem item) => OpenSourceAsync(item.Source, item.HttpHeaders);

    private async Task OpenRemoteSourceAsync(RemoteOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = Playlist.FirstOrDefault(item =>
            string.Equals(item.Source, request.Source, StringComparison.OrdinalIgnoreCase));
        var playlistItem = existing ?? new PlaylistItem(
            request.Source,
            string.IsNullOrWhiteSpace(request.DisplayName) ? CreateDisplayTitle(request.Source) : request.DisplayName,
            false,
            request.HttpHeaders,
            request.Provider);
        if (existing is null)
        {
            Playlist.Add(playlistItem);
            OnPropertyChanged(nameof(PlaylistSummary));
            QueueCloudPlaylistSync();
        }
        SelectedPlaylistItem = playlistItem;
        await OpenSourceAsync(
            request.Source,
            request.HttpHeaders,
            request.PersistentSource,
            addRecent: true,
            recentTitle: request.DisplayName).ConfigureAwait(true);
    }

    private async Task OpenSourceAsync(
        string source,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        string? resumeKey = null,
        bool addRecent = true,
        string? recentTitle = null)
    {
        if (File.Exists(source))
        {
            _settings.LastOpenedDirectory = Path.GetDirectoryName(source);
        }
        else if (!Uri.TryCreate(source, UriKind.Absolute, out _))
        {
            StatusMessage = "Kaynak bulunamadı.";
            return;
        }

        if (!await EnsurePlaybackReadyAsync().ConfigureAwait(true))
        {
            IsMediaCenterVisible = true;
            return;
        }

        try
        {
            PersistCurrentMediaAttachmentState();
            await PersistResumeAsync().ConfigureAwait(true);
            ResetAudioSyncForNewMedia();
            ResetSubtitleSyncForNewMedia();
            SelectedSidebarIndex = 3;
            IsMediaCenterVisible = false;
            var identitySource = string.IsNullOrWhiteSpace(resumeKey) ? source : resumeKey;
            var resume = _settings.RememberPlaybackPosition
                ? await _resumeStore.GetAsync(identitySource).ConfigureAwait(true)
                : null;

            Subtitles.UpdateCurrentMedia(identitySource, 0);
            StatusMessage = "Video açılıyor…";
            _currentPlaybackSource = source;
            _currentPlaybackHeaders = httpHeaders is null
                ? null
                : httpHeaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(CurrentPlaybackSource));
            OnPropertyChanged(nameof(CurrentPlaybackHeaders));
            await _playback.OpenAsync(source, resume?.PositionSeconds ?? 0, httpHeaders).ConfigureAwait(true);
            ApplyCurrentAudioDeviceAndDelay();
            ApplyVideoProfileForCurrentSource();
            ApplyVideoEqualizer();
            _currentResumeKey = identitySource;
            var restoredAttachments = await RestoreSavedMediaAttachmentsAsync(identitySource).ConfigureAwait(true);
            if (addRecent) TouchRecentSource(identitySource, recentTitle);
            SelectedPlaylistItem = Playlist.FirstOrDefault(item =>
                string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase));
            if (!restoredAttachments)
            {
                StatusMessage = resume is { PositionSeconds: > 5 }
                    ? $"Kaldığınız yerden devam ediliyor: {FormatTime(resume.PositionSeconds)}"
                    : "Video açılıyor…";
            }
            await SaveSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            IsMediaCenterVisible = true;
            _logger.Error($"Kaynak açılamadı: {SafeLogSource(source)}", exception);
            StatusMessage = "Kaynak açılamadı.";
            _dialogs.ShowError($"Video açılamadı.\n\n{exception.Message}");
        }
    }

    private void ShowMediaCenter()
    {
        if (IsReady && HasMedia && !Snapshot.IsPaused)
        {
            SafePlaybackAction(_playback.TogglePause);
        }

        SelectedSidebarIndex = 0;
        IsMediaCenterVisible = true;
        _ = SyncCloudProgressAsync();
        _ = RefreshContinueWatchingAsync();
        StatusMessage = "Medya merkezi hazır · Hesaplarınızı yönetin veya bir kaynak seçin.";
    }

    private static string SafeLogSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return $"{uri.Scheme}://{uri.Host}/…";
        return source;
    }

    private void PlaybackOnSnapshotChanged(object? sender, PlaybackSnapshot snapshot)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var previousProgressivePosition = _progressiveLastPositionSeconds;
            Snapshot = snapshot;
            if (_audioSyncApplicationKind == AudioSyncApplicationKind.Progressive)
            {
                var userSeek = !double.IsNaN(previousProgressivePosition) &&
                               Math.Abs(snapshot.PositionSeconds - previousProgressivePosition) > 4.0;
                _progressiveLastPositionSeconds = snapshot.PositionSeconds;
                UpdateProgressiveAudioSync(snapshot.PositionSeconds, force: false, userSeek);
                ScheduleLiveProgressiveAudioSync(snapshot.PositionSeconds, userSeek);
            }
            Subtitles.UpdateCurrentMedia(_currentResumeKey ?? snapshot.Source, snapshot.DurationSeconds);
            if (!string.IsNullOrWhiteSpace(snapshot.Source)) Library.UpdateVisibleProgress(snapshot.Source, snapshot.PositionSeconds, snapshot.DurationSeconds);
            if (!snapshot.IsIdle)
            {
                StatusMessage = snapshot.IsBuffering
                    ? "Arabelleğe alınıyor…"
                    : snapshot.IsPaused
                        ? "Duraklatıldı"
                        : "Oynatılıyor";
            }

            if (snapshot.Source is not null &&
                DateTimeOffset.UtcNow - _lastResumeWrite > TimeSpan.FromSeconds(5))
            {
                _lastResumeWrite = DateTimeOffset.UtcNow;
                _ = PersistResumeAsync();
            }
        });
    }

    private void PlaybackOnTracksChanged(object? sender, IReadOnlyList<MediaTrack> tracks)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _suppressTrackSelection = true;
            try
            {
                AudioTracks.Clear();
                foreach (var track in tracks.Where(track => track.Type == TrackType.Audio))
                {
                    AudioTracks.Add(track);
                }

                SubtitleTracks.Clear();
                SecondarySubtitleTracks.Clear();
                SubtitleTracks.Add(MediaTrack.DisabledSubtitle());
                SecondarySubtitleTracks.Add(MediaTrack.DisabledSubtitle());
                foreach (var track in tracks.Where(track => track.Type == TrackType.Subtitle))
                {
                    SubtitleTracks.Add(track);
                    SecondarySubtitleTracks.Add(track);
                }

                _selectedAudioTrack = AudioTracks.FirstOrDefault(track => track.IsSelected)
                                      ?? AudioTracks.FirstOrDefault();
                _selectedSubtitleTrack = SubtitleTracks.FirstOrDefault(track => track.IsSelected && !track.IsDisabled)
                                         ?? SubtitleTracks[0];
                _selectedSecondarySubtitleTrack = _selectedSecondarySubtitleTrackId.HasValue
                    ? SecondarySubtitleTracks.FirstOrDefault(track => track.Id == _selectedSecondarySubtitleTrackId.Value) ?? SecondarySubtitleTracks[0]
                    : SecondarySubtitleTracks[0];
                if (_selectedSecondarySubtitleTrack is { IsDisabled: false } secondary && _selectedSubtitleTrack is { IsDisabled: false } primary && secondary.Id == primary.Id)
                {
                    _selectedSecondarySubtitleTrack = SecondarySubtitleTracks[0];
                    _selectedSecondarySubtitleTrackId = null;
                    SafePlaybackAction(() => _playback.SelectSecondarySubtitleTrack(null));
                }
                OnPropertyChanged(nameof(SelectedAudioTrack));
                OnPropertyChanged(nameof(SelectedSubtitleTrack));
                OnPropertyChanged(nameof(SelectedSecondarySubtitleTrack));
            }
            finally
            {
                _suppressTrackSelection = false;
            }
        });
    }

    private void PlaybackOnChaptersChanged(object? sender, IReadOnlyList<ChapterInfo> chapters)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _suppressChapterSelection = true;
            try
            {
                Chapters.Clear();
                foreach (var chapter in chapters)
                {
                    Chapters.Add(chapter);
                }

                _selectedChapter = Chapters.LastOrDefault(chapter => chapter.TimeSeconds <= Snapshot.PositionSeconds + 0.5)
                                   ?? Chapters.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedChapter));
            }
            finally
            {
                _suppressChapterSelection = false;
            }
        });
    }

    private void PlaybackOnPlaybackEnded(object? sender, EventArgs e)
    {
        if (!AutoPlayNext)
        {
            return;
        }

        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _ = PlayNextAsync());
    }

    private void PlaybackOnErrorOccurred(object? sender, string message)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = message);
    }

    private void TouchRecentSource(string source, string? displayTitle = null)
    {
        var title = string.IsNullOrWhiteSpace(displayTitle) ? CreateDisplayTitle(source) : displayTitle.Trim();
        var isLocal = File.Exists(source);
        var entry = new RecentSourceEntry(source, title, DateTimeOffset.UtcNow, isLocal);

        var existing = _settings.RecentSources.FirstOrDefault(item =>
            string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _settings.RecentSources.Remove(existing);
        }

        _settings.RecentSources.Insert(0, entry);
        if (_settings.RecentSources.Count > 15)
        {
            _settings.RecentSources.RemoveRange(15, _settings.RecentSources.Count - 15);
        }

        RecentSources.Clear();
        foreach (var item in _settings.RecentSources)
        {
            RecentSources.Add(item);
        }

        HistoryItems.Clear();
        foreach (var item in RecentSources)
        {
            HistoryItems.Add(CreateHistoryItem(item));
        }

        _ = EnrichHistoryArtworkAsync(HistoryItems.ToArray());
    }

    private static HistoryItem CreateHistoryItem(RecentSourceEntry entry) =>
        new(
            entry.Source,
            entry.Title,
            entry.LastOpenedUtc,
            entry.IsLocal);

    private async Task EnrichHistoryArtworkAsync(
        IReadOnlyList<HistoryItem> items)
    {
        if (items.Count == 0 || _disposed)
            return;

        _historyArtworkCts?.Cancel();
        _historyArtworkCts?.Dispose();
        _historyArtworkCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var cancellationToken = _historyArtworkCts.Token;

        try
        {
            var work = items
                .Take(15)
                .Select(item =>
                {
                    var identity = BuildHistoryIdentity(item);
                    var key = CloudMediaIdentity.CreateMediaKey(identity, identity.Title);
                    return new HistoryArtworkWork(item, identity, key);
                })
                .ToArray();

            var cache = await ReadMediaArtworkCacheAsync(cancellationToken).ConfigureAwait(false);
            var misses = new List<HistoryArtworkWork>();

            foreach (var item in work)
            {
                if (cache.TryGetValue(item.CacheKey, out var cached)
                    && cached.ExpiresAtUtc > DateTimeOffset.UtcNow
                    && (!string.IsNullOrWhiteSpace(cached.PosterUrl)
                        || !string.IsNullOrWhiteSpace(cached.BackdropUrl)))
                {
                    await ApplyHistoryArtworkAsync(
                            item.Item.Source,
                            cached.Title,
                            cached.PosterUrl,
                            cached.BackdropUrl,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    misses.Add(item);
                }
            }

            if (misses.Count == 0 || !Account.IsSignedIn)
                return;

            var results = await _mediaArtwork
                .ResolveBatchAsync(
                    misses.Select(item => item.Identity).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            var cacheUpdates = new List<KeyValuePair<string, SharedMediaArtworkEntry>>();
            for (var index = 0; index < misses.Count && index < results.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = results[index];
                var media = result.Media;
                if (!result.Success
                    || !result.Matched
                    || media is null
                    || (string.IsNullOrWhiteSpace(media.PosterUrl)
                        && string.IsNullOrWhiteSpace(media.BackdropUrl)))
                {
                    continue;
                }

                var cached = new SharedMediaArtworkEntry(
                    media.Title,
                    media.PosterUrl,
                    media.BackdropUrl,
                    DateTimeOffset.UtcNow.AddDays(30));

                cacheUpdates.Add(new KeyValuePair<string, SharedMediaArtworkEntry>(
                    misses[index].CacheKey,
                    cached));

                await ApplyHistoryArtworkAsync(
                        misses[index].Item.Source,
                        media.Title,
                        media.PosterUrl,
                        media.BackdropUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (cacheUpdates.Count > 0)
            {
                await UpdateMediaArtworkCacheAsync(
                        cacheUpdates,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer history refresh, Cloud login or shutdown superseded this pass.
        }
        catch (Exception exception)
        {
            _logger.Warning($"Geçmiş görselleri zenginleştirilemedi: {exception.Message}");
        }
    }

    private MediaIdentity BuildHistoryIdentity(HistoryItem item)
    {
        var releaseName = item.IsLocal
            ? Path.GetFileName(item.Source)
            : item.Title;

        var parsed = _releaseParser.Parse(releaseName, item.Title);
        var title = string.IsNullOrWhiteSpace(parsed.Title)
            ? item.Title.Trim()
            : parsed.Title.Trim();

        return parsed with
        {
            Title = title,
            ReleaseName = releaseName
        };
    }

    private async Task ApplyHistoryArtworkAsync(
        string source,
        string? resolvedTitle,
        string? posterUrl,
        string? backdropUrl,
        CancellationToken cancellationToken)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || cancellationToken.IsCancellationRequested)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            for (var index = 0; index < HistoryItems.Count; index++)
            {
                var current = HistoryItems[index];
                if (!string.Equals(current.Source, source, StringComparison.OrdinalIgnoreCase))
                    continue;

                HistoryItems[index] = current with
                {
                    Title = string.IsNullOrWhiteSpace(resolvedTitle)
                        ? current.Title
                        : resolvedTitle.Trim(),
                    PosterUrl = string.IsNullOrWhiteSpace(posterUrl) ? null : posterUrl,
                    BackdropUrl = string.IsNullOrWhiteSpace(backdropUrl) ? null : backdropUrl
                };
                break;
            }
        });
    }

    private async Task RefreshContinueWatchingAsync()
    {
        try
        {
            var resumes = await _resumeStore.GetAllAsync().ConfigureAwait(false);
            var recentBySource = _settings.RecentSources
                .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var items = new List<ContinueWatchingItem>();
            foreach (var resume in resumes
                         .Where(item => item.PositionSeconds >= 5 && item.DurationSeconds > 0 && item.DurationSeconds - item.PositionSeconds >= 45)
                         .OrderByDescending(item => item.UpdatedAtUtc)
                         .Take(10))
            {
                var isLocal = File.Exists(resume.Source);
                if (!isLocal)
                {
                    if (!Uri.TryCreate(resume.Source, UriKind.Absolute, out var remoteUri) ||
                        (remoteUri.Scheme != Uri.UriSchemeHttp && remoteUri.Scheme != Uri.UriSchemeHttps))
                    {
                        continue;
                    }
                }

                var title = recentBySource.TryGetValue(resume.Source, out var recent) &&
                            !string.IsNullOrWhiteSpace(recent.Title)
                    ? recent.Title
                    : CreateDisplayTitle(resume.Source);

                if (IsLoopbackPlaceholderTitle(resume.Source, title))
                {
                    continue;
                }

                items.Add(new ContinueWatchingItem(
                    resume.Source,
                    title,
                    resume.PositionSeconds,
                    resume.DurationSeconds,
                    resume.UpdatedAtUtc,
                    isLocal));
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            await dispatcher.InvokeAsync(() =>
            {
                ContinueWatching.Clear();
                foreach (var item in items) ContinueWatching.Add(item);
            });

            // Home never waits for metadata. Artwork arrives afterwards from
            // local cache or one batched Internal API request.
            _ = EnrichContinueWatchingArtworkAsync(items);
        }
        catch (Exception exception)
        {
            _logger.Error("İzlemeye devam et listesi yüklenemedi.", exception);
        }
    }

    private async Task EnrichContinueWatchingArtworkAsync(
        IReadOnlyList<ContinueWatchingItem> items)
    {
        if (items.Count == 0 || _disposed)
            return;

        _continueWatchingArtworkCts?.Cancel();
        _continueWatchingArtworkCts?.Dispose();
        _continueWatchingArtworkCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var cancellationToken = _continueWatchingArtworkCts.Token;

        try
        {
            var work = items
                .Select(item =>
                {
                    var identity = BuildContinueWatchingIdentity(item);
                    var key = CloudMediaIdentity.CreateMediaKey(identity, identity.Title);
                    return new ContinueWatchingArtworkWork(item, identity, key);
                })
                .ToArray();

            var cache = await ReadMediaArtworkCacheAsync(cancellationToken).ConfigureAwait(false);
            var misses = new List<ContinueWatchingArtworkWork>();

            foreach (var item in work)
            {
                if (cache.TryGetValue(item.CacheKey, out var cached)
                    && cached.ExpiresAtUtc > DateTimeOffset.UtcNow
                    && (!string.IsNullOrWhiteSpace(cached.PosterUrl)
                        || !string.IsNullOrWhiteSpace(cached.BackdropUrl)))
                {
                    await ApplyContinueWatchingArtworkAsync(
                            item.Item.Source,
                            cached.Title,
                            cached.PosterUrl,
                            cached.BackdropUrl,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    misses.Add(item);
                }
            }

            if (misses.Count == 0 || !Account.IsSignedIn)
                return;

            var results = await _mediaArtwork
                .ResolveBatchAsync(
                    misses.Select(item => item.Identity).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            var cacheUpdates = new List<KeyValuePair<string, SharedMediaArtworkEntry>>();
            for (var index = 0; index < misses.Count && index < results.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = results[index];
                var media = result.Media;
                if (!result.Success
                    || !result.Matched
                    || media is null
                    || (string.IsNullOrWhiteSpace(media.PosterUrl)
                        && string.IsNullOrWhiteSpace(media.BackdropUrl)))
                {
                    continue;
                }

                var cached = new SharedMediaArtworkEntry(
                    media.Title,
                    media.PosterUrl,
                    media.BackdropUrl,
                    DateTimeOffset.UtcNow.AddDays(30));

                cacheUpdates.Add(new KeyValuePair<string, SharedMediaArtworkEntry>(
                    misses[index].CacheKey,
                    cached));

                await ApplyContinueWatchingArtworkAsync(
                        misses[index].Item.Source,
                        media.Title,
                        media.PosterUrl,
                        media.BackdropUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (cacheUpdates.Count > 0)
            {
                await UpdateMediaArtworkCacheAsync(
                        cacheUpdates,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer Home refresh, Cloud login or shutdown superseded this pass.
        }
        catch (Exception exception)
        {
            _logger.Warning($"İzlemeye devam et görselleri zenginleştirilemedi: {exception.Message}");
        }
    }

    private MediaIdentity BuildContinueWatchingIdentity(ContinueWatchingItem item)
    {
        var releaseName = item.IsLocal
            ? Path.GetFileName(item.Source)
            : item.Title;

        var parsed = _releaseParser.Parse(releaseName, item.Title);
        var title = string.IsNullOrWhiteSpace(parsed.Title)
            ? item.Title.Trim()
            : parsed.Title.Trim();

        return parsed with
        {
            Title = title,
            ReleaseName = releaseName
        };
    }

    private async Task ApplyContinueWatchingArtworkAsync(
        string source,
        string? resolvedTitle,
        string? posterUrl,
        string? backdropUrl,
        CancellationToken cancellationToken)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || cancellationToken.IsCancellationRequested)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            for (var index = 0; index < ContinueWatching.Count; index++)
            {
                var current = ContinueWatching[index];
                if (!string.Equals(current.Source, source, StringComparison.OrdinalIgnoreCase))
                    continue;

                ContinueWatching[index] = current with
                {
                    Title = string.IsNullOrWhiteSpace(resolvedTitle)
                        ? current.Title
                        : resolvedTitle.Trim(),
                    PosterUrl = string.IsNullOrWhiteSpace(posterUrl) ? null : posterUrl,
                    BackdropUrl = string.IsNullOrWhiteSpace(backdropUrl) ? null : backdropUrl
                };
                break;
            }
        });
    }

    private Task<Dictionary<string, SharedMediaArtworkEntry>>
        ReadMediaArtworkCacheAsync(CancellationToken cancellationToken) =>
        _mediaArtwork.ReadSnapshotAsync(cancellationToken);

    private Task UpdateMediaArtworkCacheAsync(
        IReadOnlyList<KeyValuePair<string, SharedMediaArtworkEntry>> updates,
        CancellationToken cancellationToken) =>
        _mediaArtwork.UpdateAsync(updates, cancellationToken);

    private async Task OpenExternalReleaseAsync(ExternalRelease release)
    {
        SelectedSidebarIndex = 8;

        var magnetCandidates = new List<string>();
        if (Uri.TryCreate(release.MagnetUri, UriKind.Absolute, out var directMagnet) &&
            directMagnet.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            magnetCandidates.Add(directMagnet.AbsoluteUri);
        }

        // Prowlarr çoğu zaman MagnetUrl alanını kendi /download proxy adresi
        // olarak döndürür. Yönlendirmedeki tam magnet'i (tracker'lar dahil) al.
        var resolvedMagnet = await Connections.ResolveMagnetAsync(release).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(resolvedMagnet) &&
            !magnetCandidates.Contains(resolvedMagnet, StringComparer.OrdinalIgnoreCase))
        {
            magnetCandidates.Add(resolvedMagnet);
        }

        Exception? lastFailure = null;
        foreach (var magnetUri in magnetCandidates)
        {
            using var metadataTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await Torrent.OpenMagnetOrThrowAsync(
                        magnetUri,
                        cancellationToken: metadataTimeout.Token,
                        displayTitle: release.Title)
                    .ConfigureAwait(true);
                return;
            }
            catch (OperationCanceledException) when (metadataTimeout.IsCancellationRequested)
            {
                lastFailure = new TimeoutException(
                    "Magnet metadata bilgisi 20 saniye içinde alınamadı.");
                _logger.Warning(
                    $"Magnet metadata zaman aşımına uğradı; .torrent yedeği denenecek: {release.Title}");
            }
            catch (OperationCanceledException)
            {
                // Kullanıcı Torrent panelinden Kapat dedi. Keşfet komutu hemen
                // serbest kalsın; otomatik olarak başka bir kaynağa geçme.
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                _logger.Warning(
                    $"Magnet açılamadı; .torrent yedeği denenecek: {exception.Message}");
            }
        }

        // Magnet başarısız olduğunda eski kod burada dönmüş sayılıyordu çünkü
        // TorrentPanel hatayı yutuyordu. Rev10.6.12 programatik hatayı yukarı
        // taşıyor ve Prowlarr/Torznab'ın authoritative .torrent kaynağını dener.
        if (Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var torrentUri) &&
            torrentUri.Scheme is "http" or "https")
        {
            try
            {
                var path = await Connections.DownloadTorrentAsync(release).ConfigureAwait(true);
                await Torrent.OpenTorrentFileOrThrowAsync(
                        path,
                        displayTitle: release.Title)
                    .ConfigureAwait(true);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                _logger.Warning($".torrent yedeği açılamadı: {exception.Message}");
            }
        }

        // Sağlayıcı yalnız info-hash verdiyse DHT üzerinden son bir deneme yap.
        if (magnetCandidates.Count == 0 && !string.IsNullOrWhiteSpace(release.InfoHash))
        {
            var displayName = Uri.EscapeDataString(release.Title);
            var fallbackMagnet = $"magnet:?xt=urn:btih:{release.InfoHash}&dn={displayName}";
            using var metadataTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await Torrent.OpenMagnetOrThrowAsync(
                        fallbackMagnet,
                        cancellationToken: metadataTimeout.Token,
                        displayTitle: release.Title)
                    .ConfigureAwait(true);
                return;
            }
            catch (OperationCanceledException) when (metadataTimeout.IsCancellationRequested)
            {
                lastFailure = new TimeoutException(
                    "DHT üzerinden torrent metadata bilgisi alınamadı.");
            }
        }

        throw new InvalidOperationException(
            lastFailure is null
                ? LocalizationManager.Get("Connections.Error.MissingTorrentSource")
                : $"Torrent kaynağı açılamadı. {lastFailure.Message}",
            lastFailure);
    }

    private async Task OpenAddonStreamAsync(CatalogStream stream)
    {
        if (stream.Kind == CatalogStreamKind.Direct &&
            Uri.TryCreate(stream.Url, UriKind.Absolute, out var directUri) &&
            directUri.Scheme is "http" or "https")
        {
            await OpenSourceArgumentAsync(directUri.AbsoluteUri).ConfigureAwait(true);
            return;
        }

        if (stream.Kind != CatalogStreamKind.Torrent)
        {
            throw new InvalidOperationException(LocalizationManager.Get("Addons.Error.UnsupportedStream"));
        }

        string? magnetUri = null;
        if (Uri.TryCreate(stream.Url, UriKind.Absolute, out var streamUri) &&
            streamUri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            magnetUri = streamUri.AbsoluteUri;
        }
        else if (!string.IsNullOrWhiteSpace(stream.InfoHash))
        {
            var displayName = Uri.EscapeDataString(SelectedAddonTitle(stream));
            magnetUri = $"magnet:?xt=urn:btih:{stream.InfoHash}&dn={displayName}";
        }

        SelectedSidebarIndex = 8;
        if (!string.IsNullOrWhiteSpace(magnetUri))
        {
            await Torrent.OpenMagnetOrThrowAsync(
                    magnetUri,
                    stream.FileIndex,
                    displayTitle: SelectedAddonTitle(stream))
                .ConfigureAwait(true);
            return;
        }

        if (Uri.TryCreate(stream.Url, UriKind.Absolute, out var torrentUri) &&
            torrentUri.Scheme is "http" or "https")
        {
            var path = await Addons.DownloadTorrentAsync(stream).ConfigureAwait(true);
            await Torrent.OpenTorrentFileOrThrowAsync(
                    path,
                    stream.FileIndex,
                    displayTitle: SelectedAddonTitle(stream))
                .ConfigureAwait(true);
            return;
        }

        throw new InvalidOperationException(LocalizationManager.Get("Addons.Error.MissingTorrentSource"));
    }

    private string SelectedAddonTitle(CatalogStream stream)
    {
        var title = Addons.SelectedItem?.Title ?? stream.FileName ?? stream.Name;
        var episode = Addons.SelectedEpisode?.DisplayLabel;
        return string.IsNullOrWhiteSpace(episode) ? title : $"{title} {episode}";
    }

    private int GetCurrentPlaylistIndex()
    {
        if (!string.IsNullOrWhiteSpace(Snapshot.Source))
        {
            for (var index = 0; index < Playlist.Count; index++)
            {
                if (string.Equals(Playlist[index].Source, Snapshot.Source, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return SelectedPlaylistItem is null ? -1 : Playlist.IndexOf(SelectedPlaylistItem);
    }

    private static bool IsLoopbackPlaceholderTitle(string source, string title)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !uri.IsLoopback)
        {
            return false;
        }

        var normalized = title.Trim().TrimEnd('/');
        return normalized.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals(uri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static PlaylistItem CreatePlaylistItem(string source) =>
        new(source, CreateDisplayTitle(source), File.Exists(source));

    private static string CreateDisplayTitle(string source)
    {
        if (File.Exists(source))
        {
            return Path.GetFileName(source);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(fileName) ? uri.Host : fileName;
        }

        return source;
    }

    private static IEnumerable<string> ExpandVideoSources(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (File.Exists(path))
            {
                if (VideoExtensions.Contains(Path.GetExtension(path)))
                {
                    yield return Path.GetFullPath(path);
                }

                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(file => VideoExtensions.Contains(Path.GetExtension(file)))
                    .OrderBy(file => file, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private void PersistCurrentMediaAttachmentState()
    {
        var mediaKey = _currentResumeKey ?? _currentPlaybackSource;
        if (string.IsNullOrWhiteSpace(mediaKey) || !File.Exists(mediaKey)) return;

        _settings.MediaAttachmentStates ??= new List<SavedMediaAttachmentState>();
        var state = _settings.MediaAttachmentStates.FirstOrDefault(item =>
            string.Equals(item.MediaSource, mediaKey, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            state = new SavedMediaAttachmentState { MediaSource = mediaKey };
            _settings.MediaAttachmentStates.Add(state);
        }

        CaptureFingerprint(mediaKey, out var mediaLength, out var mediaTicks);
        state.MediaLengthBytes = mediaLength;
        state.MediaLastWriteUtcTicks = mediaTicks;

        if (!string.IsNullOrWhiteSpace(_primarySubtitleOriginalPath) && File.Exists(_primarySubtitleOriginalPath))
        {
            state.PrimarySubtitleSource = _primarySubtitleOriginalPath;
            state.PrimarySubtitlePlaybackPath = !string.IsNullOrWhiteSpace(_subtitleSyncWorkingPath) && File.Exists(_subtitleSyncWorkingPath)
                ? _subtitleSyncWorkingPath
                : _primarySubtitleOriginalPath;
            CaptureFingerprint(_primarySubtitleOriginalPath, out var length, out var ticks);
            state.PrimarySubtitleLengthBytes = length;
            state.PrimarySubtitleLastWriteUtcTicks = ticks;
            state.PrimarySubtitleSyncFinalized = _subtitleSyncFinalized;
        }

        if (!string.IsNullOrWhiteSpace(_secondarySubtitlePath) && File.Exists(_secondarySubtitlePath))
        {
            state.SecondarySubtitleSource = _secondarySubtitlePath;
            CaptureFingerprint(_secondarySubtitlePath, out var length, out var ticks);
            state.SecondarySubtitleLengthBytes = length;
            state.SecondarySubtitleLastWriteUtcTicks = ticks;
        }

        // Rev10.6 intentionally persists only local external audio. Remote URLs may
        // carry expiring tokens/headers and are not written to settings.json.
        if (!string.IsNullOrWhiteSpace(_externalAudioSource) && File.Exists(_externalAudioSource))
        {
            state.ExternalAudioSource = _externalAudioSource;
            state.ExternalAudioPlaybackPath = !string.IsNullOrWhiteSpace(_preparedAudioPath) && File.Exists(_preparedAudioPath)
                ? _preparedAudioPath
                : _externalAudioSource;
            CaptureFingerprint(_externalAudioSource, out var length, out var ticks);
            state.ExternalAudioLengthBytes = length;
            state.ExternalAudioLastWriteUtcTicks = ticks;
            state.AudioSyncFinalized = _audioSyncFinalized;
            state.AudioSyncVerdict = _audioSyncResult?.Verdict.ToString() ?? AudioSyncVerdict.Unavailable.ToString();
            state.AudioSyncModel = _audioSyncResult?.Model.ToString() ?? AudioSyncModelKind.Fixed.ToString();
            state.AudioSyncApplication = _audioSyncApplicationKind.ToString();
            state.AudioBaseDelaySeconds = _audioSyncResult?.BaseDelaySeconds ?? _appliedAudioDelaySeconds;
            state.AudioDriftSecondsPerSecond = _audioSyncResult?.DriftSecondsPerSecond ?? 0;
            state.AudioConfidence = _audioSyncResult?.Confidence ?? 0;
            state.AudioAppliedDelaySeconds = _appliedAudioDelaySeconds;
            state.AudioTempoFactor = _audioSyncTempoFactor;
            state.AudioHybridRecovery = _preparedAudio?.HybridRecovery == true;
            state.AudioHybridMissingRegions = _preparedAudio?.HybridMissingRegions ?? 0;
            state.AudioHybridConfidence = _preparedAudio?.HybridConfidence ?? 0;
            state.AudioHybridCoverage = _preparedAudio?.HybridCoverage ?? 0;
            state.AudioRecoveryKind = _preparedAudio?.RecoveryKind ?? "ast";
            state.AudioTimelineAnchors = _preparedAudio?.TimelineAnchors ?? 0;
            state.AudioVisualAnchors = _preparedAudio?.VisualAnchors ?? 0;
            state.AudioWaveformAnchors = _preparedAudio?.WaveformAnchors ?? 0;
            state.AudioDtwRegions = _preparedAudio?.DtwRegions ?? 0;
            state.AudioVerifiedResidualSeconds = _preparedAudio?.VerifiedResidualSeconds;
            state.AudioVerifiedProbes = _preparedAudio?.VerifiedProbes ?? 0;
            state.AudioProgressiveRegions = _progressiveAudioRegions.Select(region => new SavedAudioProgressiveRegionState
            {
                TargetStartSeconds = region.TargetStartSeconds,
                TargetEndSeconds = region.TargetEndSeconds,
                SourceStartSeconds = region.SourceStartSeconds,
                SourceEndSeconds = region.SourceEndSeconds,
                Rate = region.Rate,
                Confidence = region.Confidence,
                MissingSource = region.MissingSource,
                PlanResidualSeconds = region.PlanResidualSeconds,
                VerifiedResidualSeconds = region.VerifiedResidualSeconds,
                VerifiedProbes = region.VerifiedProbes,
                Trusted = region.Trusted,
            }).ToList();
        }

        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (_settings.MediaAttachmentStates.Count > 100)
        {
            var keep = _settings.MediaAttachmentStates
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(100)
                .ToHashSet();
            _settings.MediaAttachmentStates.RemoveAll(item => !keep.Contains(item));
        }

        _ = SaveSettingsAsync();
    }

    private async Task<bool> RestoreSavedMediaAttachmentsAsync(string mediaKey)
    {
        if (!File.Exists(mediaKey)) return false;
        var state = _settings.MediaAttachmentStates?.FirstOrDefault(item =>
            string.Equals(item.MediaSource, mediaKey, StringComparison.OrdinalIgnoreCase));
        if (state is null || !FingerprintMatches(mediaKey, state.MediaLengthBytes, state.MediaLastWriteUtcTicks)) return false;

        var restoredAnything = false;

        if (!string.IsNullOrWhiteSpace(state.PrimarySubtitleSource) &&
            FingerprintMatches(state.PrimarySubtitleSource, state.PrimarySubtitleLengthBytes, state.PrimarySubtitleLastWriteUtcTicks))
        {
            if (state.PrimarySubtitleSyncFinalized)
            {
                var playbackPath = !string.IsNullOrWhiteSpace(state.PrimarySubtitlePlaybackPath) && File.Exists(state.PrimarySubtitlePlaybackPath)
                    ? state.PrimarySubtitlePlaybackPath
                    : state.PrimarySubtitleSource;
                _primarySubtitleOriginalPath = state.PrimarySubtitleSource;
                _subtitleSyncWorkingPath = playbackPath;
                _subtitleSyncFinalized = true;
                _hasSubtitleSyncSession = true;
                SafePlaybackAction(() => _playback.AddSubtitle(playbackPath));
                SubtitleSyncProgress = 100;
                SubtitleSyncStageText = LocalizationManager.Get("Player.Attachments.Restored.Subtitle");
                SubtitleSyncStatus = SubtitleSyncStageText;
                OnPropertyChanged(nameof(IsSubtitleSyncVisible));
                OnPropertyChanged(nameof(SubtitleSyncProgressSummaryText));
                restoredAnything = true;
            }
            else
            {
                AttachSubtitlePath(state.PrimarySubtitleSource);
                restoredAnything = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(state.SecondarySubtitleSource) &&
            FingerprintMatches(state.SecondarySubtitleSource, state.SecondarySubtitleLengthBytes, state.SecondarySubtitleLastWriteUtcTicks))
        {
            var before = SecondarySubtitleTracks.Where(track => !track.IsDisabled).Select(track => track.Id).ToHashSet();
            SafePlaybackAction(() => _playback.AddSubtitle(state.SecondarySubtitleSource, select: false));
            _secondarySubtitlePath = state.SecondarySubtitleSource;
            await Task.Delay(80).ConfigureAwait(true);
            var added = SecondarySubtitleTracks.LastOrDefault(track => !track.IsDisabled && !before.Contains(track.Id))
                        ?? SecondarySubtitleTracks.LastOrDefault(track => !track.IsDisabled);
            if (added is not null) SelectedSecondarySubtitleTrack = added;
            restoredAnything = true;
        }

        if (!string.IsNullOrWhiteSpace(state.ExternalAudioSource) &&
            FingerprintMatches(state.ExternalAudioSource, state.ExternalAudioLengthBytes, state.ExternalAudioLastWriteUtcTicks))
        {
            var application = Enum.TryParse<AudioSyncApplicationKind>(state.AudioSyncApplication, true, out var savedApplication)
                ? savedApplication
                : AudioSyncApplicationKind.None;

            // Rev10.11 does not trust an old experimental Progressive map or an old
            // Hybrid PreparedTrack cache. Re-run the current Safe Sync Gate instead.
            if (application == AudioSyncApplicationKind.Progressive ||
                (application == AudioSyncApplicationKind.PreparedTrack && state.AudioHybridRecovery))
            {
                state.AudioSyncFinalized = false;
                state.AudioSyncApplication = AudioSyncApplicationKind.None.ToString();
                state.ExternalAudioPlaybackPath = state.ExternalAudioSource;
                state.AudioProgressiveRegions = new List<SavedAudioProgressiveRegionState>();
                application = AudioSyncApplicationKind.None;
            }

            var hasPreparedTrack = !string.IsNullOrWhiteSpace(state.ExternalAudioPlaybackPath) &&
                                   File.Exists(state.ExternalAudioPlaybackPath);
            var playbackPath = application == AudioSyncApplicationKind.PreparedTrack && hasPreparedTrack
                ? state.ExternalAudioPlaybackPath!
                : state.ExternalAudioSource;

            if (application == AudioSyncApplicationKind.PreparedTrack && !hasPreparedTrack)
            {
                // The persistent corrected cache was removed externally. Fall back
                // to the original and let the normal analyzer rebuild it once.
                AttachAudioPath(state.ExternalAudioSource);
            }
            else if (state.AudioSyncFinalized)
            {
                _externalAudioSource = state.ExternalAudioSource;
                _externalAudioHeaders = null;
                _audioSyncFinalized = true;
                _audioSyncApplicationKind = application;
                _audioSyncTempoFactor = Math.Clamp(state.AudioTempoFactor, 0.5, 2.0);
                _manualAudioDelaySeconds = 0;
                _preparedAudioPath = application == AudioSyncApplicationKind.PreparedTrack ? playbackPath : null;
                _progressiveAudioRegions = application == AudioSyncApplicationKind.Progressive
                    ? (state.AudioProgressiveRegions ?? new List<SavedAudioProgressiveRegionState>())
                        .Select(region => new AudioSyncProgressiveRegion(
                            region.TargetStartSeconds, region.TargetEndSeconds,
                            region.SourceStartSeconds, region.SourceEndSeconds,
                            region.Rate, region.Confidence, region.MissingSource,
                            region.PlanResidualSeconds, region.VerifiedResidualSeconds,
                            region.VerifiedProbes, region.Trusted))
                        .ToArray()
                    : Array.Empty<AudioSyncProgressiveRegion>();
                _progressiveAudioRegionIndex = -1;
                _progressiveLastPositionSeconds = double.NaN;
                _preparedAudio = application is AudioSyncApplicationKind.PreparedTrack or AudioSyncApplicationKind.Progressive
                    ? new AudioSyncPreparedAudio(
                        playbackPath,
                        state.AudioVerifiedResidualSeconds,
                        state.AudioVerifiedProbes,
                        null,
                        null,
                        0,
                        LocalizationManager.Get("Player.Attachments.Restored.Audio"),
                        state.AudioHybridRecovery,
                        state.AudioHybridMissingRegions,
                        state.AudioHybridConfidence,
                        state.AudioHybridCoverage,
                        state.AudioRecoveryKind,
                        state.AudioTimelineAnchors,
                        state.AudioVisualAnchors,
                        state.AudioWaveformAnchors,
                        state.AudioDtwRegions,
                        _progressiveAudioRegions)
                    : null;

                if (Enum.TryParse<AudioSyncVerdict>(state.AudioSyncVerdict, true, out var verdict) &&
                    Enum.TryParse<AudioSyncModelKind>(state.AudioSyncModel, true, out var model) &&
                    verdict != AudioSyncVerdict.Unavailable)
                {
                    _audioSyncResult = new AudioSyncResult(
                        verdict,
                        model,
                        state.AudioBaseDelaySeconds,
                        state.AudioDriftSecondsPerSecond,
                        1.0,
                        state.AudioConfidence,
                        Array.Empty<AudioSyncPoint>(),
                        Array.Empty<AudioSyncRegion>(),
                        LocalizationManager.Get("Player.Attachments.Restored.Audio"),
                        "AudioSyncTool cache");
                }
                else
                {
                    _audioSyncResult = null;
                }

                SafePlaybackAction(() => _playback.AddAudio(playbackPath));
                if (application == AudioSyncApplicationKind.Progressive)
                {
                    await Task.Delay(120).ConfigureAwait(true);
                    AudioSyncStatus = "Zorlu kurgu bölgesel senkron haritası geri yüklendi.";
                    UpdateProgressiveAudioSync(Snapshot.PositionSeconds, force: true, userSeek: false);
                    ScheduleLiveProgressiveAudioSync(Snapshot.PositionSeconds, userSeek: true);
                }
                else
                {
                    if (application == AudioSyncApplicationKind.LiveTempo)
                        SetAudioTempoCorrection(_audioSyncTempoFactor);
                    else
                        SetAudioTempoCorrection(1.0);
                    ApplyAudioDelay(state.AudioAppliedDelaySeconds, force: true);
                    AudioSyncStatus = LocalizationManager.Get("Player.Attachments.Restored.Audio");
                }
                NotifyAudioSyncDiagnosticsChanged();
                restoredAnything = true;
            }
            else
            {
                AttachAudioPath(state.ExternalAudioSource);
                restoredAnything = true;
            }
        }

        if (restoredAnything)
        {
            StatusMessage = LocalizationManager.Get("Player.Attachments.Restored.All");
        }
        return restoredAnything;
    }

    private static void CaptureFingerprint(string path, out long? length, out long? ticks)
    {
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            length = null;
            ticks = null;
        }
    }

    private static bool FingerprintMatches(string path, long? length, long? ticks)
    {
        if (!File.Exists(path)) return false;
        if (length is null && ticks is null) return true;
        try
        {
            var info = new FileInfo(path);
            return (length is null || info.Length == length.Value) &&
                   (ticks is null || info.LastWriteTimeUtc.Ticks == ticks.Value);
        }
        catch
        {
            return false;
        }
    }




    private void AddonsOnPortablePreferencesChanged(object? sender, EventArgs e)
    {
        QueueCloudPreferencesSync();
    }

    private void QueueCloudPreferencesSync()
    {
        if (_suppressCloudPreferencesSync ||
            !Account.IsSignedIn ||
            !_cloudAccountService.IsCloudCoreConfigured)
            return;

        _cloudPreferencesSyncCts?.Cancel();
        _cloudPreferencesSyncCts?.Dispose();
        var cts = _cloudPreferencesSyncCts = new CancellationTokenSource();
        _ = DebouncedCloudPreferencesSyncAsync(cts);
    }

    private async Task DebouncedCloudPreferencesSyncAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(1200, cts.Token).ConfigureAwait(true);
            await SaveCloudPreferencesAsync(explicitRequest: false, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_cloudPreferencesSyncCts, cts))
                _cloudPreferencesSyncCts = null;
            cts.Dispose();
        }
    }

    private async Task<CloudUserPreferences> CaptureCloudPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var addons = await Addons
            .GetCloudAddonPreferencesAsync(cancellationToken)
            .ConfigureAwait(true);

        return new CloudUserPreferences(
            _settings.UiLanguage,
            _settings.SubtitleLanguage,
            _settings.AutoPlayNext,
            _settings.AutoSubtitleSync,
            Math.Clamp(_settings.SubtitleSyncMaxOffsetSeconds, 5, 600),
            Math.Clamp(_settings.SeekShortSeconds, 1, 120),
            Math.Clamp(_settings.SeekMediumSeconds, 1, 600),
            Math.Clamp(_settings.SeekLongSeconds, 1, 1800),
            Math.Clamp(_settings.PrimarySubtitleScale, 0.5, 2.0),
            Math.Clamp(_settings.SecondarySubtitleScale, 0.5, 2.0),
            Math.Clamp(_settings.PrimarySubtitlePosition, 0, 150),
            Math.Clamp(_settings.SecondarySubtitlePosition, 0, 150),
            _settings.PreservePrimaryAssStyle,
            _settings.PreserveSecondaryAssStyle,
            Math.Clamp(_settings.TimeDisplayPrecision, 0, 2),
            addons,
            DateTimeOffset.UtcNow,
            HasAddonProfiles: true);
    }

    private async Task SaveCloudPreferencesAsync(
        bool explicitRequest,
        CancellationToken cancellationToken = default)
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured)
        {
            if (explicitRequest)
                CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.RequiresAccount");
            return;
        }

        try
        {
            var snapshot = await CaptureCloudPreferencesAsync(cancellationToken).ConfigureAwait(true);
            var result = await _cloudAccountService
                .SavePreferencesAsync(snapshot, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Success || result.Preferences is null)
            {
                if (explicitRequest)
                    CloudPreferencesStatus = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Preferences.SaveFailed");
                return;
            }

            if (!result.WasApplied)
            {
                await ApplyCloudPreferencesAsync(result.Preferences, cancellationToken).ConfigureAwait(true);
                _cloudPreferencesLastSyncAt = result.Preferences.UpdatedAt;
                CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.NewerPreserved");
                OnPropertyChanged(nameof(CloudPreferencesLastSyncText));
                return;
            }

            _cloudPreferencesLastSyncAt = result.Preferences.UpdatedAt;
            CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.Saved");
            OnPropertyChanged(nameof(CloudPreferencesLastSyncText));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud tercihleri kaydedilemedi: {exception.Message}");
            if (explicitRequest)
                CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.SaveFailed");
        }
    }

    private async Task LoadCloudPreferencesAsync(
        bool explicitRequest,
        CancellationToken cancellationToken = default)
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured)
        {
            if (explicitRequest)
                CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.RequiresAccount");
            return;
        }

        try
        {
            var result = await _cloudAccountService
                .GetPreferencesAsync(cancellationToken)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                if (explicitRequest)
                    CloudPreferencesStatus = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Preferences.LoadFailed");
                return;
            }

            if (result.Preferences is null)
            {
                if (explicitRequest)
                    CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.Empty");
                return;
            }

            await ApplyCloudPreferencesAsync(result.Preferences, cancellationToken).ConfigureAwait(true);
            _cloudPreferencesLastSyncAt = result.Preferences.UpdatedAt;
            CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.Loaded");
            OnPropertyChanged(nameof(CloudPreferencesLastSyncText));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud tercihleri yüklenemedi: {exception.Message}");
            if (explicitRequest)
                CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.LoadFailed");
        }
    }

    private async Task InitializeCloudPreferencesAsync()
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured)
            return;

        var result = await _cloudAccountService.GetPreferencesAsync().ConfigureAwait(true);
        if (!result.Success)
        {
            CloudPreferencesStatus = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Preferences.LoadFailed");
            return;
        }

        if (result.Preferences is null)
        {
            await SaveCloudPreferencesAsync(explicitRequest: false).ConfigureAwait(true);
            return;
        }

        await ApplyCloudPreferencesAsync(result.Preferences).ConfigureAwait(true);

        if (!result.Preferences.HasAddonProfiles)
        {
            var localAddons = await Addons.GetCloudAddonPreferencesAsync().ConfigureAwait(true);
            if (localAddons.Any(item => !string.IsNullOrWhiteSpace(item.ManifestUrl)))
                await SaveCloudPreferencesAsync(explicitRequest: false).ConfigureAwait(true);
        }

        _cloudPreferencesLastSyncAt = result.Preferences.UpdatedAt;
        CloudPreferencesStatus = LocalizationManager.Get("Cloud.Preferences.AutoApplied");
        OnPropertyChanged(nameof(CloudPreferencesLastSyncText));
    }

    private async Task ApplyCloudPreferencesAsync(
        CloudUserPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        _suppressCloudPreferencesSync = true;
        try
        {
            _settings.UiLanguage = string.IsNullOrWhiteSpace(preferences.UiLanguage)
                ? _settings.UiLanguage
                : preferences.UiLanguage;
            _settings.SubtitleLanguage = string.IsNullOrWhiteSpace(preferences.SubtitleLanguage)
                ? _settings.SubtitleLanguage
                : preferences.SubtitleLanguage;
            _settings.AutoPlayNext = preferences.AutoPlayNext;
            _settings.AutoSubtitleSync = preferences.AutoSubtitleSync;
            _settings.SubtitleSyncMaxOffsetSeconds = Math.Clamp(preferences.SubtitleSyncMaxOffsetSeconds, 5, 600);
            _settings.SeekShortSeconds = Math.Clamp(preferences.SeekShortSeconds, 1, 120);
            _settings.SeekMediumSeconds = Math.Clamp(preferences.SeekMediumSeconds, 1, 600);
            _settings.SeekLongSeconds = Math.Clamp(preferences.SeekLongSeconds, 1, 1800);
            _settings.PrimarySubtitleScale = Math.Clamp(preferences.PrimarySubtitleScale, 0.5, 2.0);
            _settings.SecondarySubtitleScale = Math.Clamp(preferences.SecondarySubtitleScale, 0.5, 2.0);
            _settings.PrimarySubtitlePosition = Math.Clamp(preferences.PrimarySubtitlePosition, 0, 150);
            _settings.SecondarySubtitlePosition = Math.Clamp(preferences.SecondarySubtitlePosition, 0, 150);
            _settings.PreservePrimaryAssStyle = preferences.PreservePrimaryAssStyle;
            _settings.PreserveSecondaryAssStyle = preferences.PreserveSecondaryAssStyle;
            _settings.TimeDisplayPrecision = Math.Clamp(preferences.TimeDisplayPrecision, 0, 2);

            LocalizationManager.ApplyLanguage(_settings.UiLanguage);
            _selectedLanguage = LocalizationManager.SupportedLanguages
                .FirstOrDefault(item => item.Code.Equals(_settings.UiLanguage, StringComparison.OrdinalIgnoreCase))
                ?? LocalizationManager.SupportedLanguages[0];

            OnPropertyChanged(nameof(SelectedLanguage));
            OnPropertyChanged(nameof(AutoPlayNext));
            OnPropertyChanged(nameof(AutoSubtitleSyncEnabled));
            OnPropertyChanged(nameof(SeekShortSeconds));
            OnPropertyChanged(nameof(SeekMediumSeconds));
            OnPropertyChanged(nameof(SeekLongSeconds));
            OnPropertyChanged(nameof(SeekBackwardLabel));
            OnPropertyChanged(nameof(SeekForwardLabel));
            OnPropertyChanged(nameof(PrimarySubtitleScale));
            OnPropertyChanged(nameof(SecondarySubtitleScale));
            OnPropertyChanged(nameof(PrimarySubtitlePosition));
            OnPropertyChanged(nameof(SecondarySubtitlePosition));
            OnPropertyChanged(nameof(PreservePrimaryAssStyle));
            OnPropertyChanged(nameof(PreserveSecondaryAssStyle));
            OnPropertyChanged(nameof(TimeDisplayPrecision));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(DurationText));

            if (!_settings.AutoSubtitleSync)
            {
                CancelSubtitleSync();
                SubtitleSyncStatus = LocalizationManager.Get("Player.SubtitleSync.State.Disabled");
            }

            ApplySubtitlePresentationSettings();
            RefreshLocalization();
            await Addons
                .ApplyCloudAddonPreferencesAsync(
                    preferences.Addons,
                    preferences.HasAddonProfiles,
                    cancellationToken)
                .ConfigureAwait(true);
            await _settingsService.SaveAsync(_settings, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _suppressCloudPreferencesSync = false;
        }
    }

    private void QueueCloudPlaylistSync()
    {
        if (_suppressCloudPlaylistSync ||
            !Account.IsSignedIn ||
            !_cloudAccountService.IsCloudCoreConfigured)
            return;

        if (!_cloudPlaylistSessionInitialized)
        {
            _cloudPlaylistSyncQueuedBeforeInitialization = true;
            CloudPlaylistStatus = LocalizationManager.Get("Cloud.Playlist.Initializing");
            return;
        }

        if (_cloudPlaylistHasUnresolvedItems)
        {
            CloudPlaylistStatus = LocalizationManager.Get("Cloud.Playlist.AutoSyncPausedUnresolved");
            return;
        }

        _cloudPlaylistSyncCts?.Cancel();
        _cloudPlaylistSyncCts?.Dispose();
        CloudPlaylistStatus = LocalizationManager.Get("Cloud.Playlist.AutoSyncPending");
        var cts = _cloudPlaylistSyncCts = new CancellationTokenSource();
        _ = DebouncedCloudPlaylistSyncAsync(cts);
    }

    private async Task DebouncedCloudPlaylistSyncAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(850, cts.Token).ConfigureAwait(true);
            await SyncPlaylistToCloudAsync(force: false, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_cloudPlaylistSyncCts, cts))
            {
                _cloudPlaylistSyncCts = null;
            }
            cts.Dispose();
        }
    }

    private CloudLibraryManifestItem BuildCloudPlaylistManifestItem(PlaylistItem item)
    {
        var parseSource = item.IsLocal && File.Exists(item.Source)
            ? Path.GetFileName(item.Source)
            : item.Title;
        var parsed = _releaseParser.Parse(parseSource, item.Title);
        var title = string.IsNullOrWhiteSpace(parsed.Title) ? item.Title.Trim() : parsed.Title.Trim();
        var identity = new MediaIdentity(
            title,
            parsed.ContentType,
            parseSource,
            parsed.Year,
            parsed.Season,
            parsed.Episode,
            parsed.ImdbId,
            parsed.TmdbId);

        return new CloudLibraryManifestItem(
            CloudMediaIdentity.CreateMediaKey(identity, title),
            title,
            identity.ContentType,
            identity.Year,
            identity.Season,
            identity.Episode,
            identity.ImdbId,
            identity.TmdbId,
            item.IsLocal ? "local" : "remote");
    }

    private async Task SyncPlaylistToCloudAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured)
        {
            if (force)
                await SetCloudPlaylistStatusAsync(LocalizationManager.Get("Cloud.Playlist.RequiresAccount")).ConfigureAwait(false);
            return;
        }

        try
        {
            var manifest = Playlist
                .Select(BuildCloudPlaylistManifestItem)
                .GroupBy(item => item.MediaKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            string? currentMediaKey = null;
            if (SelectedPlaylistItem is not null)
                currentMediaKey = BuildCloudPlaylistManifestItem(SelectedPlaylistItem).MediaKey;

            var result = await _cloudAccountService
                .SavePlaylistAsync(manifest, currentMediaKey, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || result.Snapshot is null)
            {
                if (force)
                    await SetCloudPlaylistStatusAsync(result.ErrorMessage ?? LocalizationManager.Get("Cloud.Playlist.SyncFailed")).ConfigureAwait(false);
                return;
            }

            _cloudPlaylistCount = result.Snapshot.Items.Count;
            _cloudPlaylistSessionInitialized = true;
            _cloudPlaylistHasUnresolvedItems = false;
            _cloudPlaylistSyncQueuedBeforeInitialization = false;
            await SetCloudPlaylistStatusAsync(
                LocalizationManager.Format("Cloud.Playlist.Synced", _cloudPlaylistCount),
                notifyCount: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud oynatma listesi eşitlenemedi: {exception.Message}");
            if (force)
                await SetCloudPlaylistStatusAsync(LocalizationManager.Get("Cloud.Playlist.SyncFailed")).ConfigureAwait(false);
        }
    }

    private async Task InitializeCloudPlaylistSessionAsync()
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured)
            return;

        var userId = Account.CurrentUser?.Id;
        if (string.IsNullOrWhiteSpace(userId))
            return;

        await _cloudPlaylistInitializationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_cloudPlaylistSessionInitialized &&
                string.Equals(_cloudPlaylistInitializedUserId, userId, StringComparison.Ordinal))
                return;

            _cloudPlaylistSessionInitialized = false;
            _cloudPlaylistHasUnresolvedItems = false;
            _cloudPlaylistSyncQueuedBeforeInitialization = false;
            _cloudPlaylistInitializedUserId = userId;
            CloudPlaylistStatus = LocalizationManager.Get("Cloud.Playlist.Initializing");

            await LoadCloudPlaylistAsync(
                mergeWithExisting: true,
                automaticInitialization: true).ConfigureAwait(true);

            if (!_cloudPlaylistSessionInitialized)
                _cloudPlaylistSessionInitialized = true;
        }
        finally
        {
            _cloudPlaylistInitializationGate.Release();
        }
    }

    private async Task RefreshCloudPlaylistStatusAsync()
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured) return;

        var result = await _cloudAccountService.GetPlaylistAsync().ConfigureAwait(false);
        if (!result.Success || result.Snapshot is null) return;

        _cloudPlaylistCount = result.Snapshot.Items.Count;
        await SetCloudPlaylistStatusAsync(
            LocalizationManager.Format("Cloud.Playlist.Available", _cloudPlaylistCount),
            notifyCount: true).ConfigureAwait(false);
    }

    private async Task LoadCloudPlaylistAsync(bool mergeWithExisting = false, bool automaticInitialization = false)
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured)
        {
            CloudPlaylistStatus = LocalizationManager.Get("Cloud.Playlist.RequiresAccount");
            return;
        }

        try
        {
            var existingQueue = mergeWithExisting ? Playlist.ToArray() : Array.Empty<PlaylistItem>();
            var existingSelection = SelectedPlaylistItem;
            var result = await _cloudAccountService.GetPlaylistAsync().ConfigureAwait(true);
            if (!result.Success || result.Snapshot is null)
            {
                CloudPlaylistStatus = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Playlist.LoadFailed");
                return;
            }

            var localItems = await _mediaLibrary
                .SearchAsync(null, LibraryMediaFilter.All, 5000)
                .ConfigureAwait(true);

            var localPairs = localItems
                .Select(item =>
                {
                    var parsed = _releaseParser.Parse(item.FileName, item.Title);
                    var title = string.IsNullOrWhiteSpace(parsed.Title) ? item.Title : parsed.Title.Trim();
                    var mediaType = item.MediaType is "movie" or "series" ? item.MediaType : parsed.ContentType;
                    var identity = new MediaIdentity(
                        title,
                        mediaType,
                        item.FileName,
                        parsed.Year ?? item.Year,
                        parsed.Season ?? item.Season,
                        parsed.Episode ?? item.Episode,
                        parsed.ImdbId,
                        parsed.TmdbId);
                    var manifest = new CloudLibraryManifestItem(
                        CloudMediaIdentity.CreateMediaKey(identity, title),
                        title,
                        identity.ContentType,
                        identity.Year,
                        identity.Season,
                        identity.Episode,
                        identity.ImdbId,
                        identity.TmdbId,
                        "local");
                    return (Manifest: manifest, Item: item);
                })
                .ToArray();

            var localByKey = localPairs
                .GroupBy(pair => pair.Manifest.MediaKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Item, StringComparer.OrdinalIgnoreCase);

            var localBySignature = localPairs
                .GroupBy(pair => CloudMediaIdentity.CreateMatchSignature(
                    pair.Manifest.Title,
                    pair.Manifest.MediaType,
                    pair.Manifest.Year,
                    pair.Manifest.Season,
                    pair.Manifest.Episode),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Item, StringComparer.OrdinalIgnoreCase);

            var resolved = new List<(string Key, PlaylistItem Item)>();
            foreach (var cloudItem in result.Snapshot.Items.OrderBy(item => item.Position))
            {
                if (localByKey.TryGetValue(cloudItem.MediaKey, out var local))
                {
                    resolved.Add((cloudItem.MediaKey, CreatePlaylistItem(local.Path)));
                    continue;
                }

                var signature = CloudMediaIdentity.CreateMatchSignature(
                    cloudItem.Title,
                    cloudItem.MediaType,
                    cloudItem.Year,
                    cloudItem.Season,
                    cloudItem.Episode);
                if (localBySignature.TryGetValue(signature, out var fallbackLocal))
                {
                    resolved.Add((cloudItem.MediaKey, CreatePlaylistItem(fallbackLocal.Path)));
                    continue;
                }

                var existing = Playlist.FirstOrDefault(item =>
                {
                    try
                    {
                        return BuildCloudPlaylistManifestItem(item).MediaKey.Equals(
                            cloudItem.MediaKey,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });
                if (existing is not null)
                    resolved.Add((cloudItem.MediaKey, existing));
            }

            if (result.Snapshot.Items.Count > 0 && resolved.Count == 0)
            {
                _cloudPlaylistCount = result.Snapshot.Items.Count;
                _cloudPlaylistHasUnresolvedItems = true;
                if (automaticInitialization)
                    _cloudPlaylistSessionInitialized = true;

                OnPropertyChanged(nameof(CloudPlaylistCountText));
                CloudPlaylistStatus = LocalizationManager.Format(
                    "Cloud.Playlist.NoLocalMatches",
                    result.Snapshot.Items.Count);
                return;
            }

            _cloudPlaylistSyncCts?.Cancel();
            _cloudPlaylistSyncCts?.Dispose();
            _cloudPlaylistSyncCts = null;

            _suppressCloudPlaylistSync = true;
            try
            {
                var merged = new List<PlaylistItem>();
                foreach (var pair in resolved)
                {
                    if (merged.All(item => !string.Equals(item.Source, pair.Item.Source, StringComparison.OrdinalIgnoreCase)))
                        merged.Add(pair.Item);
                }

                if (mergeWithExisting)
                {
                    foreach (var item in existingQueue)
                    {
                        if (merged.All(existing => !string.Equals(existing.Source, item.Source, StringComparison.OrdinalIgnoreCase)))
                            merged.Add(item);
                    }
                }

                Playlist.Clear();
                foreach (var item in merged)
                    Playlist.Add(item);

                var cloudSelected = !string.IsNullOrWhiteSpace(result.Snapshot.CurrentMediaKey)
                    ? resolved.FirstOrDefault(pair => pair.Key.Equals(
                        result.Snapshot.CurrentMediaKey,
                        StringComparison.OrdinalIgnoreCase)).Item
                    : null;

                SelectedPlaylistItem = cloudSelected
                    ?? (existingSelection is not null && Playlist.Contains(existingSelection) ? existingSelection : null)
                    ?? Playlist.FirstOrDefault();

                OnPropertyChanged(nameof(PlaylistSummary));
            }
            finally
            {
                _suppressCloudPlaylistSync = false;
            }

            _cloudPlaylistCount = result.Snapshot.Items.Count;
            _cloudPlaylistHasUnresolvedItems = resolved.Count < result.Snapshot.Items.Count;
            if (automaticInitialization)
                _cloudPlaylistSessionInitialized = true;

            OnPropertyChanged(nameof(CloudPlaylistCountText));
            CloudPlaylistStatus = LocalizationManager.Format(
                "Cloud.Playlist.Loaded",
                resolved.Count,
                result.Snapshot.Items.Count);

            if (automaticInitialization &&
                !_cloudPlaylistHasUnresolvedItems &&
                (_cloudPlaylistSyncQueuedBeforeInitialization || existingQueue.Length > 0))
            {
                _cloudPlaylistSyncQueuedBeforeInitialization = false;
                QueueCloudPlaylistSync();
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud oynatma listesi yüklenemedi: {exception.Message}");
            CloudPlaylistStatus = LocalizationManager.Get("Cloud.Playlist.LoadFailed");
        }
    }

    private async Task SetCloudPlaylistStatusAsync(string value, bool notifyCount = false)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        await dispatcher.InvokeAsync(() =>
        {
            CloudPlaylistStatus = value;
            if (notifyCount) OnPropertyChanged(nameof(CloudPlaylistCountText));
        });
    }

    private async Task ShowCloudListsAsync()
    {
        Addons.SetTargetCollection(null);
        SelectedSidebarIndex = 12;
        await Library.OpenCloudListsAsync().ConfigureAwait(true);
    }

    private async Task OpenDiscoverForSelectedCloudListAsync()
    {
        var target = Library.SelectedCloudCollection;
        if (target is null) return;

        await Addons.RefreshCloudCollectionsAsync().ConfigureAwait(true);

        if (!target.IsSystem)
        {
            var refreshedTarget = Addons.TargetCollections.FirstOrDefault(item => item.Id == target.Id)
                                  ?? target;
            Addons.SetTargetCollection(refreshedTarget);
        }
        else
        {
            Addons.SetTargetCollection(null);
        }

        SelectedSidebarIndex = 1;
    }

    private async void AccountOnCloudSessionReady(object? sender, EventArgs e)
    {
        _ = Library.RefreshCloudListsAsync();
        _ = Addons.RefreshCloudCollectionsAsync();
        _ = Account.RefreshCollectionSummaryAsync();
        await InitializeCloudPlaylistSessionAsync().ConfigureAwait(true);
        _ = InitializeCloudPreferencesAsync();
        _ = InitializeCloudSyncAsync();
        _ = EnrichContinueWatchingArtworkAsync(ContinueWatching.ToArray());
        _ = EnrichHistoryArtworkAsync(HistoryItems.ToArray());
        _ = Library.RefreshArtworkAsync(retryMisses: true);
    }

    private async void CloudCollectionsOnChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!ReferenceEquals(sender, Library))
                await Library.RefreshCloudListsAsync(quiet: true).ConfigureAwait(true);

            if (!ReferenceEquals(sender, Addons))
                await Addons.RefreshCloudCollectionsAsync().ConfigureAwait(true);

            await Account.RefreshCollectionSummaryAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud liste yüzeyleri yenilenemedi: {exception.Message}");
        }
    }

    private async Task InitializeCloudSyncAsync()
    {
        await SeedCloudLibraryAsync().ConfigureAwait(false);
        await SyncCloudProgressAsync(force: true).ConfigureAwait(false);
    }

    private async Task SeedCloudLibraryAsync()
    {
        if (!Account.IsSignedIn || !_cloudAccountService.IsCloudCoreConfigured)
            return;

        try
        {
            var localItems = await _mediaLibrary
                .SearchAsync(null, LibraryMediaFilter.All, 5000)
                .ConfigureAwait(false);

            if (localItems.Count == 0)
                return;

            var manifest = localItems
                .Select(item =>
                {
                    var parsed = _releaseParser.Parse(item.FileName, item.Title);
                    var title = string.IsNullOrWhiteSpace(parsed.Title) ? item.Title : parsed.Title.Trim();
                    var mediaType = item.MediaType is "movie" or "series" ? item.MediaType : parsed.ContentType;
                    var identity = new MediaIdentity(
                        title,
                        mediaType,
                        item.FileName,
                        parsed.Year ?? item.Year,
                        parsed.Season ?? item.Season,
                        parsed.Episode ?? item.Episode,
                        parsed.ImdbId,
                        parsed.TmdbId);
                    return new CloudLibraryManifestItem(
                        CloudMediaIdentity.CreateMediaKey(identity, title),
                        title,
                        identity.ContentType,
                        identity.Year,
                        identity.Season,
                        identity.Episode,
                        identity.ImdbId,
                        identity.TmdbId,
                        "local");
                })
                .GroupBy(item => item.MediaKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            IReadOnlyList<CloudWatchProgressEntry> latest = Array.Empty<CloudWatchProgressEntry>();
            foreach (var batch in manifest.Chunk(100))
            {
                var result = await _cloudAccountService
                    .SyncLibraryAsync(batch)
                    .ConfigureAwait(false);

                if (!result.Success)
                {
                    _logger.Warning($"ADB Cloud kütüphane manifesti ertelendi: {result.ErrorMessage}");
                    return;
                }

                latest = result.Entries;
            }

            if (latest.Count > 0)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null)
                    await dispatcher.InvokeAsync(() => Account.ApplyCloudLibrarySnapshot(latest));
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud kütüphane manifesti hazırlanamadı: {exception.Message}");
        }
    }

    private CloudWatchProgressMutation? BuildCloudWatchProgressMutation()
    {
        var source = _currentResumeKey ?? Snapshot.Source;
        if (string.IsNullOrWhiteSpace(source) || Snapshot.DurationSeconds <= 0)
            return null;

        var recentTitle = _settings.RecentSources
            .FirstOrDefault(item => string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase))
            ?.Title;

        var releaseName = !string.IsNullOrWhiteSpace(recentTitle)
            ? recentTitle
            : File.Exists(source)
                ? Path.GetFileName(source)
                : Snapshot.Title;

        var identity = _releaseParser.Parse(releaseName ?? Snapshot.Title, Snapshot.Title);
        var title = string.IsNullOrWhiteSpace(identity.Title)
            ? CreateDisplayTitle(source)
            : identity.Title.Trim();

        if (string.IsNullOrWhiteSpace(title))
            return null;

        return new CloudWatchProgressMutation(
            CloudMediaIdentity.CreateMediaKey(identity, title),
            source,
            title,
            identity.ContentType,
            identity.Year,
            identity.Season,
            identity.Episode,
            identity.ImdbId,
            identity.TmdbId,
            DetectCloudSourceKind(_currentPlaybackSource ?? source),
            Math.Max(0, Snapshot.PositionSeconds),
            Math.Max(0, Snapshot.DurationSeconds),
            DateTimeOffset.UtcNow);
    }

    private static string DetectCloudSourceKind(string source)
    {
        if (File.Exists(source)) return "local";
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.IsLoopback) return "local-stream";
            if (uri.Scheme is "http" or "https") return "web";
        }

        return "remote";
    }

    private async Task SyncCloudProgressAsync(bool force = false)
    {
        if (_cloudSyncJournal is null ||
            !Account.IsSignedIn ||
            !_cloudAccountService.IsCloudCoreConfigured)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastCloudSyncAttempt < TimeSpan.FromSeconds(20))
            return;

        if (!await _cloudSyncGate.WaitAsync(0).ConfigureAwait(false))
            return;

        _lastCloudSyncAttempt = now;
        try
        {
            var pending = await _cloudSyncJournal
                .GetPendingWatchProgressAsync(100)
                .ConfigureAwait(false);

            var result = pending.Count > 0
                ? await _cloudAccountService.SyncWatchProgressAsync(pending).ConfigureAwait(false)
                : await _cloudAccountService.GetWatchProgressAsync().ConfigureAwait(false);

            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    _logger.Warning($"ADB Cloud v0.3 senkronu ertelendi: {result.ErrorMessage}");
                return;
            }

            if (pending.Count > 0)
            {
                await _cloudSyncJournal
                    .AcknowledgeWatchProgressAsync(pending)
                    .ConfigureAwait(false);
            }

            foreach (var entry in result.Entries)
            {
                var localSource = await _cloudSyncJournal
                    .ResolveLocalSourceAsync(entry)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(localSource)) continue;

                await _cloudSyncJournal
                    .ApplyRemoteWatchProgressAsync(localSource, entry)
                    .ConfigureAwait(false);
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null)
            {
                await dispatcher.InvokeAsync(() => Account.ApplyCloudLibrarySnapshot(result.Entries));
            }

            await RefreshContinueWatchingAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud v0.3 senkronu tamamlanamadı: {exception.Message}");
        }
        finally
        {
            _cloudSyncGate.Release();
        }
    }

    private async Task PersistResumeAsync(bool waitForTurn = false)
    {
        if (!_settings.RememberPlaybackPosition || Snapshot.Source is null)
        {
            return;
        }

        if (waitForTurn)
        {
            await _resumeSaveGate.WaitAsync().ConfigureAwait(false);
        }
        else if (!await _resumeSaveGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _resumeStore.SaveAsync(
                    _currentResumeKey ?? Snapshot.Source,
                    Snapshot.PositionSeconds,
                    Snapshot.DurationSeconds)
                .ConfigureAwait(false);

            if (_cloudSyncJournal is not null)
            {
                var mutation = BuildCloudWatchProgressMutation();
                if (mutation is not null)
                {
                    await _cloudSyncJournal
                        .QueueWatchProgressAsync(mutation)
                        .ConfigureAwait(false);
                    _ = SyncCloudProgressAsync();
                }
            }
        }
        finally
        {
            _resumeSaveGate.Release();
        }
    }

    private Task SaveSettingsAsync() => _settingsService.SaveAsync(_settings);

    private static IReadOnlyList<VideoProfileOption> CreateVideoProfileOptions() =>
    [
        new("auto", "Otomatik", "Dosya/release adına göre güvenli profil seçilir."),
        new("film", "Film / Dizi", "Doğal görünüm; kaynak tipine göre hafif deband."),
        new("anime", "Anime", "Düz gradyanlar için daha güçlü deband; shader ayrı seçilir."),
        new("documentary", "Belgesel", "Arşiv/TV kaynaklarına uygun orta seviye temizlik."),
        new("raw", "Ham", "Shader ve deband kapalı; mpv varsayılan renk eşleme davranışı.")
    ];

    private static IReadOnlyList<VideoEnhancementOption> CreateVideoEnhancementOptions() =>
    [
        new("off", "Kapalı", Array.Empty<string>()),
        new("auto", "Otomatik · profile göre", Array.Empty<string>()),
        new("artcnn-c4f16", "ArtCNN C4F16 · Hafif", new[]
        {
            "artcnn/ArtCNN_C4F16.glsl"
        }),
        new("artcnn-c4f32", "ArtCNN C4F32 · Yüksek kalite", new[]
        {
            "artcnn/ArtCNN_C4F32.glsl"
        }),
        new("anime4k-a", "Anime4K A · 1080p", new[]
        {
            "anime4k/Anime4K_Clamp_Highlights.glsl",
            "anime4k/Anime4K_Restore_CNN_M.glsl",
            "anime4k/Anime4K_Upscale_CNN_x2_M.glsl",
            "anime4k/Anime4K_AutoDownscalePre_x2.glsl",
            "anime4k/Anime4K_AutoDownscalePre_x4.glsl",
            "anime4k/Anime4K_Upscale_CNN_x2_S.glsl"
        }),
        new("anime4k-b", "Anime4K B · 720p", new[]
        {
            "anime4k/Anime4K_Clamp_Highlights.glsl",
            "anime4k/Anime4K_Restore_CNN_Soft_M.glsl",
            "anime4k/Anime4K_Upscale_CNN_x2_M.glsl",
            "anime4k/Anime4K_AutoDownscalePre_x2.glsl",
            "anime4k/Anime4K_AutoDownscalePre_x4.glsl",
            "anime4k/Anime4K_Upscale_CNN_x2_S.glsl"
        }),
        new("anime4k-c", "Anime4K C · düşük çözünürlük", new[]
        {
            "anime4k/Anime4K_Clamp_Highlights.glsl",
            "anime4k/Anime4K_Upscale_Denoise_CNN_x2_M.glsl",
            "anime4k/Anime4K_AutoDownscalePre_x2.glsl",
            "anime4k/Anime4K_AutoDownscalePre_x4.glsl",
            "anime4k/Anime4K_Upscale_CNN_x2_S.glsl"
        })
    ];

    private static IReadOnlyList<PlayerChoiceOption> CreateHdrOutputOptions() =>
    [
        new("auto", "Otomatik", "Windows ve ekran bilgisini kullan; güvenli varsayılan."),
        new("sdr", "SDR ekran", "HDR kaynakları SDR/BT.1886 çıkışına ton eşler."),
        new("hdr", "HDR ekran", "Windows HDR açıkken PQ/HDR çıkışını hedefler.")
    ];

    private static IReadOnlyList<PlayerChoiceOption> CreateToneMappingOptions() =>
    [
        new("auto", "Otomatik", "gpu-next varsayılanı; genel kullanım için önerilir."),
        new("natural", "Doğal renk", "Mobius; renk doğruluğunu ve doğal kontrastı önceler."),
        new("detail", "Parlak detay", "BT.2446A; iyi master edilmiş HDR içerikte detay korumayı önceler.")
    ];

    private static IReadOnlyList<PlayerChoiceOption> CreateContactSheetSizeOptions() =>
    [
        new("4x4", "4 × 4", "16 kare"),
        new("5x5", "5 × 5", "25 kare"),
        new("6x6", "6 × 6", "36 kare")
    ];

    private VideoProcessingSettings ApplyHdrAndToneMappingPreferences(VideoProcessingSettings settings)
    {
        var hdrMode = SelectedHdrOutputMode?.Code ?? _settings.HdrOutputMode ?? "auto";
        var toneMode = SelectedToneMappingMode?.Code ?? _settings.ToneMappingMode ?? "auto";
        var tone = toneMode switch
        {
            "natural" => "mobius",
            "detail" => "bt.2446a",
            _ => "auto"
        };

        return hdrMode switch
        {
            "sdr" => settings with
            {
                ToneMapping = tone,
                TargetColorspaceHint = true,
                TargetColorspaceHintMode = "target",
                TargetTrc = "bt.1886",
                TargetPeak = "203"
            },
            "hdr" => settings with
            {
                ToneMapping = tone,
                TargetColorspaceHint = true,
                TargetColorspaceHintMode = "target",
                TargetTrc = "pq",
                TargetPeak = "auto"
            },
            _ => settings with
            {
                ToneMapping = tone,
                TargetColorspaceHint = true,
                TargetColorspaceHintMode = "target",
                TargetTrc = "auto",
                TargetPeak = "auto"
            }
        };
    }

    private void SetVideoEqualizerSetting(string propertyName, double value)
    {
        var current = propertyName switch
        {
            nameof(VideoBrightness) => _settings.VideoBrightness,
            nameof(VideoContrast) => _settings.VideoContrast,
            nameof(VideoSaturation) => _settings.VideoSaturation,
            nameof(VideoGamma) => _settings.VideoGamma,
            _ => 0
        };
        if (Math.Abs(current - value) < 0.01) return;

        switch (propertyName)
        {
            case nameof(VideoBrightness): _settings.VideoBrightness = value; break;
            case nameof(VideoContrast): _settings.VideoContrast = value; break;
            case nameof(VideoSaturation): _settings.VideoSaturation = value; break;
            case nameof(VideoGamma): _settings.VideoGamma = value; break;
        }

        OnPropertyChanged(propertyName);
        if (_playback.IsInitialized) ApplyVideoEqualizer();
        _ = SaveSettingsAsync();
    }

    private void ApplyVideoEqualizer()
    {
        if (!_playback.IsInitialized) return;
        SafePlaybackAction(() => _playback.SetVideoEqualizer(
            _settings.VideoBrightness,
            _settings.VideoContrast,
            _settings.VideoSaturation,
            _settings.VideoGamma));
    }

    private void ResetVideoEqualizer()
    {
        _settings.VideoBrightness = 0;
        _settings.VideoContrast = 0;
        _settings.VideoSaturation = 0;
        _settings.VideoGamma = 0;
        OnPropertyChanged(nameof(VideoBrightness));
        OnPropertyChanged(nameof(VideoContrast));
        OnPropertyChanged(nameof(VideoSaturation));
        OnPropertyChanged(nameof(VideoGamma));
        ApplyVideoEqualizer();
        _ = SaveSettingsAsync();
        StatusMessage = "Görüntü renk ayarları sıfırlandı.";
    }

    private void RefreshAudioDevices()
    {
        if (!_playback.IsInitialized) return;
        try
        {
            var devices = _playback.GetAudioDevices();
            _suppressAudioDeviceSelection = true;
            AudioDevices.Clear();
            foreach (var device in devices)
            {
                AudioDevices.Add(device.Name.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? new AudioDeviceInfo("auto", LocalizationManager.Get("Player.AudioOutput.Auto"))
                    : device);
            }

            var preferred = AudioDevices.FirstOrDefault(device =>
                device.Name.Equals(_settings.AudioDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? AudioDevices.FirstOrDefault()
                ?? AudioDeviceInfo.Automatic();
            SelectedAudioDevice = preferred;
            _selectedAudioDevice = preferred;
            OnPropertyChanged(nameof(SelectedAudioDevice));
            _suppressAudioDeviceSelection = false;

            SafePlaybackAction(() => _playback.SetAudioDevice(preferred.Name));
            LoadAudioDeviceDelay(preferred.Name);
        }
        catch (Exception exception)
        {
            _suppressAudioDeviceSelection = false;
            _logger.Warning($"Ses aygıtları yenilenemedi: {exception.Message}");
        }
    }

    private void ApplyCurrentAudioDeviceAndDelay()
    {
        if (!_playback.IsInitialized) return;
        var name = SelectedAudioDevice?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = _settings.AudioDeviceName;
        if (string.IsNullOrWhiteSpace(name)) name = "auto";
        SafePlaybackAction(() => _playback.SetAudioDevice(name));
        LoadAudioDeviceDelay(name);
    }

    private void LoadAudioDeviceDelay(string deviceName)
    {
        _settings.AudioDeviceDelays ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        _audioDeviceDelaySeconds = _settings.AudioDeviceDelays.TryGetValue(deviceName, out var delay)
            ? Math.Clamp(delay, -1.0, 1.0)
            : 0;
        OnPropertyChanged(nameof(AudioDeviceDelaySeconds));
        OnPropertyChanged(nameof(AudioDeviceDelayText));
        if (_playback.IsInitialized) ApplyAudioDelay(_appliedAudioDelaySeconds, force: true);
    }

    private void AdjustAudioDeviceDelay(double deltaSeconds) =>
        SetAudioDeviceDelay(_audioDeviceDelaySeconds + deltaSeconds);

    private void SetAudioDeviceDelay(double seconds)
    {
        var bounded = Math.Clamp(seconds, -1.0, 1.0);
        if (Math.Abs(_audioDeviceDelaySeconds - bounded) < 0.0005) return;
        _audioDeviceDelaySeconds = bounded;
        var name = SelectedAudioDevice?.Name ?? _settings.AudioDeviceName ?? "auto";
        _settings.AudioDeviceDelays ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        _settings.AudioDeviceDelays[name] = bounded;
        OnPropertyChanged(nameof(AudioDeviceDelaySeconds));
        OnPropertyChanged(nameof(AudioDeviceDelayText));
        if (_playback.IsInitialized) ApplyAudioDelay(_appliedAudioDelaySeconds, force: true);
        _ = SaveSettingsAsync();
    }

    private async Task CreateContactSheetAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPlaybackSource) || Snapshot.DurationSeconds < 5)
        {
            StatusMessage = "Kare özeti oluşturmak için önce bir video açın.";
            return;
        }

        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "bin", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
        {
            StatusMessage = "Kare özeti oluşturulamadı: FFmpeg bulunamadı.";
            return;
        }

        var columns = Math.Clamp(_settings.ContactSheetColumns, 4, 6);
        var rows = Math.Clamp(_settings.ContactSheetRows, 4, 6);
        var count = columns * rows;
        var duration = Math.Max(5, Snapshot.DurationSeconds);
        var start = duration * 0.04;
        var usable = Math.Max(3, duration * 0.92);
        var fps = (count + 0.5) / usable;
        var safeTitle = Regex.Replace(Path.GetFileNameWithoutExtension(_currentPlaybackSource) ?? "video", @"[^\p{L}\p{N}._-]+", "-");
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "video";
        var output = Path.Combine(
            _paths.ScreenshotDirectory,
            $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}-{columns}x{rows}.png");

        try
        {
            Directory.CreateDirectory(_paths.ScreenshotDirectory);
            StatusMessage = $"{columns}×{rows} kare özeti hazırlanıyor…";
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(start.ToString("0.###", CultureInfo.InvariantCulture));
            if (_currentPlaybackHeaders is { Count: > 0 })
            {
                psi.ArgumentList.Add("-headers");
                psi.ArgumentList.Add(string.Join("\r\n", _currentPlaybackHeaders.Select(pair => $"{pair.Key}: {pair.Value}")) + "\r\n");
            }
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(_currentPlaybackSource);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(usable.ToString("0.###", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"fps={fps.ToString("0.########", CultureInfo.InvariantCulture)},scale=320:-2,tile={columns}x{rows}:padding=4:margin=4");
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add(output);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg başlatılamadı.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(true);
            var stderr = await stderrTask.ConfigureAwait(true);
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"FFmpeg çıkış kodu {process.ExitCode}." : stderr.Trim());
            }

            StatusMessage = $"Kare özeti kaydedildi: {Path.GetFileName(output)}";
        }
        catch (Exception exception)
        {
            _logger.Error("Kare özeti oluşturulamadı.", exception);
            StatusMessage = "Kare özeti oluşturulamadı.";
            _dialogs.ShowError($"Kare özeti oluşturulamadı.\n\n{exception.Message}");
        }
    }

    public bool MatchesShortcut(PlayerShortcutAction action, Key key, ModifierKeys modifiers)
    {
        var configured = action switch
        {
            PlayerShortcutAction.PlayPause => _settings.ShortcutPlayPause,
            PlayerShortcutAction.SeekBackward => _settings.ShortcutSeekBackward,
            PlayerShortcutAction.SeekForward => _settings.ShortcutSeekForward,
            PlayerShortcutAction.SeekBackwardMedium => _settings.ShortcutSeekBackwardMedium,
            PlayerShortcutAction.SeekForwardMedium => _settings.ShortcutSeekForwardMedium,
            PlayerShortcutAction.SeekBackwardLong => _settings.ShortcutSeekBackwardLong,
            PlayerShortcutAction.SeekForwardLong => _settings.ShortcutSeekForwardLong,
            PlayerShortcutAction.Screenshot => _settings.ShortcutScreenshot,
            PlayerShortcutAction.FrameBackward => _settings.ShortcutFrameBackward,
            PlayerShortcutAction.FrameForward => _settings.ShortcutFrameForward,
            PlayerShortcutAction.Fullscreen => _settings.ShortcutFullscreen,
            PlayerShortcutAction.MiniPlayer => _settings.ShortcutMiniPlayer,
            PlayerShortcutAction.Playlist => _settings.ShortcutPlaylist,
            _ => string.Empty
        };
        return TryParseShortcut(configured, out var configuredKey, out var configuredModifiers) &&
               configuredKey == key && configuredModifiers == modifiers;
    }

    private void SetShortcut(string current, string value, string propertyName, Action<string> assign)
    {
        var normalized = NormalizeShortcut(value);
        if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase)) return;
        if (!normalized.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            !TryParseShortcut(normalized, out _, out _))
        {
            StatusMessage = $"Geçersiz kısayol: {value}. Örnek: Ctrl+S, Shift+Left, Space.";
            OnPropertyChanged(propertyName);
            return;
        }
        assign(normalized);
        OnPropertyChanged(propertyName);
        _ = SaveSettingsAsync();
    }

    private static string NormalizeShortcut(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "None";
        var trimmed = value.Trim();
        return trimmed switch
        {
            "," => "OemComma",
            "." => "OemPeriod",
            _ => trimmed.Replace("Control+", "Ctrl+", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool TryParseShortcut(string? value, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win":
                case "windows": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        return Enum.TryParse(parts[^1], true, out key) && key != Key.None;
    }

    private void ResetShortcuts()
    {
        _settings.ShortcutPlayPause = "Space";
        _settings.ShortcutSeekBackward = "Left";
        _settings.ShortcutSeekForward = "Right";
        _settings.ShortcutSeekBackwardMedium = "Shift+Left";
        _settings.ShortcutSeekForwardMedium = "Shift+Right";
        _settings.ShortcutSeekBackwardLong = "Ctrl+Left";
        _settings.ShortcutSeekForwardLong = "Ctrl+Right";
        _settings.ShortcutScreenshot = "Ctrl+S";
        _settings.ShortcutFrameBackward = "OemComma";
        _settings.ShortcutFrameForward = "OemPeriod";
        _settings.ShortcutFullscreen = "F";
        _settings.ShortcutMiniPlayer = "M";
        _settings.ShortcutPlaylist = "P";
        foreach (var property in new[]
        {
            nameof(ShortcutPlayPause), nameof(ShortcutSeekBackward), nameof(ShortcutSeekForward),
            nameof(ShortcutSeekBackwardMedium), nameof(ShortcutSeekForwardMedium),
            nameof(ShortcutSeekBackwardLong), nameof(ShortcutSeekForwardLong), nameof(ShortcutScreenshot),
            nameof(ShortcutFrameBackward), nameof(ShortcutFrameForward), nameof(ShortcutFullscreen),
            nameof(ShortcutMiniPlayer), nameof(ShortcutPlaylist)
        }) OnPropertyChanged(property);
        _ = SaveSettingsAsync();
        StatusMessage = "Klavye kısayolları varsayılanlara döndürüldü.";
    }

    private void ApplyVideoProfileForCurrentSource()
    {
        if (!IsReady && _videoWindowHandle == 0)
        {
            return;
        }

        try
        {
            var selected = SelectedVideoProfile ?? VideoProfileOptions[0];
            var detection = DetectVideoProfile(_currentPlaybackSource, selected.Code);
            _effectiveVideoProfileCode = detection.ProfileCode;

            var processing = ApplyHdrAndToneMappingPreferences(
                BuildVideoProcessingSettings(detection.ProfileCode, detection.SourceKind));
            _playback.ApplyVideoProcessingSettings(processing);

            var profileLabel = VideoProfileOptions.FirstOrDefault(option =>
                option.Code.Equals(detection.ProfileCode, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? detection.ProfileCode;
            var sourceLabel = detection.SourceKind switch
            {
                "remux" => "REMUX / BluRay",
                "web" => "WEB / streaming",
                _ => "kaynak türü bilinmiyor"
            };

            VideoProfileSummary = selected.Code.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? $"Otomatik: {profileLabel} · {sourceLabel}{(string.IsNullOrWhiteSpace(detection.Reason) ? string.Empty : $" · {detection.Reason}")}"
                : $"{profileLabel} · elle seçildi · {sourceLabel}";

            ApplyVideoEnhancement(SelectedVideoEnhancement ?? VideoEnhancementOptions[0], announce: false);
        }
        catch (Exception exception)
        {
            _logger.Error("Görüntü profili uygulanamadı.", exception);
            VideoProfileSummary = "Görüntü profili uygulanamadı; güvenli varsayılanlar kullanılıyor.";
        }
    }

    private static VideoProfileDetection DetectVideoProfile(string? source, string requestedMode)
    {
        var hay = NormalizeProfileHaystack(source);
        var sourceKind = DetectSourceKind(hay);

        if (!requestedMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var manual = requestedMode is "film" or "anime" or "documentary" or "raw"
                ? requestedMode
                : "film";
            return new VideoProfileDetection(manual, sourceKind, "elle seçildi");
        }

        var animeReason = FirstProfileKeyword(hay, AnimeProfileKeywords);
        if (animeReason is not null)
        {
            return new VideoProfileDetection("anime", sourceKind, $"anime işareti: {animeReason}");
        }

        var docReason = FirstProfileKeyword(hay, DocumentaryProfileKeywords);
        if (docReason is not null)
        {
            return new VideoProfileDetection("documentary", sourceKind, $"belgesel işareti: {docReason}");
        }

        return new VideoProfileDetection("film", sourceKind, "güvenli varsayılan");
    }

    private static string DetectSourceKind(string hay)
    {
        if (FirstProfileKeyword(hay, RemuxSourceKeywords) is not null) return "remux";
        if (FirstProfileKeyword(hay, WebSourceKeywords) is not null) return "web";
        return "unknown";
    }

    private static string NormalizeProfileHaystack(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return " ";
        }

        var candidate = source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            candidate = Uri.UnescapeDataString(uri.AbsolutePath);
        }

        candidate = candidate.ToLowerInvariant();
        candidate = Regex.Replace(candidate, @"[\\/_\.\-\[\]\(\)]+", " ");
        candidate = Regex.Replace(candidate, @"\s+", " ").Trim();
        return $" {candidate} ";
    }

    private static string? FirstProfileKeyword(string hay, IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            var normalized = Regex.Replace(keyword.ToLowerInvariant(), @"[\\/_\.\-\[\]\(\)]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            if (normalized.Length > 0 && hay.Contains($" {normalized} ", StringComparison.Ordinal))
            {
                return keyword;
            }
        }

        return null;
    }

    private static VideoProcessingSettings BuildVideoProcessingSettings(string profileCode, string sourceKind)
    {
        if (profileCode == "raw")
        {
            return new VideoProcessingSettings(
                "raw",
                DebandEnabled: false,
                DebandIterations: 1,
                DebandThreshold: 24,
                DebandRange: 12,
                DebandGrain: 0,
                ToneMapping: "auto",
                GamutMappingMode: "auto");
        }

        if (profileCode == "anime")
        {
            var remux = sourceKind == "remux";
            return new VideoProcessingSettings(
                remux ? "anime-remux" : "anime",
                DebandEnabled: !remux,
                DebandIterations: 4,
                DebandThreshold: 48,
                DebandRange: 16,
                DebandGrain: 24,
                ToneMapping: "spline",
                GamutMappingMode: "perceptual");
        }

        if (profileCode == "documentary")
        {
            if (sourceKind == "remux")
            {
                return new VideoProcessingSettings(
                    "documentary-remux",
                    DebandEnabled: false,
                    DebandIterations: 1,
                    DebandThreshold: 24,
                    DebandRange: 12,
                    DebandGrain: 0,
                    ToneMapping: "spline",
                    GamutMappingMode: "perceptual");
            }

            var isWeb = sourceKind == "web";
            return new VideoProcessingSettings(
                isWeb ? "documentary-web" : "documentary",
                DebandEnabled: true,
                DebandIterations: 2,
                DebandThreshold: isWeb ? 36 : 40,
                DebandRange: isWeb ? 14 : 16,
                DebandGrain: isWeb ? 8 : 12,
                ToneMapping: "spline",
                GamutMappingMode: "perceptual");
        }

        // Film / dizi: REMUX'ta grain'i koru, WEB'de banding'i nazikçe temizle.
        if (sourceKind == "remux")
        {
            return new VideoProcessingSettings(
                "film-remux",
                DebandEnabled: false,
                DebandIterations: 1,
                DebandThreshold: 24,
                DebandRange: 12,
                DebandGrain: 0,
                ToneMapping: "spline",
                GamutMappingMode: "perceptual");
        }

        if (sourceKind == "web")
        {
            return new VideoProcessingSettings(
                "film-web",
                DebandEnabled: true,
                DebandIterations: 2,
                DebandThreshold: 36,
                DebandRange: 14,
                DebandGrain: 8,
                ToneMapping: "spline",
                GamutMappingMode: "perceptual");
        }

        return new VideoProcessingSettings(
            "film",
            DebandEnabled: true,
            DebandIterations: 1,
            DebandThreshold: 24,
            DebandRange: 12,
            DebandGrain: 0,
            ToneMapping: "spline",
            GamutMappingMode: "perceptual");
    }

    private void ApplyVideoEnhancement(VideoEnhancementOption option, bool announce = true)
    {
        try
        {
            var resolved = option;

            if (_effectiveVideoProfileCode == "raw")
            {
                resolved = VideoEnhancementOptions.First(item => item.Code == "off");
            }
            else if (option.Code == "auto")
            {
                resolved = _effectiveVideoProfileCode == "anime"
                    ? VideoEnhancementOptions.First(item => item.Code == "artcnn-c4f16")
                    : VideoEnhancementOptions.First(item => item.Code == "off");
            }

            var root = Path.Combine(AppContext.BaseDirectory, "native");
            var paths = resolved.ShaderFiles.Select(file => Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))).ToArray();
            _playback.SetShaderChain(paths);

            if (announce)
            {
                StatusMessage = resolved.Code == "off"
                    ? option.Code == "auto"
                        ? "Otomatik görüntü iyileştirme: bu profil için shader gerekmiyor."
                        : "Görüntü iyileştirme kapalı."
                    : option.Code == "auto"
                        ? $"Otomatik görüntü iyileştirme: {resolved.DisplayName} etkin."
                        : $"{resolved.DisplayName} etkin.";
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Görüntü iyileştirmesi uygulanamadı.", exception);
            StatusMessage = "Görüntü iyileştirme dosyaları hazır değil veya ekran kartı bu modu çalıştıramadı.";
            try { _playback.SetShaderChain(Array.Empty<string>()); } catch { }
        }
    }

    private sealed record VideoProfileDetection(
        string ProfileCode,
        string SourceKind,
        string Reason);

    private void OpenFolder(string path, string errorMessage)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logger.Error(errorMessage, exception);
        }
    }

    private void SafePlaybackAction(Action action)
    {
        try
        {
            if (IsReady)
            {
                action();
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Oynatıcı komutu uygulanamadı.", exception);
            StatusMessage = exception.Message;
        }
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "altyazidb-player" : safe;
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatDisplayTime(double seconds, int precision)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return Math.Clamp(precision, 0, 2) switch
        {
            1 => time.ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture),
            2 => time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
            _ => time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture),
        };
    }


    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        Library.RefreshLocalization();
        Sources.RefreshLocalization();
        Telegram.RefreshLocalization();
        Torrent.RefreshLocalization();
        Connections.RefreshLocalization();
        Addons.RefreshLocalization();
        Subtitles.RefreshLocalization();
        Updates.RefreshLocalization();
        ApiSettings.RefreshLocalization();
        Account.RefreshLocalization();
        OnPropertyChanged(nameof(CloudPlaylistCountText));
        OnPropertyChanged(nameof(CloudPreferencesLastSyncText));
        NotifyAudioSyncDiagnosticsChanged();
        OnPropertyChanged(nameof(SubtitleSyncProgressSummaryText));
        OnPropertyChanged(nameof(SubtitleSyncStageText));
        OnPropertyChanged(nameof(SubtitleSyncStatus));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAudioSyncAnalysis();
        CancelSubtitleSync();
        PersistCurrentMediaAttachmentState();
        await PersistResumeAsync(waitForTurn: true).ConfigureAwait(false);
        await SaveSettingsAsync().ConfigureAwait(false);

        Account.CloudSessionReady -= AccountOnCloudSessionReady;
        Library.CloudCollectionsChanged -= CloudCollectionsOnChanged;
        Addons.CloudCollectionsChanged -= CloudCollectionsOnChanged;
        Addons.PortablePreferencesChanged -= AddonsOnPortablePreferencesChanged;
        _playback.SnapshotChanged -= PlaybackOnSnapshotChanged;
        _playback.TracksChanged -= PlaybackOnTracksChanged;
        _playback.ChaptersChanged -= PlaybackOnChaptersChanged;
        _playback.PlaybackEnded -= PlaybackOnPlaybackEnded;
        _playback.ErrorOccurred -= PlaybackOnErrorOccurred;
        await Library.DisposeAsync().ConfigureAwait(false);
        Subtitles.Dispose();
        await Sources.DisposeAsync().ConfigureAwait(false);
        await Telegram.DisposeAsync().ConfigureAwait(false);
        await Torrent.DisposeAsync().ConfigureAwait(false);
        Connections.Dispose();
        Addons.Dispose();
        ApiSettings.Dispose();
        await _playback.DisposeAsync().ConfigureAwait(false);
        _playbackInitGate.Dispose();
        _resumeSaveGate.Dispose();
        _cloudPlaylistSyncCts?.Cancel();
        _cloudPlaylistSyncCts?.Dispose();
        _cloudPreferencesSyncCts?.Cancel();
        _cloudPreferencesSyncCts?.Dispose();
        _continueWatchingArtworkCts?.Cancel();
        _continueWatchingArtworkCts?.Dispose();
        _historyArtworkCts?.Cancel();
        _historyArtworkCts?.Dispose();
        _mediaArtwork.Dispose();
        _cloudSyncGate.Dispose();
    }
    private sealed record ContinueWatchingArtworkWork(
        ContinueWatchingItem Item,
        MediaIdentity Identity,
        string CacheKey);

    private sealed record HistoryArtworkWork(
        HistoryItem Item,
        MediaIdentity Identity,
        string CacheKey);

}
