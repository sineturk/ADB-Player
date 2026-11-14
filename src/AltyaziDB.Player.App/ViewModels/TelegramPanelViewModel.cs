using System.IO;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;
using AltyaziDB.Player.Telegram;
using AltyaziDB.Player.Telegram.Generated;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class TelegramPanelViewModel : ObservableObject, IAsyncDisposable
{
    private const string DatabaseKeySecretKey = "telegram:database_key";
    private const string ApiHashSecretKey = ApiSettingsPanelViewModel.TelegramApiHashSecretKey;
    private readonly ITelegramService _service;
    private readonly ISecretStore _secrets;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly Func<RemoteOpenRequest, Task> _openSource;
    private readonly Func<RemoteOpenRequest, Task> _attachAudio;
    private readonly Action<string> _attachLocalAudio;
    private readonly Action<string> _attachSubtitle;
    private readonly Func<bool> _hasPlayableMedia;
    private readonly Func<string?> _currentEpisodeHint;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly Func<bool> _canMaintainCache;
    private static readonly IReadOnlyList<int> CacheQuotas = [5, 10, 20, 50, 100];
    private TelegramCacheUsage? _cacheUsage;
    private string _cacheStatusKey = "Settings.TelegramCache.Status.Unread";
    private object?[] _cacheStatusArguments = [];
    private bool _cacheIsBusy;
    private DateTimeOffset _lastAutomaticCacheCleanupUtc = DateTimeOffset.MinValue;
    private readonly Task _loadSecretsTask;
    private string _databaseEncryptionKey = string.Empty;
    private int _telegramApiId;
    private string _telegramApiHash = string.Empty;
    private string _phoneNumber = string.Empty;
    private string _emailAddress = string.Empty;
    private string _code = string.Empty;
    private string _password = string.Empty;
    private string _firstName = string.Empty;
    private string _lastName = string.Empty;
    private string _searchText = string.Empty;
    private string _mediaFilter = "all";
    private CancellationTokenSource? _prefetchCancellation;
    private string _chatFilterText = string.Empty;
    private string _chatListFilter = "all";
    private readonly Dictionary<(long ChatId, long MessageId), TelegramMediaItem> _recentMediaById = new();
    private readonly Dictionary<long, TelegramChatItem> _knownChats = new();
    private DateTimeOffset _favoriteFeedLastRefreshUtc = DateTimeOffset.MinValue;
    private bool _favoriteFeedIsLoading;
    private string _statusText = "Telegram hazırlanıyor…";
    private TelegramAuthorizationSnapshot _authorization = TelegramAuthorizationSnapshot.NotStarted;
    private long? _currentAccountId;
    private int _accountIdentityGeneration;
    private TelegramChatItem? _selectedChat;
    private TelegramChatFolderItem? _selectedFolder;
    private TelegramForumTopicItem? _selectedTopic;
    private TelegramMediaItem? _selectedMedia;
    private bool _isBusy;
    private bool _submitPhoneWhenRequested;
    private bool _phoneSubmissionInProgress;
    private bool _phoneSubmissionPending;
    private bool _serviceStarted;
    private bool _suppressFolderReload;
    private bool _suppressChatReload;
    private bool _suppressTopicReload;
    private double _downloadPercent;
    private TelegramMediaItem? _activeTransfer;
    private bool _transferStopped;
    private bool _isAudioSelectionMode;
    private int _mediaResultLimit = 300;
    private string _activeChatQuery = string.Empty;
    private bool _canLoadMoreMedia;
    private readonly HashSet<(long ChatId, long MessageId)> _globalSearchMediaIds = new();

    public TelegramPanelViewModel(
        ITelegramService service,
        ISecretStore secrets,
        PlayerSettings settings,
        Func<Task> saveSettings,
        Func<RemoteOpenRequest, Task> openSource,
        Func<RemoteOpenRequest, Task> attachAudio,
        Action<string> attachLocalAudio,
        Action<string> attachSubtitle,
        Func<bool> hasPlayableMedia,
        Func<string?> currentEpisodeHint,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths,
        Func<bool>? canMaintainCache = null)
    {
        _service = service;
        _secrets = secrets;
        _settings = settings;
        _saveSettings = saveSettings;
        _openSource = openSource;
        _attachAudio = attachAudio;
        _attachLocalAudio = attachLocalAudio;
        _attachSubtitle = attachSubtitle;
        _hasPlayableMedia = hasPlayableMedia;
        _currentEpisodeHint = currentEpisodeHint;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
        _canMaintainCache = canMaintainCache ?? (() => true);
        if (!CacheQuotas.Contains(settings.TelegramCacheLimitGiB))
            settings.TelegramCacheLimitGiB = 10;
        _phoneNumber = settings.TelegramPhoneNumber;
        Chats = new ObservableCollection<TelegramChatItem>();
        FilteredChats = new ObservableCollection<TelegramChatItem>();
        FavoriteChats = new ObservableCollection<TelegramChatItem>();
        FavoriteRecentMedia = new ObservableCollection<TelegramMediaItem>();
        RecentMedia = new ObservableCollection<TelegramMediaItem>();
        ChatFolders = new ObservableCollection<TelegramChatFolderItem>();
        ForumTopics = new ObservableCollection<TelegramForumTopicItem>();
        Media = new ObservableCollection<TelegramMediaItem>();
        FilteredMedia = new ObservableCollection<TelegramMediaItem>();
        StartCommand = new AsyncRelayCommand(StartAsync);
        SubmitAuthorizationCommand = new AsyncRelayCommand(SubmitAuthorizationAsync);
        LoadChatsCommand = new AsyncRelayCommand(LoadChatsAsync);
        ClearChatFilterCommand = new RelayCommand(() => ChatFilterText = string.Empty);
        LoadMediaCommand = new AsyncRelayCommand(LoadMediaAsync);
        LoadMoreMediaCommand = new AsyncRelayCommand(LoadMoreMediaAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        GlobalSearchCommand = new AsyncRelayCommand(GlobalSearchAsync);
        ShowAllTopicsCommand = new AsyncRelayCommand(ShowAllTopicsAsync);
        DownloadAndOpenCommand = new AsyncRelayCommand(() => DownloadAndOpenAsync());
        CacheSelectedMediaCommand = new AsyncRelayCommand(CacheSelectedMediaAsync);
        StopTransferCommand = new AsyncRelayCommand(StopTransferAsync);
        ResumeTransferCommand = new RelayCommand(ResumeTransfer);
        AttachSelectedSubtitleCommand = new AsyncRelayCommand(() => DownloadAndOpenAsync(subtitleOnly: true));
        RepairArchivePartsCommand = new AsyncRelayCommand(RepairArchivePartsAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        OpenTelegramFolderCommand = new RelayCommand(() => OpenFolder(ActiveCacheDirectory));
        RefreshCacheUsageCommand = new AsyncRelayCommand(RefreshCacheUsageAsync);
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync);
        OpenCacheDirectoryCommand = new RelayCommand(() => OpenFolder(ActiveCacheDirectory));
        _service.AuthorizationChanged += OnAuthorizationChanged;
        _service.DownloadProgressChanged += OnDownloadProgressChanged;
        _service.ChatFoldersChanged += OnChatFoldersChanged;
        _logger.Info($"TDLib durumu: {_service.NativeDiagnostic}");
        StatusText = AvailabilityText;
        _loadSecretsTask = LoadSecretAsync();
        _ = RestoreSavedSessionAsync();
    }

    public ObservableCollection<TelegramChatItem> Chats { get; }
    public ObservableCollection<TelegramChatItem> FilteredChats { get; }
    public ObservableCollection<TelegramChatItem> FavoriteChats { get; }
    public ObservableCollection<TelegramMediaItem> FavoriteRecentMedia { get; }
    public bool FavoriteFeedIsLoading
    {
        get => _favoriteFeedIsLoading;
        private set => SetProperty(ref _favoriteFeedIsLoading, value);
    }
    public ObservableCollection<TelegramMediaItem> RecentMedia { get; }
    public ICommand ClearChatFilterCommand { get; }
    // Four exclusive Telegram Desktop-style pills; keep filtering local and
    // do not issue TDLib history requests while typing.
    public string ChatListFilter
    {
        get => _chatListFilter;
        set
        {
            if (value is not ("all" or "starred" or "channels" or "groups" or "private") ||
                !SetProperty(ref _chatListFilter, value)) return;
            OnPropertyChanged(nameof(IsAllFilter));
            OnPropertyChanged(nameof(IsStarredFilter));
            OnPropertyChanged(nameof(IsChannelsFilter));
            OnPropertyChanged(nameof(IsGroupsFilter));
            OnPropertyChanged(nameof(IsPrivateFilter));
            OnPropertyChanged(nameof(FavoritesOnly));
            RefreshChatLists();
        }
    }
    public bool IsAllFilter
    {
        get => ChatListFilter == "all";
        set { if (value) ChatListFilter = "all"; }
    }
    public bool IsStarredFilter
    {
        get => ChatListFilter == "starred";
        set { if (value) ChatListFilter = "starred"; }
    }
    public bool IsChannelsFilter
    {
        get => ChatListFilter == "channels";
        set { if (value) ChatListFilter = "channels"; }
    }
    public bool IsGroupsFilter
    {
        get => ChatListFilter == "groups";
        set { if (value) ChatListFilter = "groups"; }
    }
    public bool IsPrivateFilter
    {
        get => ChatListFilter == "private";
        set { if (value) ChatListFilter = "private"; }
    }
    public string FilteredChatCountText =>
        LocalizationManager.Format("Telegram.ChatList.ResultCount", FilteredChats.Count);
    public string ChatFilterText
    {
        get => _chatFilterText;
        set
        {
            if (SetProperty(ref _chatFilterText, value)) RefreshChatLists();
        }
    }
    // Kept as a compatibility alias for the earlier starred-only checkbox.
    public bool FavoritesOnly
    {
        get => IsStarredFilter;
        set => ChatListFilter = value ? "starred" : "all";
    }
    public bool IsFavoriteChat(long chatId) => _settings.TelegramFavoriteChatIds?.Contains(chatId) == true;
    public async Task ToggleFavoriteChatAsync(TelegramChatItem chat)
    {
        var favorites = _settings.TelegramFavoriteChatIds ??= new List<long>();
        if (!favorites.Remove(chat.Id)) favorites.Add(chat.Id);
        // The clicked row may differ from a cached chat instance in a custom folder.
        // Update it before persisting so the star immediately reflects the new state.
        chat.IsFavorite = favorites.Contains(chat.Id);
        // Force a fresh feed next time Hub opens, without scanning Telegram now.
        _favoriteFeedLastRefreshUtc = DateTimeOffset.MinValue;
        FavoriteRecentMedia.Clear();
        RefreshChatLists();
        await _saveSettings().ConfigureAwait(true);
    }
    // Fetch only when Hub is shown or explicitly refreshed: no background
    // monitoring, no downloads, and no changes to current chat or playback.
    public async Task RefreshFavoriteMediaAsync(bool force = false)
    {
        if (!IsReady)
        {
            FavoriteRecentMedia.Clear();
            return;
        }
        if (FavoriteFeedIsLoading ||
            (!force && DateTimeOffset.UtcNow - _favoriteFeedLastRefreshUtc < TimeSpan.FromMinutes(3)))
            return;

        var generation = _accountIdentityGeneration;
        var starred = FavoriteChats.Where(chat => chat.IsChannel || chat.IsGroup)
            .Take(8).ToArray();
        FavoriteFeedIsLoading = true;
        try
        {
            var discovered = new List<TelegramMediaItem>();
            foreach (var chat in starred)
            {
                if (!IsReady || generation != _accountIdentityGeneration) return;
                try
                {
                    // Each TDLib media type is internally paged. Keep the per-chat
                    // budget small to avoid heavy group/topic history enumeration.
                    var media = await _service.GetChatMediaAsync(chat.Id, limit: 12)
                        .ConfigureAwait(true);
                    discovered.AddRange(media);
                }
                catch (Exception exception)
                {
                    _logger.Warning($"Telegram favori içerik listesi okunamadı: {chat.Id}: {exception.Message}");
                }
            }

            if (!IsReady || generation != _accountIdentityGeneration) return;
            // Revalidate after awaits: an unstarred channel or another account
            // must not leak into Hub even if its request finished late.
            var eligible = FavoriteChats.Where(chat => chat.IsChannel || chat.IsGroup)
                .Select(chat => chat.Id).ToArray();
            var latest = TelegramFavoriteFeedPolicy.Latest(discovered, eligible);
            FavoriteRecentMedia.Clear();
            foreach (var item in latest) FavoriteRecentMedia.Add(item);
            _favoriteFeedLastRefreshUtc = DateTimeOffset.UtcNow;
        }
        finally { FavoriteFeedIsLoading = false; }
    }

    public async Task OpenRecentMediaAsync(TelegramMediaItem media)
    {
        SelectedMedia = media;
        await DownloadAndOpenAsync().ConfigureAwait(true);
    }

    public async Task OpenFavoriteChatAsync(TelegramChatItem chat)
    {
        if (!IsReady) return;
        if (!Chats.Any(item => item.Id == chat.Id))
        {
            // Favorites are global, independently of the currently selected folder.
            try
            {
                _suppressFolderReload = true;
                SelectedFolder = ChatFolders.FirstOrDefault(item => item.Id is null);
            }
            finally { _suppressFolderReload = false; }
            await LoadChatsAsync().ConfigureAwait(true);
        }
        SelectedChat = Chats.FirstOrDefault(item => item.Id == chat.Id);
    }
    public string ChatFavoriteGlyph(long chatId) => IsFavoriteChat(chatId) ? "★" : "☆";
    private void RefreshChatLists()
    {
        var favorites = (_settings.TelegramFavoriteChatIds ?? new List<long>()).ToHashSet();
        var query = (ChatFilterText ?? string.Empty).Trim();
        foreach (var chat in _knownChats.Values) chat.IsFavorite = favorites.Contains(chat.Id);
        // A custom folder's visible rows can be distinct objects from the main-list
        // cache. Keep both instances in sync so the yellow filled star is reliable.
        foreach (var chat in Chats) chat.IsFavorite = favorites.Contains(chat.Id);
        var wanted = Chats.Where(item =>
                (_chatListFilter switch
                {
                    "starred" => favorites.Contains(item.Id),
                    "channels" => item.IsChannel,
                    "groups" => item.IsGroup,
                    "private" => item.IsPrivate,
                    _ => true
                }) &&
                (query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .ToArray();
        // Reconcile in place instead of clearing the whole bound collection:
        // a full Clear() would briefly reset SelectedChat, reload the wrong
        // topic, and interrupt playback when someone stars a channel.
        var wantedIds = wanted.Select(item => item.Id).ToHashSet();
        for (var i = FilteredChats.Count - 1; i >= 0; --i)
            if (!wantedIds.Contains(FilteredChats[i].Id)) FilteredChats.RemoveAt(i);
        for (var i = 0; i < wanted.Length; ++i)
        {
            if (i < FilteredChats.Count && FilteredChats[i].Id == wanted[i].Id) continue;
            var oldIndex = FilteredChats.ToList().FindIndex(item => item.Id == wanted[i].Id);
            if (oldIndex >= 0) FilteredChats.Move(oldIndex, i);
            else FilteredChats.Insert(i, wanted[i]);
        }
        FavoriteChats.Clear();
        foreach (var id in _settings.TelegramFavoriteChatIds ?? new List<long>())
            if (_knownChats.TryGetValue(id, out var chat)) FavoriteChats.Add(chat);
        OnPropertyChanged(nameof(FavoritesSummary));
        OnPropertyChanged(nameof(FilteredChatCountText));
    }
    public string FavoritesSummary => LocalizationManager.Format("Telegram.Hub.FavoritesCount", FavoriteChats.Count);
    private void RememberRecentMedia(IEnumerable<TelegramMediaItem> items)
    {
        foreach (var item in items)
        {
            if (item.Kind == TelegramMediaKind.Document && string.IsNullOrWhiteSpace(item.FileName)) continue;
            _recentMediaById[(item.ChatId, item.MessageId)] = item;
        }
        // Entries from chats visited in this session; avoid claiming a full
        // global index when Telegram history wasn't enumerated.
        var latest = _recentMediaById.Values.OrderByDescending(x => x.Date)
            .ThenByDescending(x => x.MessageId).Take(30).ToArray();
        RecentMedia.Clear();
        foreach (var item in latest) RecentMedia.Add(item);
    }
    public ObservableCollection<TelegramChatFolderItem> ChatFolders { get; }
    public ObservableCollection<TelegramForumTopicItem> ForumTopics { get; }
    public ObservableCollection<TelegramMediaItem> Media { get; }
    public ObservableCollection<TelegramMediaItem> FilteredMedia { get; }
    public ICommand CacheSelectedMediaCommand { get; }
    public string MediaFilter
    {
        get => _mediaFilter;
        set
        {
            if (value is not ("all" or "video" or "archive" or "subtitle" or "audio" or "document") ||
                !SetProperty(ref _mediaFilter, value)) return;
            OnPropertyChanged(nameof(IsAllMediaFilter));
            OnPropertyChanged(nameof(IsVideoMediaFilter));
            OnPropertyChanged(nameof(IsArchiveMediaFilter));
            OnPropertyChanged(nameof(IsSubtitleMediaFilter));
            OnPropertyChanged(nameof(IsAudioMediaFilter));
            OnPropertyChanged(nameof(IsDocumentMediaFilter));
            RefreshFilteredMedia();
        }
    }
    public bool IsAllMediaFilter { get => MediaFilter == "all"; set { if (value) MediaFilter = "all"; } }
    public bool IsVideoMediaFilter { get => MediaFilter == "video"; set { if (value) MediaFilter = "video"; } }
    public bool IsArchiveMediaFilter { get => MediaFilter == "archive"; set { if (value) MediaFilter = "archive"; } }
    public bool IsSubtitleMediaFilter { get => MediaFilter == "subtitle"; set { if (value) MediaFilter = "subtitle"; } }
    public bool IsAudioMediaFilter { get => MediaFilter == "audio"; set { if (value) MediaFilter = "audio"; } }
    public bool IsDocumentMediaFilter { get => MediaFilter == "document"; set { if (value) MediaFilter = "document"; } }
    public string FilteredMediaCountText =>
        LocalizationManager.Format("Telegram.MediaList.Count", FilteredMedia.Count);
    public string SelectedMediaName => SelectedMedia?.FileName ??
        LocalizationManager.Get("Telegram.MediaList.NoSelection");
    public string SelectedMediaSummary => SelectedMedia is null ? string.Empty :
        $"{LocalizationManager.Get("Telegram.MediaList.Type." + SelectedMedia.MediaCategory)} · " +
        $"{SelectedMedia.MediaSizeLabel}" +
        (SelectedMedia.HasArchivePartCount
            ? " · " + LocalizationManager.Format("Telegram.MediaList.PartsCount", SelectedMedia.ArchivePartCount)
            : string.Empty) +
        (SelectedMedia.PartStatusCode is "missing" or "ambiguous" or "standalone"
            ? " · " + LocalizationManager.Get("Telegram.MediaList.Status." + SelectedMedia.PartStatusCode)
            : string.Empty);
    public bool CanOpenSelectedMedia => SelectedMedia is not null &&
        SelectedMedia.Kind != TelegramMediaKind.ArchivePart && !SelectedMedia.NeedsArchiveRepair && !IsBusy;
    public bool CanAttachSelectedSubtitle => SelectedMedia is not null && !IsBusy &&
        (SelectedMedia.Kind == TelegramMediaKind.Subtitle || SelectedMedia.IsArchiveSet ||
         SelectedMedia.IsStandaloneArchive);
    public bool CanRepairSelectedArchive => SelectedMedia?.IsArchiveSet == true && !IsBusy;
    public bool CanCacheSelectedMedia => CanOpenSelectedMedia && _canMaintainCache();
    public void NotifyPlaybackCacheAvailabilityChanged() =>
        OnPropertyChanged(nameof(CanCacheSelectedMedia));
    private void NotifySelectedMediaCommands()
    {
        OnPropertyChanged(nameof(CanOpenSelectedMedia));
        OnPropertyChanged(nameof(CanAttachSelectedSubtitle));
        OnPropertyChanged(nameof(CanRepairSelectedArchive));
        OnPropertyChanged(nameof(CanCacheSelectedMedia));
    }
    private void RefreshFilteredMedia()
    {
        var chosen = Media.Where(item => _mediaFilter == "all" ||
                            item.MediaCategory == _mediaFilter).ToArray();
        // Filtering changes only presentation. Never clear the selected media
        // or requery TDLib when the user switches a category.
        var wanted = chosen.Select(item => (item.ChatId, item.MessageId)).ToHashSet();
        for (var i = FilteredMedia.Count - 1; i >= 0; --i)
            if (!wanted.Contains((FilteredMedia[i].ChatId, FilteredMedia[i].MessageId)))
                FilteredMedia.RemoveAt(i);
        for (var i = 0; i < chosen.Length; ++i)
        {
            if (i < FilteredMedia.Count &&
                FilteredMedia[i].ChatId == chosen[i].ChatId &&
                FilteredMedia[i].MessageId == chosen[i].MessageId)
            {
                if (!ReferenceEquals(FilteredMedia[i], chosen[i]))
                    FilteredMedia[i] = chosen[i];
                continue;
            }
            var previous = FilteredMedia.ToList().FindIndex(item =>
                item.ChatId == chosen[i].ChatId && item.MessageId == chosen[i].MessageId);
            if (previous >= 0) FilteredMedia.Move(previous, i);
            else FilteredMedia.Insert(i, chosen[i]);
        }
        OnPropertyChanged(nameof(FilteredMediaCountText));
    }
    public ICommand StartCommand { get; }
    public ICommand SubmitAuthorizationCommand { get; }
    public ICommand LoadChatsCommand { get; }
    public ICommand LoadMediaCommand { get; }
    public ICommand LoadMoreMediaCommand { get; }
    public bool CanLoadMoreMedia { get => _canLoadMoreMedia; private set => SetProperty(ref _canLoadMoreMedia, value); }
    public ICommand SearchCommand { get; }
    public ICommand GlobalSearchCommand { get; }
    public ICommand ShowAllTopicsCommand { get; }
    public ICommand DownloadAndOpenCommand { get; }
    public ICommand StopTransferCommand { get; }
    public ICommand ResumeTransferCommand { get; }
    public bool CanStopTransfer => _activeTransfer is not null && !_transferStopped;
    public bool CanResumeTransfer => _activeTransfer is not null && _transferStopped;
    public string ActiveTransferLabel => _activeTransfer is null
        ? string.Empty : $"Aktif Telegram: {_activeTransfer.FileName}";
    public ICommand AttachSelectedSubtitleCommand { get; }
    public ICommand RepairArchivePartsCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand OpenTelegramFolderCommand { get; }
    public ICommand RefreshCacheUsageCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand OpenCacheDirectoryCommand { get; }
    public IReadOnlyList<int> CacheLimitOptions => CacheQuotas;
    public int CacheLimitGiB
    {
        get => _settings.TelegramCacheLimitGiB;
        set
        {
            if (!CacheQuotas.Contains(value) || value == _settings.TelegramCacheLimitGiB) return;
            _settings.TelegramCacheLimitGiB = value;
            OnPropertyChanged();
            _lastAutomaticCacheCleanupUtc = DateTimeOffset.MinValue;
            _ = _saveSettings();
            if (CacheAutoCleanup) _ = ApplyAutomaticCacheCleanupAsync();
        }
    }
    public bool CacheAutoCleanup
    {
        get => _settings.TelegramCacheAutoCleanup;
        set
        {
            if (value == _settings.TelegramCacheAutoCleanup) return;
            _settings.TelegramCacheAutoCleanup = value;
            OnPropertyChanged();
            _lastAutomaticCacheCleanupUtc = DateTimeOffset.MinValue;
            _ = _saveSettings();
            if (value) _ = ApplyAutomaticCacheCleanupAsync();
        }
    }
    public string CacheDirectoryDisplay =>
        string.IsNullOrWhiteSpace(_settings.TelegramCacheDirectory)
            ? _paths.TelegramFilesDirectory : _settings.TelegramCacheDirectory;
    private string ActiveCacheDirectory => _cacheUsage?.FilesDirectory is { Length: > 0 } active
        ? active : _serviceStarted ? _sessionCacheDirectory : CacheDirectoryDisplay;
    private string _sessionCacheDirectory = string.Empty;
    public string CacheLocationHint => LocalizationManager.Get(_serviceStarted &&
        !string.Equals(CacheDirectoryDisplay, ActiveCacheDirectory, StringComparison.OrdinalIgnoreCase)
            ? "Settings.TelegramCache.Location.Restart"
            : "Settings.TelegramCache.Location.Current");
    public bool CacheIsBusy { get => _cacheIsBusy; private set => SetProperty(ref _cacheIsBusy, value); }
    public string CacheStatus => LocalizationManager.Format(_cacheStatusKey, _cacheStatusArguments);
    public string CacheUsageLabel => _cacheUsage is null
        ? LocalizationManager.Get("Settings.TelegramCache.Usage.Unread")
        : LocalizationManager.Format("Settings.TelegramCache.Usage.Files",
            FormatCacheBytes(_cacheUsage.FilesBytes), _cacheUsage.FileCount);
    public string CacheArchiveLabel => _cacheUsage is null ? string.Empty
        : LocalizationManager.Format("Settings.TelegramCache.Usage.Archive",
            FormatCacheBytes(_cacheUsage.ArchiveTemporaryBytes));
    public string CacheDatabaseLabel => _cacheUsage is null ? string.Empty
        : LocalizationManager.Format("Settings.TelegramCache.Usage.Database",
            FormatCacheBytes(_cacheUsage.DatabaseBytes));
    public string PhoneNumber { get => _phoneNumber; set => SetProperty(ref _phoneNumber, value); }
    public string EmailAddress { get => _emailAddress; set => SetProperty(ref _emailAddress, value); }
    public string Code { get => _code; set => SetProperty(ref _code, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string FirstName { get => _firstName; set => SetProperty(ref _firstName, value); }
    public string LastName { get => _lastName; set => SetProperty(ref _lastName, value); }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public TelegramAuthorizationSnapshot Authorization
    {
        get => _authorization;
        private set
        {
            if (!SetProperty(ref _authorization, value)) return;
            OnPropertyChanged(nameof(AuthorizationLabel));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsPhoneEntryVisible));
            OnPropertyChanged(nameof(IsEmailEntryVisible));
            OnPropertyChanged(nameof(IsCodeEntryVisible));
            OnPropertyChanged(nameof(IsPasswordEntryVisible));
            OnPropertyChanged(nameof(IsRegistrationEntryVisible));
            OnPropertyChanged(nameof(IsOtherConfirmationVisible));
            OnPropertyChanged(nameof(IsTelegramContentVisible));
            OnPropertyChanged(nameof(IsLoginPanelVisible));
        }
    }
    // TDLib briefly emits WaitPhoneNumber while the submitted phone is
    // already being processed. This is a state hint, not a validation error.
    public string AuthorizationLabel => _phoneSubmissionPending &&
        Authorization.Stage == TelegramAuthorizationStage.WaitingForPhoneNumber
            ? LocalizationManager.Get("R5177.Telegram.PhonePending")
            : Authorization.Message;

    private void SetPhoneSubmissionPending(bool pending)
    {
        if (_phoneSubmissionPending == pending) return;
        _phoneSubmissionPending = pending;
        OnPropertyChanged(nameof(AuthorizationLabel));
        OnPropertyChanged(nameof(IsPhoneEntryVisible));
        if (pending)
            StatusText = LocalizationManager.Get("R5177.Telegram.PhonePending");
    }
    public bool IsReady => Authorization.IsReady;
    // Only a verified TDLib getMe response can populate this. Hidden while
    // offline, logging out or changing accounts.
    public long? CurrentAccountId
    {
        get => _currentAccountId;
        private set => SetProperty(ref _currentAccountId, value);
    }
    public bool IsPhoneEntryVisible => !_phoneSubmissionPending && Authorization.Stage is
        TelegramAuthorizationStage.NotStarted or TelegramAuthorizationStage.Starting or
        TelegramAuthorizationStage.WaitingForParameters or TelegramAuthorizationStage.WaitingForPhoneNumber or
        TelegramAuthorizationStage.Closed or TelegramAuthorizationStage.Error;
    public bool IsEmailEntryVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForEmailAddress;
    public bool IsCodeEntryVisible => Authorization.Stage is TelegramAuthorizationStage.WaitingForCode or TelegramAuthorizationStage.WaitingForEmailCode;
    public bool IsPasswordEntryVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForPassword;
    public bool IsRegistrationEntryVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForRegistration;
    public bool IsOtherConfirmationVisible => Authorization.Stage == TelegramAuthorizationStage.WaitingForOtherDeviceConfirmation;
    public bool IsTelegramContentVisible => Authorization.IsReady;
    public bool IsLoginPanelVisible => !Authorization.IsReady;
    public bool NativeAvailable => _service.IsNativeAvailable;
    public string AvailabilityText => LocalizationManager.Get(NativeAvailable && CredentialsConfigured ? "Panel.TelegramReady" : "Panel.TelegramUnavailable");
    public string NativeDiagnostic => _service.NativeDiagnostic;
    public bool CredentialsConfigured => _telegramApiId > 0 && !string.IsNullOrWhiteSpace(_telegramApiHash);
    public string CredentialDiagnostic => CredentialsConfigured
        ? LocalizationManager.Get("Settings.Telegram.Configured")
        : LocalizationManager.Get("Settings.Telegram.Missing");
    public TelegramChatFolderItem? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetProperty(ref _selectedFolder, value) || !IsReady || _suppressFolderReload) return;
            SearchText = string.Empty;
            _ = LoadChatsAsync();
        }
    }
    public TelegramChatItem? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (!SetProperty(ref _selectedChat, value)) return;
            SearchText = string.Empty;
            OnPropertyChanged(nameof(CurrentContextTitle));
            OnPropertyChanged(nameof(CurrentContextPath));
            if (!IsReady || _suppressChatReload) return;
            _ = LoadChatContextAsync();
        }
    }

    public TelegramForumTopicItem? SelectedTopic
    {
        get => _selectedTopic;
        set
        {
            if (!SetProperty(ref _selectedTopic, value)) return;
            OnPropertyChanged(nameof(CurrentContextTitle));
            OnPropertyChanged(nameof(CurrentContextPath));
            if (!IsReady || _suppressTopicReload || SelectedChat is null) return;
            _ = LoadMediaAsync();
        }
    }

    public bool HasForumTopics => ForumTopics.Count > 0;
    public string CurrentContextTitle => SelectedTopic?.Name ?? SelectedChat?.Title ?? LocalizationManager.Get("Telegram.Media.Title");
    public string CurrentContextPath
    {
        get
        {
            var folder = SelectedFolder?.Name ?? LocalizationManager.Get("Telegram.AllChats");
            if (SelectedChat is null) return folder;
            return SelectedTopic is null
                ? $"{folder}  ›  {SelectedChat.Title}"
                : $"{folder}  ›  {SelectedChat.Title}  ›  {SelectedTopic.Name}";
        }
    }
    public TelegramMediaItem? SelectedMedia
    {
        get => _selectedMedia;
        set
        {
            if (!SetProperty(ref _selectedMedia, value)) return;
            OnPropertyChanged(nameof(IsArchiveSelected));
            OnPropertyChanged(nameof(SelectedMediaDiagnostic));
            OnPropertyChanged(nameof(SelectedMediaName));
            OnPropertyChanged(nameof(SelectedMediaSummary));
            NotifySelectedMediaCommands();
        }
    }
    public bool IsArchiveSelected => SelectedMedia?.IsArchiveSet == true;
    // Important for real-world Telegram filenames: captions are frequently
    // "MEDIA INFO" metadata and may not be the uploaded document name.
    // Show the actual TDLib filename when the entry was not grouped, so
    // diagnostics never depend on guessing from long Telegram captions.
    public string SelectedMediaDiagnostic => SelectedMedia is null
        ? string.Empty
        : SelectedMedia.IsArchiveSet
            ? $"Arşiv · {SelectedMedia.ArchiveParts?.Count ?? 0} parça · {SelectedMedia.FileName}"
            : $"Kaynak dosya: {SelectedMedia.FileName} · Tür: {SelectedMedia.Kind}";
    public bool IsAudioSelectionMode
    {
        get => _isAudioSelectionMode;
        private set
        {
            if (!SetProperty(ref _isAudioSelectionMode, value)) return;
            OnPropertyChanged(nameof(OpenActionText));
        }
    }
    public string OpenActionText => LocalizationManager.Get(IsAudioSelectionMode ? "Player.AudioSelection.Attach" : "Action.Open");
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) NotifySelectedMediaCommands(); }
    }
    public double DownloadPercent { get => _downloadPercent; private set => SetProperty(ref _downloadPercent, value); }

    public void BeginAudioSelection()
    {
        IsAudioSelectionMode = true;
        StatusText = IsReady
            ? "Telegram'dan bir ses dosyası veya ses içeren arşiv seçin."
            : "Telegram oturumunuz açıldığında ses dosyalarınızı seçebilirsiniz.";
    }

    public void EndAudioSelection() => IsAudioSelectionMode = false;

    private async Task LoadSecretAsync()
    {
        try
        {
            _databaseEncryptionKey = await _secrets.GetAsync(DatabaseKeySecretKey).ConfigureAwait(true) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_databaseEncryptionKey))
            {
                _databaseEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await _secrets.SetAsync(DatabaseKeySecretKey, _databaseEncryptionKey).ConfigureAwait(true);
            }

            await ReloadCredentialsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram güvenli ayarları yüklenemedi.", exception);
            StatusText = "Telegram güvenli ayarları yüklenemedi.";
        }
    }

    public async Task ReloadCredentialsAsync()
    {
        var storedHash = await _secrets.GetAsync(ApiHashSecretKey).ConfigureAwait(true);
        var hasUserOverride = _settings.TelegramApiId > 0 || !string.IsNullOrWhiteSpace(storedHash);

        if (hasUserOverride)
        {
            // Treat the Telegram application credential pair as atomic. Mixing a
            // user API ID with a packaged API hash (or vice versa) produces a
            // misleading TDLib authentication failure.
            _telegramApiId = _settings.TelegramApiId;
            _telegramApiHash = storedHash ?? string.Empty;
        }
        else
        {
            _telegramApiId = GeneratedTelegramCredentials.ApiId;
            _telegramApiHash = GeneratedTelegramCredentials.ApiHash;
        }

        OnPropertyChanged(nameof(CredentialsConfigured));
        OnPropertyChanged(nameof(CredentialDiagnostic));
        OnPropertyChanged(nameof(AvailabilityText));

        StatusText = _serviceStarted
            ? LocalizationManager.Get("Settings.Telegram.RestartHint")
            : AvailabilityText;
    }

    private async Task RestoreSavedSessionAsync()
    {
        await _loadSecretsTask.ConfigureAwait(true);
        if (!NativeAvailable || !CredentialsConfigured || !HasStoredSession())
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Telegram oturumu açılıyor…";
            await EnsureServiceStartedAsync(submitSavedPhone: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Warning($"Telegram kayıtlı oturumu açılamadı: {exception.Message}");
            StatusText = AvailabilityText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasStoredSession()
    {
        try
        {
            return Directory.Exists(_paths.TelegramDatabaseDirectory) &&
                   Directory.EnumerateFileSystemEntries(_paths.TelegramDatabaseDirectory).Any();
        }
        catch
        {
            return false;
        }
    }

    private async Task StartAsync()
    {
        await _loadSecretsTask.ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(PhoneNumber))
        {
            StatusText = "Telefon numaranızı ülke koduyla girin.";
            return;
        }

        if (!CredentialsConfigured)
        {
            StatusText = LocalizationManager.Get("Settings.Telegram.Missing");
            _logger.Warning("Telegram uygulama kimliği eksik.");
            _dialogs.ShowError(LocalizationManager.Get("Settings.Telegram.MissingHelp"));
            return;
        }

        try
        {
            IsBusy = true;
            _settings.TelegramPhoneNumber = PhoneNumber.Trim();
            await _saveSettings().ConfigureAwait(true);
            await EnsureServiceStartedAsync(submitSavedPhone: true).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            SetPhoneSubmissionPending(false);
            _logger.Error("Telegram başlatılamadı.", exception);
            StatusText = "Telegram başlatılamadı.";
            _dialogs.ShowError($"Telegram başlatılamadı.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureServiceStartedAsync(bool submitSavedPhone)
    {
        _submitPhoneWhenRequested = submitSavedPhone;
        SetPhoneSubmissionPending(submitSavedPhone);
        if (!_serviceStarted)
        {
            _serviceStarted = true;
            try
            {
                var filesDirectory = CacheDirectoryDisplay;
                _sessionCacheDirectory = filesDirectory;
                await _service.StartAsync(new TelegramClientConfiguration(
                    _telegramApiId,
                    _telegramApiHash,
                    _paths.TelegramDatabaseDirectory,
                    filesDirectory,
                    _databaseEncryptionKey)).ConfigureAwait(true);
                OnPropertyChanged(nameof(CacheLocationHint));
            }
            catch
            {
                _serviceStarted = false;
                SetPhoneSubmissionPending(false);
                throw;
            }
        }

        Authorization = _service.Authorization;
        StatusText = AuthorizationLabel;
        await SubmitPendingPhoneAsync().ConfigureAwait(true);
    }

    private async Task SubmitAuthorizationAsync()
    {
        try
        {
            IsBusy = true;
            switch (Authorization.Stage)
            {
                case TelegramAuthorizationStage.WaitingForPhoneNumber:
                    if (string.IsNullOrWhiteSpace(PhoneNumber)) throw new InvalidOperationException("Telefon numarası girin.");
                    _settings.TelegramPhoneNumber = PhoneNumber.Trim();
                    await _saveSettings().ConfigureAwait(true);
                    SetPhoneSubmissionPending(true);
                    try
                    {
                        await _service.SubmitPhoneNumberAsync(PhoneNumber).ConfigureAwait(true);
                    }
                    catch
                    {
                        SetPhoneSubmissionPending(false);
                        throw;
                    }
                    break;
                case TelegramAuthorizationStage.WaitingForEmailAddress:
                    await _service.SubmitEmailAddressAsync(EmailAddress).ConfigureAwait(true);
                    break;
                case TelegramAuthorizationStage.WaitingForEmailCode:
                    await _service.SubmitEmailCodeAsync(Code).ConfigureAwait(true);
                    Code = string.Empty;
                    break;
                case TelegramAuthorizationStage.WaitingForCode:
                    await _service.SubmitCodeAsync(Code).ConfigureAwait(true);
                    Code = string.Empty;
                    break;
                case TelegramAuthorizationStage.WaitingForPassword:
                    await _service.SubmitPasswordAsync(Password).ConfigureAwait(true);
                    Password = string.Empty;
                    break;
                case TelegramAuthorizationStage.WaitingForRegistration:
                    if (string.IsNullOrWhiteSpace(FirstName)) throw new InvalidOperationException("Ad alanı gereklidir.");
                    await _service.RegisterUserAsync(FirstName, LastName).ConfigureAwait(true);
                    break;
                default:
                    StatusText = "Şu anda gönderilecek bir Telegram doğrulama bilgisi yok.";
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram doğrulama bilgisi gönderilemedi.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"Telegram doğrulaması başarısız.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task LoadChatsAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.LoadingChats");
            var chats = await _service.GetChatsAsync(
                chatFolderId: SelectedFolder?.Id).ConfigureAwait(true);

            var previousChatId = SelectedChat?.Id;
            Chats.Clear();
            foreach (var chat in chats)
            {
                Chats.Add(chat);
                _knownChats[chat.Id] = chat;
            }
            if (SelectedFolder?.Id is not null && (_settings.TelegramFavoriteChatIds?.Count ?? 0) > 0)
            {
                // Keep favorites visible across custom folders without changing
                // the current chat list or loading any private message history.
                var allChats = await _service.GetChatsAsync(chatFolderId: null).ConfigureAwait(true);
                foreach (var chat in allChats) _knownChats.TryAdd(chat.Id, chat);
            }
            RefreshChatLists();

            var next = previousChatId is > 0
                ? Chats.FirstOrDefault(item => item.Id == previousChatId)
                : null;

            try
            {
                _suppressChatReload = true;
                SelectedChat = next ?? Chats.FirstOrDefault();
            }
            finally
            {
                _suppressChatReload = false;
            }

            if (SelectedChat is null)
            {
                ForumTopics.Clear();
                Media.Clear();
                RefreshFilteredMedia();
                SelectedMedia = null;
                OnPropertyChanged(nameof(HasForumTopics));
                OnPropertyChanged(nameof(CurrentContextTitle));
                OnPropertyChanged(nameof(CurrentContextPath));
                StatusText = LocalizationManager.Get("Telegram.Status.NoChats");
            }
            else
            {
                StatusText = string.Format(
                    LocalizationManager.Get("Telegram.Status.ChatCount"),
                    Chats.Count,
                    SelectedFolder?.Name ?? LocalizationManager.Get("Telegram.AllChats"));
                await LoadChatContextAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram sohbetleri alınamadı.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadChatContextAsync()
    {
        var chatAtStart = SelectedChat;
        if (chatAtStart is null)
        {
            ForumTopics.Clear();
            Media.Clear();
            RefreshFilteredMedia();
            SelectedMedia = null;
            OnPropertyChanged(nameof(HasForumTopics));
            CanLoadMoreMedia = false;
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.LoadingChat");

            var topics = await _service.GetForumTopicsAsync(chatAtStart.Id).ConfigureAwait(true);
            // Async chat switches can otherwise put the previous chat's
            // topic/media list under the newly selected chat.
            if (SelectedChat?.Id != chatAtStart.Id) return;
            try
            {
                _suppressTopicReload = true;
                ForumTopics.Clear();
                foreach (var topic in topics) ForumTopics.Add(topic);
                SelectedTopic = null;
            }
            finally
            {
                _suppressTopicReload = false;
            }

            OnPropertyChanged(nameof(HasForumTopics));
            OnPropertyChanged(nameof(CurrentContextTitle));
            OnPropertyChanged(nameof(CurrentContextPath));

            _mediaResultLimit = 300;
            _activeChatQuery = string.Empty;
            var chatId = chatAtStart.Id;
            var media = await _service.GetChatMediaAsync(
                chatId, query: string.Empty,
                limit: _mediaResultLimit, forumTopicId: null).ConfigureAwait(true);
            if (SelectedChat?.Id != chatId) return;
            ReplaceMedia(media);
            RememberRecentMedia(media);
            CanLoadMoreMedia = media.Count > 0;

            StatusText = topics.Count > 0
                ? string.Format(LocalizationManager.Get("Telegram.Status.TopicsAndMedia"), topics.Count, media.Count)
                : string.Format(LocalizationManager.Get("Telegram.Status.MediaCount"), media.Count);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram sohbet içeriği alınamadı.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private static string MediaCountLabel(IReadOnlyList<TelegramMediaItem> media)
    {
        var physicalFiles = media.Sum(item => item.ArchiveParts?.Count ?? 1);
        return $"{media.Count} medya grubu · {physicalFiles} Telegram dosyası";
    }

    private async Task LoadMediaAsync()
    {
        if (SelectedChat is null)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SelectChat");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.LoadingMedia");
            _mediaResultLimit = 300;
            _activeChatQuery = string.Empty;
            var chatId = SelectedChat.Id;
            var topicId = SelectedTopic?.Id;
            var media = await _service.GetChatMediaAsync(
                chatId, query: string.Empty, limit: _mediaResultLimit,
                forumTopicId: topicId).ConfigureAwait(true);
            if (SelectedChat?.Id != chatId || SelectedTopic?.Id != topicId) return;
            ReplaceMedia(media);
            RememberRecentMedia(media);
            CanLoadMoreMedia = media.Count > 0;
            StatusText = MediaCountLabel(media);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram medyası alınamadı.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task SearchAsync()
    {
        if (SelectedChat is null)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SelectChat");
            return;
        }

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            await LoadMediaAsync().ConfigureAwait(true);
            return;
        }

        if (query.Length < 2)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SearchMin");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.SearchingCurrent");
            _mediaResultLimit = 300;
            _activeChatQuery = query;
            var chatId = SelectedChat.Id;
            var topicId = SelectedTopic?.Id;
            var media = await _service.GetChatMediaAsync(
                chatId, query, limit: _mediaResultLimit,
                forumTopicId: topicId).ConfigureAwait(true);
            if (SelectedChat?.Id != chatId || SelectedTopic?.Id != topicId) return;
            ReplaceMedia(media);
            RememberRecentMedia(media);
            CanLoadMoreMedia = media.Count > 0;
            StatusText = MediaCountLabel(media);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram sohbet araması başarısız.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task GlobalSearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length < 2)
        {
            StatusText = LocalizationManager.Get("Telegram.Status.SearchMin");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Status.SearchingAll");
            var media = await _service.SearchMediaAsync(query).ConfigureAwait(true);
            _globalSearchMediaIds.Clear();
            foreach (var item in media) _globalSearchMediaIds.Add((item.ChatId, item.MessageId));
            ReplaceMedia(media);
            RememberRecentMedia(media);
            CanLoadMoreMedia = false;
            StatusText = string.Format(LocalizationManager.Get("Telegram.Status.GlobalSearchCount"), media.Count);
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram genel araması başarısız.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task ShowAllTopicsAsync()
    {
        if (SelectedChat is null) return;
        try
        {
            _suppressTopicReload = true;
            SelectedTopic = null;
        }
        finally
        {
            _suppressTopicReload = false;
        }
        OnPropertyChanged(nameof(CurrentContextTitle));
        OnPropertyChanged(nameof(CurrentContextPath));
        await LoadMediaAsync().ConfigureAwait(true);
    }

    private async Task LoadMoreMediaAsync()
    {
        if (SelectedChat is null || IsBusy || !CanLoadMoreMedia) return;
        var chatId = SelectedChat.Id;
        var topicId = SelectedTopic?.Id;
        var currentIds = Media.Select(item => (item.ChatId, item.MessageId)).ToHashSet();
        var nextLimit = Math.Min(2500, _mediaResultLimit + 300);
        if (nextLimit == _mediaResultLimit) { CanLoadMoreMedia = false; return; }
        try
        {
            IsBusy = true;
            StatusText = "Telegram'dan eski medya kayıtları yükleniyor…";
            var media = await _service.GetChatMediaAsync(
                chatId, _activeChatQuery, limit: nextLimit,
                forumTopicId: topicId).ConfigureAwait(true);
            if (SelectedChat?.Id != chatId || SelectedTopic?.Id != topicId) return;
            _mediaResultLimit = nextLimit;
            ReplaceMedia(media);
            RememberRecentMedia(media);
            var added = media.Count(item => !currentIds.Contains((item.ChatId, item.MessageId)));
            CanLoadMoreMedia = nextLimit < 2500 && added > 0;
            StatusText = added == 0
                ? $"{MediaCountLabel(media)} · Daha eski kayıt bulunamadı."
                : $"{MediaCountLabel(media)} · {added} yeni grup.";
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram eski medya kayıtları alınamadı.", exception);
            StatusText = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private void UpdateTransferControls()
    {
        OnPropertyChanged(nameof(CanStopTransfer));
        OnPropertyChanged(nameof(CanResumeTransfer));
        OnPropertyChanged(nameof(ActiveTransferLabel));
    }

    private async Task StopTransferAsync()
    {
        if (_activeTransfer is null || _transferStopped) return;
        var target = _activeTransfer;
        _transferStopped = true;
        _prefetchCancellation?.Cancel();
        UpdateTransferControls();
        try
        {
            await _service.StopMediaTransferAsync(target).ConfigureAwait(true);
            StatusText = "Telegram indirmesi durduruldu. Oynatma tamponu bitebilir; devam etmek için Devam veya Aç kullanın.";
        }
        catch (Exception error)
        {
            _logger.Error("Telegram indirmesini durdurma başarısız.", error);
            StatusText = $"Durdurma sırasında hata: {error.Message}";
            _dialogs.ShowError(error.Message, "Telegram indirmesi durdurulamadı");
        }
    }

    private void ResumeTransfer()
    {
        if (_activeTransfer is null || !_transferStopped) return;
        _service.ResumeMediaTransfer(_activeTransfer);
        _transferStopped = false;
        UpdateTransferControls();
        StatusText = "Telegram akışına izin verildi. Görüntü devam etmiyorsa Aç düğmesine basın.";
    }

    private async Task CacheSelectedMediaAsync()
    {
        if (IsBusy || SelectedMedia is not { } media) return;
        if (!_canMaintainCache())
        {
            StatusText = LocalizationManager.Get("Telegram.MediaList.CacheWaitIdle");
            return;
        }
        if (!CanCacheSelectedMedia) return;
        // Explicit offline-cache operation, not an export into a user-specified
        // Downloads folder. TDLib owns these files and may evict them on cleanup.
        var sourceParts = media.IsArchiveSet
            ? media.ArchiveParts?.Where(part => part.FileId > 0)
                .DistinctBy(part => part.FileId).ToArray() ?? []
            : [media];
        if (sourceParts.Length == 0)
        {
            StatusText = LocalizationManager.Get("Telegram.MediaList.CacheFailed");
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _prefetchCancellation = cancellation;
        _activeTransfer = media;
        _service.ResumeMediaTransfer(media);
        _transferStopped = false;
        UpdateTransferControls();
        DownloadPercent = 0;
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Format("Telegram.MediaList.CacheStarted", sourceParts.Length);
            foreach (var part in sourceParts)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await _service.DownloadMediaAsync(part, cancellation.Token).ConfigureAwait(true);
            }
            DownloadPercent = 100;
            StatusText = LocalizationManager.Format("Telegram.MediaList.CacheCompleted", sourceParts.Length);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText = LocalizationManager.Get("Telegram.MediaList.CacheStopped");
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram media cache prefetch failed.", exception);
            StatusText = LocalizationManager.Get("Telegram.MediaList.CacheFailed");
            _dialogs.ShowError(exception.Message, LocalizationManager.Get("Telegram.MediaList.CacheFailed"));
        }
        finally
        {
            if (ReferenceEquals(_prefetchCancellation, cancellation)) _prefetchCancellation = null;
            _activeTransfer = null;
            _transferStopped = false;
            UpdateTransferControls();
            IsBusy = false;
        }
    }

    private async Task DownloadAndOpenAsync(bool subtitleOnly = false)
    {
        if (IsBusy) return;
        if (SelectedMedia is null) { StatusText = "Önce bir Telegram medya dosyası seçin."; return; }
        try
        {
            IsBusy = true;
            DownloadPercent = 0;
            // Global search is limited to indexed message hits. It may return
            // only .002 even though the forum contains .001/.002. Re-enumerate
            // the source chat before opening, using the *same* canonical
            // grouping path as in-chat playback.
            if (_globalSearchMediaIds.Contains((SelectedMedia.ChatId, SelectedMedia.MessageId)) &&
                (SelectedMedia.IsArchiveSet || SelectedMedia.Kind == TelegramMediaKind.ArchivePart))
            {
                var selected = SelectedMedia;
                var sourceItems = await _service.GetChatMediaAsync(selected.ChatId,
                    query: null, limit: 2500).ConfigureAwait(true);
                var canonical = sourceItems.FirstOrDefault(item =>
                    item.MessageId == selected.MessageId ||
                    (item.ArchiveParts?.Any(part => part.MessageId == selected.MessageId) == true));
                if (canonical is not null)
                {
                    _logger.Info($"Telegram global search resolved archive: {selected.FileName} -> {canonical.FileName}");
                    SelectedMedia = canonical;
                }
            }
            _activeTransfer = SelectedMedia;
            // Only an explicit user open action may restart a stopped stream.
            _service.ResumeMediaTransfer(_activeTransfer);
            _transferStopped = false;
            UpdateTransferControls();
            if (SelectedMedia.IsArchiveSet)
            {
                await OpenArchiveAsync(SelectedMedia, subtitleOnly).ConfigureAwait(true);
                return;
            }
            if (ArchiveMediaAttachmentHelper.IsZip(SelectedMedia.FileName))
            {
                await OpenSingleZipAttachmentAsync(SelectedMedia, subtitleOnly).ConfigureAwait(true);
                return;
            }
            if (SelectedMedia.Kind == TelegramMediaKind.ArchivePart)
                throw new InvalidOperationException("Bu dosya bir arşiv parçası. Aynı sete ait tüm parçaları yükleyip yenileyin.");
            if (SelectedMedia.Kind == TelegramMediaKind.Subtitle)
            {
                if (!_hasPlayableMedia())
                    throw new InvalidOperationException("Harici altyazı eklemek için önce bir video açın.");
                StatusText = $"Telegram altyazısı indiriliyor: {SelectedMedia.FileName}";
                var subtitlePath = await _service.DownloadMediaAsync(SelectedMedia).ConfigureAwait(true);
                _attachSubtitle(subtitlePath);
                StatusText = $"Telegram altyazısı eklendi: {SelectedMedia.FileName}";
                return;
            }
            if (subtitleOnly)
                throw new InvalidOperationException("Altyazı eklemek için .srt/.ass/.vtt dosyası veya bunları içeren tek ZIP seçin.");
            if (IsAudioSelectionMode && SelectedMedia.Kind != TelegramMediaKind.Audio)
                throw new InvalidOperationException("Harici ses için bir ses dosyası veya ses içeren arşiv seçin.");
            StatusText = $"Telegram akışı açılıyor: {SelectedMedia.FileName}";
            var request = await _service.CreateStreamRequestAsync(SelectedMedia).ConfigureAwait(true);
            if (SelectedMedia.Kind == TelegramMediaKind.Audio)
            {
                await _attachAudio(request).ConfigureAwait(true);
                IsAudioSelectionMode = false;
                StatusText = $"Telegram harici sesi eklendi: {SelectedMedia.FileName}";
            }
            else
            {
                await _openSource(request).ConfigureAwait(true);
                StatusText = $"Telegram akışı oynatılıyor: {SelectedMedia.FileName}";
            }
        }
        catch (OperationCanceledException) when (_transferStopped)
        {
            StatusText = "Telegram akışı kullanıcı tarafından durduruldu.";
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram medya veya ZIP eki açılamadı.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError(exception.Message, "Telegram medya eki açılamadı");
        }
        finally { IsBusy = false; }
    }

    public async Task OpenPersistentAsync(string persistentSource)
    {
        if (!IsReady)
        {
            StatusText = LocalizationManager.Get("Telegram.Resume.Connect");
            _dialogs.ShowError(StatusText);
            return;
        }
        if (string.IsNullOrWhiteSpace(persistentSource)) return;
        var isArchive = persistentSource.StartsWith("telegram-archive:", StringComparison.OrdinalIgnoreCase);
        var parts = persistentSource.Split(':', isArchive ? 4 : 3);
        if (parts.Length != (isArchive ? 4 : 3) ||
            !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var chatId))
        {
            _dialogs.ShowError(LocalizationManager.Get("Telegram.Resume.Unavailable"));
            return;
        }
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Telegram.Resume.Restoring");
            // A fresh source request obtains a fresh localhost HTTP stream.
            // Never store ephemeral 127.0.0.1 URLs in playback history.
            var media = await _service.GetChatMediaAsync(chatId,
                query: null, limit: 2500).ConfigureAwait(true);
            if (isArchive)
            {
                var baseName = parts[2];
                var entryName = parts[3];
                var archive = media.FirstOrDefault(item => item.IsArchiveSet &&
                    string.Equals(item.ArchiveBaseName, baseName, StringComparison.Ordinal));
                if (archive is null)
                    throw new InvalidOperationException(LocalizationManager.Get("Telegram.Resume.Unavailable"));
                SelectedMedia = archive;
                await OpenArchiveAsync(archive, preferredEntryName: entryName).ConfigureAwait(true);
            }
            else
            {
                if (!long.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var messageId))
                    throw new InvalidOperationException(LocalizationManager.Get("Telegram.Resume.Unavailable"));
                var item = media.FirstOrDefault(candidate =>
                    candidate.MessageId == messageId ||
                    candidate.ArchiveParts?.Any(part => part.MessageId == messageId) == true);
                if (item is null)
                    throw new InvalidOperationException(LocalizationManager.Get("Telegram.Resume.Unavailable"));
                SelectedMedia = item;
                // Reuse identical logic for direct streams and archives without
                // nesting two IsBusy checks or skipping resume-state lookup.
                if (item.IsArchiveSet) await OpenArchiveAsync(item).ConfigureAwait(true);
                else if (ArchiveMediaAttachmentHelper.IsZip(item.FileName))
                    await OpenSingleZipAttachmentAsync(item, subtitleOnly: false).ConfigureAwait(true);
                else await _openSource(await _service.CreateStreamRequestAsync(item).ConfigureAwait(true));
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram resume source could not be restored.", exception);
            StatusText = LocalizationManager.Get("Telegram.Resume.Unavailable");
            _dialogs.ShowError(StatusText);
        }
        finally { IsBusy = false; }
    }

    private async Task OpenSingleZipAttachmentAsync(TelegramMediaItem item, bool subtitleOnly)
    {
        // A plain .zip is represented as a ONE-volume ZIP using the existing
        // TDLib byte-range reader, not DownloadMediaAsync (which waits for the
        // entire archive to arrive).
        var archive = item with
        {
            Kind = TelegramMediaKind.SplitArchive,
            ArchiveFormat = TelegramArchiveFormat.SplitZip,
            ArchiveBaseName = item.FileName,
            ArchiveParts = [item],
            ArchiveSetComplete = true,
            ArchiveSetAmbiguous = false
        };
        await OpenArchiveAsync(archive, subtitleOnly).ConfigureAwait(true);
    }

    private async Task OpenArchiveAsync(TelegramMediaItem archive, bool subtitleOnly = false, string? preferredEntryName = null)
    {
        if (archive.NeedsArchiveRepair)
            throw new InvalidOperationException("Arşiv parçaları eksik veya belirsiz. Önce Parçaları düzenle seçeneğini kullanın.");

        StatusText = $"Arşiv dizini Telegram üzerinden okunuyor: {archive.DisplayTitle}";
        var inspection = await _service.InspectArchiveAsync(archive).ConfigureAwait(true);
        if (!inspection.IsComplete) throw new InvalidOperationException(inspection.StatusMessage);
        var availableEntries = subtitleOnly
            ? inspection.Entries.Where(entry => ArchiveMediaAttachmentHelper.IsSubtitle(entry.Name)).ToArray()
            : IsAudioSelectionMode
                ? inspection.Entries.Where(entry => IsAudioFile(entry.Name)).ToArray()
                : inspection.Entries.ToArray();
        if (availableEntries.Length == 0)
            throw new InvalidOperationException(subtitleOnly
                ? "ZIP içinde desteklenen bir altyazı bulunamadı."
                : IsAudioSelectionMode ? "Arşivde desteklenen ses dosyası bulunamadı." : inspection.StatusMessage);

        var isStandaloneZip = archive.ArchiveParts is { Count: 1 } &&
            ArchiveMediaAttachmentHelper.IsZip(archive.ArchiveParts[0].FileName);

        // A season ZIP can contain dozens of different episode tracks.
        // Prefer the exact currently playing SxxEyy, but never guess an ID
        // when neither the video title nor filename carries an episode code.
        var hint = _currentEpisodeHint();
        var code = TelegramEpisodeTrackMatcher.Code(hint);
        var matches = TelegramEpisodeTrackMatcher.MatchingIndices(
            hint, availableEntries.Select(entry => entry.Name).ToArray());
        var matchingSet = matches.ToHashSet();
        var names = availableEntries.Select((entry, index) =>
            matchingSet.Contains(index) ? $"✓ {entry.DisplayLabel}" : entry.DisplayLabel).ToArray();
        var hasCodedOtherEpisode = code is not null &&
            matches.Count == 0 &&
            availableEntries.Any(entry => TelegramEpisodeTrackMatcher.Parse(entry.Name) is not null);

        IReadOnlyList<int> indexes = matches.Count > 0 ? [matches[0]] : [0];
        if (preferredEntryName is not null)
        {
            var matchingIndex = Array.FindIndex(availableEntries, candidate =>
                string.Equals(candidate.Name, preferredEntryName, StringComparison.Ordinal));
            if (matchingIndex < 0)
                throw new InvalidOperationException(LocalizationManager.Get("Telegram.Resume.Unavailable"));
            indexes = [matchingIndex];
        }
        else if (availableEntries.Length > 1 || hasCodedOtherEpisode)
        {
            var title = subtitleOnly ? "ZIP içinden altyazı seçin" :
                IsAudioSelectionMode ? "Arşivden ses dosyası seçin" :
                LocalizationManager.Get("Dialog.ArchiveMediaTitle");
            if (code is not null)
                title += matches.Count > 0 ? $" · Geçerli bölüm: {code}" :
                    $" · {code} için birebir eşleşme bulunamadı; elle seçin";
            indexes = _dialogs.SelectItems(
                title, names, hasCodedOtherEpisode ? [] : indexes,
                allowMultiple: isStandaloneZip);
            if (indexes.Count == 0)
            {
                StatusText = "Arşiv medya seçimi iptal edildi.";
                return;
            }
        }

        var chosen = indexes.Distinct()
            .Select(index => availableEntries[index])
            // If selected together, open the video before attaching its tracks.
            .OrderBy(entry => IsAudioFile(entry.Name) ? 1 :
                ArchiveMediaAttachmentHelper.IsSubtitle(entry.Name) ? 2 : 0)
            .ToArray();
        foreach (var entry in chosen)
        {
            if (!entry.CanInstantStream)
                throw new InvalidOperationException("Arşivdeki medya şifreli. Şifreli arşiv desteği etkin değil.");
            if (ArchiveMediaAttachmentHelper.IsSubtitle(entry.Name))
            {
                if (!_hasPlayableMedia())
                    throw new InvalidOperationException("Harici altyazı eklemek için önce bir video açın.");
                StatusText = $"Telegram üzerinden yalnız altyazı girdisi okunuyor: {entry.Name}";
                var subtitlePath = await _service.ExtractArchiveSubtitleAsync(archive, entry).ConfigureAwait(true);
                _attachSubtitle(subtitlePath);
                StatusText = $"Arşivden altyazı eklendi: {entry.Name}";
                continue;
            }

            StatusText = entry.UsesDirectRange
                ? $"Arşiv içi doğrudan akış açılıyor: {entry.Name}"
                : $"Arşiv içi kademeli çözme başlatılıyor: {entry.Name}";
            var request = await _service.CreateArchiveStreamRequestAsync(archive, entry).ConfigureAwait(true);
            if (IsAudioFile(entry.Name))
            {
                if (!_hasPlayableMedia())
                    throw new InvalidOperationException("Harici ses eklemek için önce bir video açın.");
                await _attachAudio(request).ConfigureAwait(true);
                IsAudioSelectionMode = false;
                StatusText = $"ZIP içinden harici ses akışı eklendi: {entry.Name}";
            }
            else
            {
                await _openSource(request).ConfigureAwait(true);
                StatusText = $"Arşivden video akışı açıldı: {entry.Name}";
            }
        }
    }

    private Task RepairArchivePartsAsync()
    {
        if (SelectedMedia is not { IsArchiveSet: true } current)
        {
            StatusText = "Önce çok parçalı bir arşiv seçin.";
            return Task.CompletedTask;
        }

        try
        {
            var candidates = Media
                .SelectMany(item => item.ArchiveParts is { Count: > 0 } parts
                    ? parts.AsEnumerable()
                    : item.Kind == TelegramMediaKind.ArchivePart
                        ? new[] { item }
                        : Enumerable.Empty<TelegramMediaItem>())
                .Where(item => item.ChatId == current.ChatId)
                .DistinctBy(item => item.FileId)
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedIds = (current.ArchiveParts ?? []).Select(item => item.FileId).ToHashSet();
            var initial = candidates
                .Select((item, index) => new { item.FileId, Index = index })
                .Where(item => selectedIds.Contains(item.FileId))
                .Select(item => item.Index)
                .ToArray();
            var selected = _dialogs.SelectItems(
                LocalizationManager.Get("Dialog.ArchivePartsTitle"),
                candidates.Select(item => $"{item.FileName} · {item.MetaLabel}").ToArray(),
                initial,
                allowMultiple: true);
            if (selected.Count == 0) return Task.CompletedTask;

            var rebuilt = TelegramArchiveSetDetector.BuildManualSet(selected.Select(index => candidates[index]).ToArray());
            var position = Media.IndexOf(current);
            if (position >= 0) Media[position] = rebuilt;
            else Media.Insert(0, rebuilt);
            RefreshFilteredMedia();
            SelectedMedia = rebuilt;
            StatusText = rebuilt.NeedsArchiveRepair
                ? "Seçilen parçalarda eksik sıra veya çakışma var."
                : $"{rebuilt.ArchiveParts?.Count ?? 0} arşiv parçası doğrulandı.";
        }
        catch (Exception exception)
        {
            _logger.Error("Telegram arşiv parçaları düzenlenemedi.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"Arşiv parçaları düzenlenemedi.\n\n{exception.Message}");
        }
        return Task.CompletedTask;
    }

    private static bool IsAudioFile(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is
        ".mka" or ".mp3" or ".flac" or ".aac" or ".ac3" or ".eac3" or ".dts" or
        ".dtshd" or ".thd" or ".truehd" or ".m4a" or ".opus" or ".ogg" or ".wav";

    private async Task LogoutAsync()
    {
        if (!_dialogs.Confirm("Telegram oturumunu kapatmak istediğinizden emin misiniz?")) return;
        try { await _service.SignOutAsync().ConfigureAwait(true); StatusText = "Telegram oturumu kapatılıyor…"; }
        catch (Exception exception) { StatusText = exception.Message; }
    }

    private void ReplaceMedia(IReadOnlyList<TelegramMediaItem> media)
    {
        var previous = SelectedMedia;
        Media.Clear();
        foreach (var item in media) Media.Add(item);
        RefreshFilteredMedia();
        SelectedMedia = previous is null
            ? Media.FirstOrDefault()
            : Media.FirstOrDefault(item => item.ChatId == previous.ChatId &&
                item.MessageId == previous.MessageId) ?? Media.FirstOrDefault();
    }

    private void OnAuthorizationChanged(object? sender, TelegramAuthorizationSnapshot snapshot) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var wasReady = Authorization.IsReady;
                if (snapshot.Stage is not (TelegramAuthorizationStage.NotStarted
                    or TelegramAuthorizationStage.Starting
                    or TelegramAuthorizationStage.WaitingForParameters
                    or TelegramAuthorizationStage.WaitingForPhoneNumber))
                    SetPhoneSubmissionPending(false);
                Authorization = snapshot;
                StatusText = AuthorizationLabel;
                if (!snapshot.IsReady)
                {
                    _accountIdentityGeneration++;
                    CurrentAccountId = null;
                    if (wasReady)
                    {
                        // Session-discovered attachments, chat titles and
                        // favorites are never carried into a second account.
                        _knownChats.Clear();
                        Chats.Clear();
                        while (FilteredChats.Count > 0)
                            FilteredChats.RemoveAt(FilteredChats.Count - 1);
                        FavoriteChats.Clear();
                        FavoriteRecentMedia.Clear();
                        _favoriteFeedLastRefreshUtc = DateTimeOffset.MinValue;
                        RecentMedia.Clear();
                        ForumTopics.Clear();
                        Media.Clear();
                        FilteredMedia.Clear();
                        ChatFolders.Clear();
                        SelectedMedia = null;
                        SelectedChat = null;
                        SelectedFolder = null;
                        CanLoadMoreMedia = false;
                    }
                }
                await SubmitPendingPhoneAsync().ConfigureAwait(true);
                if (snapshot.IsReady && !wasReady)
                {
                    // Resolve identity independently: do not hold up the chat
                    // listing while a getMe TDLib round trip is pending.
                    _ = ResolveCurrentAccountIdAsync(++_accountIdentityGeneration);
                    ApplyChatFolders(_service.ChatFolders);
                    await LoadChatsAsync().ConfigureAwait(true);
                    await ApplyAutomaticCacheCleanupAsync().ConfigureAwait(true);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("Telegram giriş adımı tamamlanamadı.", exception);
                StatusText = exception.Message;
            }
        });

    private async Task ResolveCurrentAccountIdAsync(int generation)
    {
        try
        {
            var id = await _service.GetCurrentUserIdAsync().ConfigureAwait(true);
            if (generation == _accountIdentityGeneration && IsReady)
                CurrentAccountId = id is > 0 ? id : null;
        }
        catch (Exception exception)
        {
            // Fail closed: never show previous-account content based solely
            // on authorizationStateReady or a remembered phone number.
            if (generation == _accountIdentityGeneration)
                CurrentAccountId = null;
            _logger.Warning($"Telegram hesap kimliği doğrulanamadı: {exception.Message}");
        }
    }

    private async Task SubmitPendingPhoneAsync()
    {
        if (!_submitPhoneWhenRequested || _phoneSubmissionInProgress ||
            Authorization.Stage != TelegramAuthorizationStage.WaitingForPhoneNumber) return;
        _phoneSubmissionInProgress = true;
        _submitPhoneWhenRequested = false;
        try
        {
            StatusText = LocalizationManager.Get("R5177.Telegram.PhonePending");
            await _service.SubmitPhoneNumberAsync(PhoneNumber.Trim()).ConfigureAwait(true);
        }
        catch
        {
            SetPhoneSubmissionPending(false);
            throw;
        }
        finally
        {
            _phoneSubmissionInProgress = false;
        }
    }

    private void OnChatFoldersChanged(object? sender, IReadOnlyList<TelegramChatFolderItem> folders) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            ApplyChatFolders(folders);
            if (IsReady) await LoadChatsAsync().ConfigureAwait(true);
        });

    private void ApplyChatFolders(IReadOnlyList<TelegramChatFolderItem> folders)
    {
        var selectedId = SelectedFolder?.Id;
        ChatFolders.Clear();
        foreach (var folder in folders)
        {
            var displayName = folder.Name switch
            {
                "Telegram.AllChats" => LocalizationManager.Get("Telegram.AllChats"),
                "Telegram.Archive" => LocalizationManager.Get("Telegram.Archive"),
                _ => folder.Name
            };
            ChatFolders.Add(folder with { Name = displayName });
        }
        if (ChatFolders.Count == 0)
        {
            ChatFolders.Add(new TelegramChatFolderItem(null, LocalizationManager.Get("Telegram.AllChats")));
            ChatFolders.Add(new TelegramChatFolderItem(-1, LocalizationManager.Get("Telegram.Archive"), int.MaxValue));
        }
        try
        {
            _suppressFolderReload = true;
            SelectedFolder = ChatFolders.FirstOrDefault(item => item.Id == selectedId) ?? ChatFolders.FirstOrDefault();
        }
        finally
        {
            _suppressFolderReload = false;
        }
    }

    private void OnDownloadProgressChanged(object? sender, TelegramDownloadProgress progress) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_activeTransfer is null || _transferStopped) return;
            if (_activeTransfer.FileId != progress.FileId &&
                _activeTransfer.ArchiveParts?.Any(part => part.FileId == progress.FileId) != true) return;
            DownloadPercent = progress.Percent;
            StatusText = progress.IsCompleted ? "Telegram önbellek aktarımı tamamlandı." :
                $"Telegram önbelleği alınıyor: %{progress.Percent:0.0}";
        });

    public async Task RefreshCacheUsageAsync()
    {
        if (CacheIsBusy) return;
        if (!IsReady)
        {
            SetCacheStatus("Settings.TelegramCache.Status.LoginRequired");
            return;
        }
        try
        {
            CacheIsBusy = true;
            _cacheUsage = await _service.GetCacheUsageAsync().ConfigureAwait(true);
            NotifyCacheUsageChanged();
            SetCacheStatus("Settings.TelegramCache.Status.Refreshed");
        }
        catch (Exception exception)
        {
            _logger.Warning($"Telegram önbelleği ölçülemedi: {exception.Message}");
            SetCacheStatus("Settings.TelegramCache.Status.UsageFailed");
        }
        finally { CacheIsBusy = false; }
    }

    public async Task ClearCacheAsync()
    {
        if (CacheIsBusy || !IsReady) return;
        if (!_canMaintainCache())
        {
            SetCacheStatus("Settings.TelegramCache.Status.StopPlayback");
            return;
        }
        if (!_dialogs.Confirm(
            LocalizationManager.Get("Settings.TelegramCache.Confirm.Message"),
            LocalizationManager.Get("Settings.TelegramCache.Confirm.Title"))) return;
        try
        {
            CacheIsBusy = true;
            _cacheUsage = await _service.OptimizeCacheAsync(0, manual: true).ConfigureAwait(true);
            NotifyCacheUsageChanged();
            SetCacheStatus("Settings.TelegramCache.Status.Cleared");
        }
        catch (Exception exception)
        {
            _logger.Warning($"Telegram önbelleği temizlenemedi: {exception.Message}");
            SetCacheStatus("Settings.TelegramCache.Status.ClearFailed");
        }
        finally { CacheIsBusy = false; }
    }

    public async Task ApplyAutomaticCacheCleanupAsync()
    {
        if (!CacheAutoCleanup || !IsReady || CacheIsBusy || !_canMaintainCache() ||
            DateTimeOffset.UtcNow - _lastAutomaticCacheCleanupUtc < TimeSpan.FromMinutes(30)) return;
        _lastAutomaticCacheCleanupUtc = DateTimeOffset.UtcNow;
        try
        {
            CacheIsBusy = true;
            var usage = await _service.GetCacheUsageAsync().ConfigureAwait(true);
            var maxBytes = CacheLimitGiB * 1024L * 1024L * 1024L;
            if (usage.FilesBytes > maxBytes)
            {
                usage = await _service.OptimizeCacheAsync(maxBytes, manual: false).ConfigureAwait(true);
                SetCacheStatus("Settings.TelegramCache.Status.AutoCleaned", CacheLimitGiB);
            }
            _cacheUsage = usage;
            NotifyCacheUsageChanged();
        }
        catch (Exception exception)
        {
            _logger.Warning($"Telegram otomatik önbellek bakımı: {exception.Message}");
        }
        finally { CacheIsBusy = false; }
    }

    public async Task SetCacheDirectoryAsync(string selectedBaseFolder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(selectedBaseFolder) ||
                !Path.IsPathFullyQualified(selectedBaseFolder) ||
                selectedBaseFolder.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidOperationException("Yerel ve tam bir klasör yolu seçin.");
            var newDirectory = Path.GetFullPath(
                Path.Combine(selectedBaseFolder, "ADB-Player-Telegram-Cache"));
            var database = Path.GetFullPath(_paths.TelegramDatabaseDirectory);
            if (newDirectory.Equals(database, StringComparison.OrdinalIgnoreCase) ||
                newDirectory.StartsWith(database + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                database.StartsWith(newDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Telegram oturum veritabanı klasörü seçilemez.");
            Directory.CreateDirectory(newDirectory);
            var probe = Path.Combine(newDirectory, ".adb-cache-write-" + Guid.NewGuid().ToString("N"));
            try { await File.WriteAllTextAsync(probe, "ok").ConfigureAwait(true); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
            _settings.TelegramCacheDirectory = newDirectory;
            await _saveSettings().ConfigureAwait(true);
            OnPropertyChanged(nameof(CacheDirectoryDisplay));
            OnPropertyChanged(nameof(CacheLocationHint));
            SetCacheStatus("Settings.TelegramCache.Status.FolderSaved");
        }
        catch (Exception exception)
        {
            _logger.Warning($"Telegram önbellek konumu değiştirilemedi: {exception.Message}");
            SetCacheStatus("Settings.TelegramCache.Status.FolderFailed");
        }
    }

    public async Task RestoreDefaultCacheDirectoryAsync()
    {
        _settings.TelegramCacheDirectory = string.Empty;
        await _saveSettings().ConfigureAwait(true);
        OnPropertyChanged(nameof(CacheDirectoryDisplay));
        OnPropertyChanged(nameof(CacheLocationHint));
        SetCacheStatus("Settings.TelegramCache.Status.DefaultRestored");
    }

    private void SetCacheStatus(string resourceKey, params object?[] args)
    {
        _cacheStatusKey = resourceKey;
        _cacheStatusArguments = args;
        OnPropertyChanged(nameof(CacheStatus));
    }

    private void NotifyCacheUsageChanged()
    {
        OnPropertyChanged(nameof(CacheUsageLabel));
        OnPropertyChanged(nameof(CacheArchiveLabel));
        OnPropertyChanged(nameof(CacheDatabaseLabel));
        OnPropertyChanged(nameof(CacheLocationHint));
    }

    private static string FormatCacheBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        var i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:0.##} {units[i]}";
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(AuthorizationLabel));
        OnPropertyChanged(nameof(CredentialDiagnostic));
        OnPropertyChanged(nameof(CurrentContextTitle));
        OnPropertyChanged(nameof(CurrentContextPath));
        OnPropertyChanged(nameof(FavoritesSummary));
        OnPropertyChanged(nameof(FilteredChatCountText));
        OnPropertyChanged(nameof(FilteredMediaCountText));
        OnPropertyChanged(nameof(SelectedMediaName));
        OnPropertyChanged(nameof(SelectedMediaSummary));
        OnPropertyChanged(nameof(CacheStatus));
        NotifyCacheUsageChanged();
        ApplyChatFolders(_service.ChatFolders);
    }

    public async ValueTask DisposeAsync()
    {
        _service.AuthorizationChanged -= OnAuthorizationChanged;
        _service.DownloadProgressChanged -= OnDownloadProgressChanged;
        _service.ChatFoldersChanged -= OnChatFoldersChanged;
        await _service.DisposeAsync().ConfigureAwait(false);
    }
}
