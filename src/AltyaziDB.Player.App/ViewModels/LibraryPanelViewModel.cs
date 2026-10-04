using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Core.Parsing;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.ViewModels;

public sealed record LibraryFilterChoice(string Label, LibraryMediaFilter Value);

public enum LibrarySortMode
{
    Smart,
    Title,
    YearNewest,
    RecentlyAdded,
    RecentlyPlayed,
    Largest
}

public enum LibraryBrowseScope
{
    AllFolders,
    SelectedFolder
}

public sealed record LibrarySortChoice(string Label, LibrarySortMode Value);
public sealed record LibraryScopeChoice(string Label, LibraryBrowseScope Value);

public sealed class LibraryPanelViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IMediaLibrary _library;
    private readonly ICloudAccountService _cloudAccount;
    private readonly ReleaseParser _releaseParser;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly MediaArtworkService _artwork;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly Func<string, Task> _openSource;
    private readonly SemaphoreSlim _artworkPageGate = new(1, 1);
    private readonly HashSet<string> _artworkAttemptedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _detailLoadedPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _artworkCts;
    private CancellationTokenSource? _selectedDetailCts;
    private LibraryFolder? _selectedFolder;
    private LibraryMediaItem? _selectedItem;
    private LibraryMediaItem? _selectedDetailItem;
    private LibraryGridItem? _selectedGridItem;
    private LibraryGridItem? _selectedSeriesHub;
    private LibrarySeriesSeasonChoice? _selectedSeriesSeason;
    private LibraryMediaItem? _selectedSeriesEpisode;
    private CloudCollectionItem? _selectedCloudItem;
    private CloudCollection? _selectedCloudCollection;
    private readonly Dictionary<string, LibraryMediaItem> _cloudLocalMatches = new(StringComparer.OrdinalIgnoreCase);
    private LibraryFilterChoice _selectedFilter =
        new("Library.Filter.All", LibraryMediaFilter.All);
    private LibrarySortChoice _selectedSort =
        new("Library.Sort.Smart", LibrarySortMode.Smart);
    private LibraryScopeChoice _selectedScope =
        new("Library.Scope.AllFolders", LibraryBrowseScope.AllFolders);
    private string _searchText = string.Empty;
    private string _newCollectionName = string.Empty;
    private string _statusText = "Kütüphane hazırlanıyor…";
    private string? _summaryOverride;
    private bool _isBusy;
    private bool _isCloudCollectionMode;
    private bool _suppressCloudCollectionAutoOpen;
    private bool _isGridView;
    private bool _isSelectedDetailLoading;
    private string _selectedDetailStatusText = string.Empty;

    public LibraryPanelViewModel(
        IMediaLibrary library,
        ICloudAccountService cloudAccount,
        ReleaseParser releaseParser,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths,
        MediaArtworkService artwork,
        PlayerSettings settings,
        Func<Task> saveSettings,
        Func<string, Task> openSource)
    {
        _library = library;
        _cloudAccount = cloudAccount;
        _releaseParser = releaseParser;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
        _artwork = artwork;
        _settings = settings;
        _saveSettings = saveSettings;
        _openSource = openSource;
        _isGridView = string.Equals(
            settings.LibraryViewMode,
            "grid",
            StringComparison.OrdinalIgnoreCase);

        Folders = new ObservableCollection<LibraryFolder>();
        Items = new ObservableCollection<LibraryMediaItem>();
        GridItems = new ObservableCollection<LibraryGridItem>();
        SeriesSeasonChoices = new ObservableCollection<LibrarySeriesSeasonChoice>();
        SeriesEpisodes = new ObservableCollection<LibraryMediaItem>();
        CloudItems = new ObservableCollection<CloudCollectionItem>();
        CloudCollections = new ObservableCollection<CloudCollection>();
        Filters = new ObservableCollection<LibraryFilterChoice>();
        SortChoices = new ObservableCollection<LibrarySortChoice>();
        ScopeChoices = new ObservableCollection<LibraryScopeChoice>();
        RefreshFilterChoices(LibraryMediaFilter.All);
        RefreshSortChoices(ParseSortMode(settings.LibrarySortMode));
        RefreshScopeChoices(ParseBrowseScope(settings.LibraryBrowseScope));

        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        ScanSelectedCommand = new AsyncRelayCommand(ScanSelectedAsync);
        ScanAllCommand = new AsyncRelayCommand(ScanAllAsync);
        RemoveFolderCommand = new AsyncRelayCommand(RemoveFolderAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PlaySelectedCommand = new AsyncRelayCommand(PlaySelectedAsync);
        OpenDatabaseFolderCommand = new RelayCommand(OpenDatabaseFolder);
        ShowListViewCommand = new RelayCommand(() => IsGridView = false);
        ShowGridViewCommand = new RelayCommand(() => IsGridView = true);
        CloseDetailCommand = new RelayCommand(CloseDetail);
        PlaySeriesNextCommand = new AsyncRelayCommand(PlaySeriesNextAsync);
        PlaySeriesEpisodeCommand = new AsyncRelayCommand(PlaySeriesEpisodeAsync);

        RefreshCloudListsCommand = new AsyncRelayCommand(OpenCloudListsAsync);
        CreateCloudListCommand = new AsyncRelayCommand(CreateCloudCollectionAsync);
        RenameCloudListCommand = new AsyncRelayCommand(RenameCloudCollectionAsync);
        DeleteCloudListCommand = new AsyncRelayCommand(DeleteCloudCollectionAsync);
        OpenCloudListCommand = new AsyncRelayCommand(OpenSelectedCloudCollectionAsync);
        CloseCloudListCommand = new AsyncRelayCommand(CloseCloudCollectionAsync);
        AddToFavoritesCommand = new AsyncRelayCommand(AddSelectedToFavoritesAsync);
        AddToWatchlistCommand = new AsyncRelayCommand(AddSelectedToWatchlistAsync);
        AddToSelectedCloudListCommand = new AsyncRelayCommand(AddSelectedToCurrentCollectionAsync);
        RemoveFromSelectedCloudListCommand = new AsyncRelayCommand(RemoveSelectedFromCurrentCollectionAsync);
    }

    public ObservableCollection<LibraryFolder> Folders { get; }
    public ObservableCollection<LibraryMediaItem> Items { get; }
    public ObservableCollection<LibraryGridItem> GridItems { get; }
    public ObservableCollection<LibrarySeriesSeasonChoice> SeriesSeasonChoices { get; }
    public ObservableCollection<LibraryMediaItem> SeriesEpisodes { get; }
    public ObservableCollection<CloudCollectionItem> CloudItems { get; }
    public ObservableCollection<CloudCollection> CloudCollections { get; }

    public event EventHandler? CloudCollectionsChanged;
    public ObservableCollection<LibraryFilterChoice> Filters { get; }
    public ObservableCollection<LibrarySortChoice> SortChoices { get; }
    public ObservableCollection<LibraryScopeChoice> ScopeChoices { get; }

    public ICommand AddFolderCommand { get; }
    public ICommand ScanSelectedCommand { get; }
    public ICommand ScanAllCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand PlaySelectedCommand { get; }
    public ICommand OpenDatabaseFolderCommand { get; }
    public ICommand ShowListViewCommand { get; }
    public ICommand ShowGridViewCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand PlaySeriesNextCommand { get; }
    public ICommand PlaySeriesEpisodeCommand { get; }
    public ICommand RefreshCloudListsCommand { get; }
    public ICommand CreateCloudListCommand { get; }
    public ICommand RenameCloudListCommand { get; }
    public ICommand DeleteCloudListCommand { get; }
    public ICommand OpenCloudListCommand { get; }
    public ICommand CloseCloudListCommand { get; }
    public ICommand AddToFavoritesCommand { get; }
    public ICommand AddToWatchlistCommand { get; }
    public ICommand AddToSelectedCloudListCommand { get; }
    public ICommand RemoveFromSelectedCloudListCommand { get; }

    public LibraryFolder? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetProperty(ref _selectedFolder, value)) return;
            if (!IsCloudCollectionMode
                && SelectedScope.Value == LibraryBrowseScope.SelectedFolder)
            {
                QueueRefresh();
            }
        }
    }

    public LibraryMediaItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;

            // WPF may transiently clear one of the two ListBox selections while
            // switching/recycling containers. Do not let that close the detail panel.
            if (value is null) return;

            SelectedDetailItem = value;
            _ = LoadSelectedDetailAsync(value);
        }
    }

    public LibraryMediaItem? SelectedDetailItem
    {
        get => _selectedDetailItem;
        private set => SetProperty(ref _selectedDetailItem, value);
    }

    public LibraryGridItem? SelectedGridItem
    {
        get => _selectedGridItem;
        set
        {
            if (!SetProperty(ref _selectedGridItem, value)) return;
            if (value is null) return;

            if (value.IsSeriesHub)
            {
                OpenSeriesHub(value);
                return;
            }

            ClearSeriesHubState();
            SelectedItem = value.Representative;
        }
    }

    public LibraryGridItem? SelectedSeriesHub
    {
        get => _selectedSeriesHub;
        private set => SetProperty(ref _selectedSeriesHub, value);
    }

    public LibrarySeriesSeasonChoice? SelectedSeriesSeason
    {
        get => _selectedSeriesSeason;
        set
        {
            if (!SetProperty(ref _selectedSeriesSeason, value)) return;
            RefreshSeriesEpisodes();
        }
    }

    public LibraryMediaItem? SelectedSeriesEpisode
    {
        get => _selectedSeriesEpisode;
        set => SetProperty(ref _selectedSeriesEpisode, value);
    }

    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (!SetProperty(ref _isGridView, value)) return;
            OnPropertyChanged(nameof(IsListView));

            if (!value && SelectedSeriesHub is not null)
                CloseDetail();

            var mode = value ? "grid" : "list";
            if (!string.Equals(_settings.LibraryViewMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LibraryViewMode = mode;
                _ = _saveSettings();
            }
        }
    }

    public bool IsListView => !IsGridView;

    public bool IsSelectedDetailLoading
    {
        get => _isSelectedDetailLoading;
        private set => SetProperty(ref _isSelectedDetailLoading, value);
    }

    public string SelectedDetailStatusText
    {
        get => _selectedDetailStatusText;
        private set => SetProperty(ref _selectedDetailStatusText, value ?? string.Empty);
    }

    public CloudCollectionItem? SelectedCloudItem
    {
        get => _selectedCloudItem;
        set => SetProperty(ref _selectedCloudItem, value);
    }

    public CloudCollection? SelectedCloudCollection
    {
        get => _selectedCloudCollection;
        set
        {
            if (!SetProperty(ref _selectedCloudCollection, value)) return;
            if (value is { IsSystem: false })
            {
                NewCollectionName = value.Name;
            }

            if (IsCloudCollectionMode && !IsBusy && !_suppressCloudCollectionAutoOpen)
                QueueCloudCollectionRefresh();
        }
    }

    public LibraryFilterChoice SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!SetProperty(ref _selectedFilter, value)) return;
            if (!IsCloudCollectionMode) QueueRefresh();
        }
    }

    public LibrarySortChoice SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!SetProperty(ref _selectedSort, value)) return;

            var mode = SortModeToSetting(value.Value);
            if (!string.Equals(_settings.LibrarySortMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LibrarySortMode = mode;
                _ = _saveSettings();
            }

            if (!IsCloudCollectionMode) QueueRefresh();
        }
    }

    public LibraryScopeChoice SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (!SetProperty(ref _selectedScope, value)) return;

            var scope = value.Value == LibraryBrowseScope.SelectedFolder ? "folder" : "all";
            if (!string.Equals(_settings.LibraryBrowseScope, scope, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LibraryBrowseScope = scope;
                _ = _saveSettings();
            }

            if (!IsCloudCollectionMode) QueueRefresh();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            if (IsCloudCollectionMode) QueueCloudCollectionRefresh();
            else QueueRefresh();
        }
    }

    public string NewCollectionName
    {
        get => _newCollectionName;
        set => SetProperty(ref _newCollectionName, value ?? string.Empty);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsCloudCollectionMode
    {
        get => _isCloudCollectionMode;
        private set
        {
            if (!SetProperty(ref _isCloudCollectionMode, value)) return;
            OnPropertyChanged(nameof(DisplayItemCount));
        }
    }

    public bool IsCloudAvailable => _cloudAccount.IsCloudCoreConfigured;
    public bool CanAddContentToSelectedCloudList => SelectedCloudCollection is { IsSystem: false };
    public int DisplayItemCount => IsCloudCollectionMode ? CloudItems.Count : Items.Count;
    public string Summary => _summaryOverride
        ?? (Items.Count == 0
            ? LocalizationManager.Get("Library.Summary.Empty")
            : LocalizationManager.Format("Library.Summary.Count", Items.Count));

    public async Task InitializeAsync()
    {
        await _library.InitializeAsync().ConfigureAwait(true);
        await ReloadFoldersAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        await RefreshCloudListsAsync(quiet: true).ConfigureAwait(true);
    }

    public async Task PlaySelectedAsync()
    {
        if (IsCloudCollectionMode)
        {
            var cloudItem = SelectedCloudItem;
            if (cloudItem is null)
            {
                StatusText = LocalizationManager.Get("Cloud.Collections.SelectMedia");
                return;
            }

            if (!_cloudLocalMatches.TryGetValue(cloudItem.MediaKey, out var localItem))
            {
                StatusText = LocalizationManager.Get("Cloud.Collections.NotOnDevice");
                return;
            }

            await _openSource(localItem.Path).ConfigureAwait(true);
            return;
        }

        var selectedLocalItem =
            SelectedItem
            ?? SelectedDetailItem
            ?? SelectedSeriesEpisode
            ?? SelectedSeriesHub?.NextPlayableItem;

        if (selectedLocalItem is not null)
            await _openSource(selectedLocalItem.Path).ConfigureAwait(true);
    }

    public async Task PlaySelectedGridAsync()
    {
        var gridItem = SelectedGridItem;
        if (gridItem is null)
        {
            await PlaySelectedAsync().ConfigureAwait(true);
            return;
        }

        var item = gridItem.IsSeriesHub
            ? gridItem.NextPlayableItem
            : gridItem.Representative;

        await _openSource(item.Path).ConfigureAwait(true);
    }

    private async Task PlaySeriesNextAsync()
    {
        var item = SelectedSeriesHub?.NextPlayableItem;
        if (item is not null)
            await _openSource(item.Path).ConfigureAwait(true);
    }

    private async Task PlaySeriesEpisodeAsync()
    {
        var item = SelectedSeriesEpisode ?? SelectedSeriesHub?.NextPlayableItem;
        if (item is not null)
            await _openSource(item.Path).ConfigureAwait(true);
    }

    public void UpdateVisibleProgress(string source, double position, double duration)
    {
        var index = Items.ToList().FindIndex(item =>
            string.Equals(item.Path, source, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        var old = Items[index];
        var updated = old with
        {
            PositionSeconds = position,
            DurationSeconds = duration,
            LastPlayedAtUtc = DateTimeOffset.UtcNow
        };
        Items[index] = updated;

        if (SelectedSeriesEpisode is not null
            && string.Equals(SelectedSeriesEpisode.Path, source, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSeriesEpisode = updated;
        }

        SyncGridItemForPath(source);
    }

    public async Task RefreshCloudListsAsync(bool quiet = false)
    {
        if (!_cloudAccount.IsCloudCoreConfigured)
        {
            CloudCollections.Clear();
            OnPropertyChanged(nameof(IsCloudAvailable));
            return;
        }

        try
        {
            var selectedId = SelectedCloudCollection?.Id;
            var result = await _cloudAccount.GetCollectionsAsync().ConfigureAwait(true);
            if (!result.Success)
            {
                if (!quiet && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                    StatusText = result.ErrorMessage;
                return;
            }

            _suppressCloudCollectionAutoOpen = true;
            try
            {
                CloudCollections.Clear();
                foreach (var item in result.Collections)
                    CloudCollections.Add(LocalizeCollection(item));

                SelectedCloudCollection =
                    CloudCollections.FirstOrDefault(item => item.Id == selectedId)
                    ?? CloudCollections.FirstOrDefault(item => item.Kind.Equals("favorites", StringComparison.OrdinalIgnoreCase))
                    ?? CloudCollections.FirstOrDefault();
            }
            finally
            {
                _suppressCloudCollectionAutoOpen = false;
            }

            OnPropertyChanged(nameof(IsCloudAvailable));
        }
        catch (Exception exception)
        {
            _logger.Warning($"Bulut listeleri yenilenemedi: {exception.Message}");
            if (!quiet) StatusText = LocalizationManager.Get("Cloud.Collections.Unavailable");
        }
    }

    private async Task AddFolderAsync()
    {
        var path = _dialogs.PickFolder(SelectedFolder?.Path);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            IsBusy = true;
            StatusText = "Klasör ekleniyor…";
            var folder = await _library.AddFolderAsync(path).ConfigureAwait(true);
            await ReloadFoldersAsync().ConfigureAwait(true);
            SelectedFolder = Folders.FirstOrDefault(item => item.Id == folder.Id);
            await ScanSelectedAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Kütüphane klasörü eklenemedi.", exception);
            _dialogs.ShowError($"Klasör eklenemedi.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ScanSelectedAsync()
    {
        if (SelectedFolder is null)
        {
            StatusText = "Önce bir kütüphane klasörü seçin.";
            return;
        }

        await ScanAsync(() => _library.ScanFolderAsync(SelectedFolder.Id, CreateProgress())).ConfigureAwait(true);
    }

    private async Task ScanAllAsync() =>
        await ScanAsync(() => _library.ScanAllAsync(CreateProgress())).ConfigureAwait(true);

    private async Task ScanAsync(Func<Task> action)
    {
        try
        {
            IsBusy = true;
            StatusText = "Kütüphane taranıyor…";
            await action().ConfigureAwait(true);
            await ReloadFoldersAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Kütüphane taraması başarısız.", exception);
            StatusText = "Kütüphane taraması başarısız.";
            _dialogs.ShowError($"Kütüphane taranamadı.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IProgress<LibraryScanProgress> CreateProgress() =>
        new Progress<LibraryScanProgress>(progress => StatusText = progress.StatusText);

    private async Task RemoveFolderAsync()
    {
        if (SelectedFolder is null) return;
        if (!_dialogs.Confirm($"'{SelectedFolder.Path}' kütüphaneden kaldırılacak. Dosyalar diskinizden silinmeyecek. Devam edilsin mi?"))
            return;

        try
        {
            IsBusy = true;
            await _library.RemoveFolderAsync(SelectedFolder.Id).ConfigureAwait(true);
            await ReloadFoldersAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            StatusText = "Klasör kütüphaneden kaldırıldı.";
        }
        catch (Exception exception)
        {
            _logger.Error("Kütüphane klasörü kaldırılamadı.", exception);
            _dialogs.ShowError(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadFoldersAsync()
    {
        var selectedId = SelectedFolder?.Id;
        var folders = await _library.GetFoldersAsync().ConfigureAwait(true);
        Folders.Clear();
        foreach (var folder in folders) Folders.Add(folder);

        var nextFolder =
            Folders.FirstOrDefault(folder => folder.Id == selectedId)
            ?? Folders.FirstOrDefault();

        if (!Equals(_selectedFolder, nextFolder))
        {
            _selectedFolder = nextFolder;
            OnPropertyChanged(nameof(SelectedFolder));
        }
    }

    private async Task RefreshAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();

        try
        {
            IsCloudCollectionMode = false;
            CloudItems.Clear();
            SelectedCloudItem = null;
            _cloudLocalMatches.Clear();
            OnPropertyChanged(nameof(DisplayItemCount));
            _summaryOverride = null;

            var query = ParseSmartQuery(SearchText);
            var rawItems = await _library.SearchAsync(
                query.Text,
                SelectedFilter.Value,
                limit: 5000,
                cancellationToken: _refreshCts.Token).ConfigureAwait(true);

            IEnumerable<LibraryMediaItem> visible = rawItems;

            if (SelectedScope.Value == LibraryBrowseScope.SelectedFolder
                && SelectedFolder is not null)
            {
                visible = visible.Where(item =>
                    string.Equals(
                        item.RootPath,
                        SelectedFolder.Path,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (query.Year is not null)
                visible = visible.Where(item => item.Year == query.Year);

            if (query.Season is not null)
                visible = visible.Where(item => item.Season == query.Season);

            if (query.Episode is not null)
                visible = visible.Where(item => item.Episode == query.Episode);

            if (!string.IsNullOrWhiteSpace(query.MediaType))
            {
                visible = visible.Where(item =>
                    string.Equals(
                        item.MediaType,
                        query.MediaType,
                        StringComparison.OrdinalIgnoreCase));
            }

            var items = SortItems(visible, SelectedSort.Value).ToArray();

            CloseDetail();
            Items.Clear();
            _detailLoadedPaths.Clear();
            foreach (var item in items) Items.Add(item);
            RebuildGridItems();

            OnPropertyChanged(nameof(DisplayItemCount));
            _ = RefreshArtworkAsync();
            OnPropertyChanged(nameof(Summary));
            StatusText = Folders.Count == 0
                ? LocalizationManager.Get("Library.Status.AddFolder")
                : LocalizationManager.Format("Library.Status.Listed", items.Length);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadSelectedDetailAsync(LibraryMediaItem? item)
    {
        _selectedDetailCts?.Cancel();
        _selectedDetailCts?.Dispose();
        _selectedDetailCts = null;

        if (item is null || IsCloudCollectionMode)
        {
            IsSelectedDetailLoading = false;
            SelectedDetailStatusText = string.Empty;
            return;
        }

        if (_detailLoadedPaths.Contains(item.Path))
        {
            IsSelectedDetailLoading = false;
            SelectedDetailStatusText = string.Empty;
            return;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _selectedDetailCts = cts;
        var cancellationToken = cts.Token;

        try
        {
            IsSelectedDetailLoading = true;
            SelectedDetailStatusText = LocalizationManager.Get("Library.Detail.Loading");

            var identity = BuildArtworkIdentity(item);
            var result = await _artwork
                .ResolveDetailAsync(
                    identity,
                    $"library-detail-{item.Id}",
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success || !result.Matched || result.Media is null)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (SelectedDetailItem is not null
                        && string.Equals(SelectedDetailItem.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedDetailStatusText = LocalizationManager.Get("Library.Detail.Unavailable");
                    }
                });
                return;
            }

            await ApplyDetailedMetadataAsync(
                    item.Path,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);

            var media = result.Media;
            if (!string.IsNullOrWhiteSpace(media.PosterUrl)
                || !string.IsNullOrWhiteSpace(media.BackdropUrl))
            {
                var key = CloudMediaIdentity.CreateMediaKey(identity, identity.Title);
                await _artwork.UpdateAsync(
                        [
                            new KeyValuePair<string, SharedMediaArtworkEntry>(
                                key,
                                new SharedMediaArtworkEntry(
                                    media.Title,
                                    media.PosterUrl,
                                    media.BackdropUrl,
                                    DateTimeOffset.UtcNow.AddDays(30)))
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"Kütüphane medya detayı yüklenemedi: {exception.Message}");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (SelectedDetailItem is not null
                    && string.Equals(SelectedDetailItem.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedDetailStatusText = LocalizationManager.Get("Library.Detail.Unavailable");
                }
            });
        }
        finally
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (SelectedDetailItem is not null
                    && string.Equals(SelectedDetailItem.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    IsSelectedDetailLoading = false;
                    if (_detailLoadedPaths.Contains(item.Path))
                        SelectedDetailStatusText = string.Empty;
                }
            });
        }
    }

    private async Task ApplyDetailedMetadataAsync(
        string path,
        MediaMetadataResult result,
        CancellationToken cancellationToken)
    {
        var media = result.Media;
        if (media is null) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || cancellationToken.IsCancellationRequested)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            for (var index = 0; index < Items.Count; index++)
            {
                var current = Items[index];
                if (!string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                var enriched = current with
                {
                    ResolvedTitle = string.IsNullOrWhiteSpace(media.Title)
                        ? current.ResolvedTitle
                        : media.Title.Trim(),
                    PosterUrl = string.IsNullOrWhiteSpace(media.PosterUrl)
                        ? current.PosterUrl
                        : media.PosterUrl,
                    BackdropUrl = string.IsNullOrWhiteSpace(media.BackdropUrl)
                        ? current.BackdropUrl
                        : media.BackdropUrl,
                    Year = media.Year ?? current.Year,
                    MediaType = string.IsNullOrWhiteSpace(media.MediaType)
                        ? current.MediaType
                        : media.MediaType,
                    AdbId = media.AdbId,
                    TmdbId = media.TmdbId,
                    ImdbId = media.ImdbId,
                    Overview = string.IsNullOrWhiteSpace(media.Overview)
                        ? current.Overview
                        : media.Overview.Trim(),
                    MetadataSource = result.Match?.Source,
                    MatchConfidence = result.Match?.Confidence,
                    TurkishSubtitleCount = result.Subtitles.TurkishCount,
                    EnglishSubtitleCount = result.Subtitles.EnglishCount
                };

                Items[index] = enriched;
                _detailLoadedPaths.Add(path);

                if (_selectedItem is not null
                    && string.Equals(_selectedItem.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedItem = enriched;
                    OnPropertyChanged(nameof(SelectedItem));
                }

                if (SelectedDetailItem is not null
                    && string.Equals(SelectedDetailItem.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedDetailItem = enriched;
                }

                SelectedDetailStatusText = string.Empty;
                RebuildGridItems();
                break;
            }
        });
    }

    public async Task RefreshArtworkAsync(bool retryMisses = false)
    {
        if (IsCloudCollectionMode || Items.Count == 0)
            return;

        _artworkCts?.Cancel();
        _artworkCts?.Dispose();
        _artworkCts = new CancellationTokenSource(TimeSpan.FromSeconds(18));

        await EnrichNextArtworkPageAsync(_artworkCts.Token, retryMisses).ConfigureAwait(true);
    }

    public async Task LoadMoreArtworkAsync()
    {
        if (IsCloudCollectionMode || Items.Count == 0)
            return;

        if (_artworkCts is null || _artworkCts.IsCancellationRequested)
        {
            _artworkCts?.Dispose();
            _artworkCts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        }

        await EnrichNextArtworkPageAsync(_artworkCts.Token).ConfigureAwait(true);
    }

    private async Task EnrichNextArtworkPageAsync(
        CancellationToken cancellationToken,
        bool retryMisses = false)
    {
        var gateHeld = false;
        try
        {
            await _artworkPageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            if (retryMisses)
                _artworkAttemptedKeys.Clear();

            var snapshot = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => Items.ToArray());

            if (snapshot.Length == 0)
                return;

            var work = snapshot
                .Select(item =>
                {
                    var identity = BuildArtworkIdentity(item);
                    var key = CloudMediaIdentity.CreateMediaKey(identity, identity.Title);
                    return new LibraryArtworkWork(item, identity, key);
                })
                .ToArray();

            var cache = await _artwork.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var misses = new List<LibraryArtworkWork>();

            foreach (var item in work)
            {
                if (cache.TryGetValue(item.CacheKey, out var cached)
                    && cached.ExpiresAtUtc > DateTimeOffset.UtcNow
                    && (!string.IsNullOrWhiteSpace(cached.PosterUrl)
                        || !string.IsNullOrWhiteSpace(cached.BackdropUrl)))
                {
                    _artworkAttemptedKeys.Add(item.CacheKey);
                    await ApplyArtworkAsync(
                            item.Item.Path,
                            cached.Title,
                            cached.PosterUrl,
                            cached.BackdropUrl,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_artworkAttemptedKeys.Contains(item.CacheKey))
                    continue;

                misses.Add(item);
                if (misses.Count >= 20)
                    break;
            }

            if (misses.Count == 0)
                return;

            foreach (var miss in misses)
                _artworkAttemptedKeys.Add(miss.CacheKey);

            var results = await _artwork
                .ResolveBatchAsync(
                    misses.Select(item => item.Identity).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            var updates = new List<KeyValuePair<string, SharedMediaArtworkEntry>>();
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

                updates.Add(new KeyValuePair<string, SharedMediaArtworkEntry>(
                    misses[index].CacheKey,
                    cached));

                await ApplyArtworkAsync(
                        misses[index].Item.Path,
                        media.Title,
                        media.PosterUrl,
                        media.BackdropUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (updates.Count > 0)
                await _artwork.UpdateAsync(updates, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"Kütüphane görselleri zenginleştirilemedi: {exception.Message}");
        }
        finally
        {
            if (gateHeld)
                _artworkPageGate.Release();
        }
    }

    private MediaIdentity BuildArtworkIdentity(LibraryMediaItem item)
    {
        var parsed = _releaseParser.Parse(item.FileName, item.Title);
        var title = string.IsNullOrWhiteSpace(parsed.Title)
            ? item.Title.Trim()
            : parsed.Title.Trim();
        var mediaType = item.MediaType is "movie" or "series"
            ? item.MediaType
            : parsed.ContentType;

        return new MediaIdentity(
            title,
            mediaType,
            item.FileName,
            parsed.Year ?? item.Year,
            parsed.Season ?? item.Season,
            parsed.Episode ?? item.Episode,
            parsed.ImdbId,
            parsed.TmdbId);
    }

    private async Task ApplyArtworkAsync(
        string path,
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
            for (var index = 0; index < Items.Count; index++)
            {
                var current = Items[index];
                if (!string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                var enriched = current with
                {
                    ResolvedTitle = string.IsNullOrWhiteSpace(resolvedTitle)
                        ? current.ResolvedTitle
                        : resolvedTitle.Trim(),
                    PosterUrl = string.IsNullOrWhiteSpace(posterUrl) ? null : posterUrl,
                    BackdropUrl = string.IsNullOrWhiteSpace(backdropUrl) ? null : backdropUrl
                };

                Items[index] = enriched;
                if (_selectedItem is not null
                    && string.Equals(_selectedItem.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedItem = enriched;
                    OnPropertyChanged(nameof(SelectedItem));
                }

                if (SelectedDetailItem is not null
                    && string.Equals(SelectedDetailItem.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedDetailItem = enriched;
                }

                SyncGridItemForPath(path);
                break;
            }
        });
    }

    private void RebuildGridItems()
    {
        var selectedKey = _selectedGridItem?.Key;
        var openHubKey = SelectedSeriesHub?.Key;
        var preferredSeason = SelectedSeriesSeason?.Season;

        var groups = new Dictionary<string, List<LibraryMediaItem>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var item in Items)
        {
            var key = CreateGridKey(item);
            if (!groups.TryGetValue(key, out var members))
            {
                members = [];
                groups[key] = members;
                order.Add(key);
            }

            members.Add(item);
        }

        GridItems.Clear();
        foreach (var key in order)
        {
            var members = groups[key];
            GridItems.Add(CreateGridItem(key, members));
        }

        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            var selected = GridItems.FirstOrDefault(item =>
                string.Equals(item.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                _selectedGridItem = selected;
                OnPropertyChanged(nameof(SelectedGridItem));
            }
        }

        if (!string.IsNullOrWhiteSpace(openHubKey))
        {
            var hub = GridItems.FirstOrDefault(item =>
                item.IsSeriesHub
                && string.Equals(item.Key, openHubKey, StringComparison.OrdinalIgnoreCase));

            if (hub is not null)
            {
                SelectedSeriesHub = hub;
                RefreshSeriesSeasonChoices(preferredSeason);
            }
            else
            {
                ClearSeriesHubState();
            }
        }
    }

    private void SyncGridItemForPath(string path)
    {
        var changed = Items.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (changed is null)
            return;

        var key = CreateGridKey(changed);
        var members = changed.IsSeries
            ? Items.Where(item =>
                    item.IsSeries
                    && string.Equals(
                        CreateGridKey(item),
                        key,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [changed];

        var updated = CreateGridItem(key, members);
        var index = GridItems
            .Select((item, itemIndex) => (item, itemIndex))
            .FirstOrDefault(pair =>
                string.Equals(pair.item.Key, key, StringComparison.OrdinalIgnoreCase))
            .itemIndex;

        if (index >= 0
            && index < GridItems.Count
            && string.Equals(GridItems[index].Key, key, StringComparison.OrdinalIgnoreCase))
        {
            GridItems[index] = updated;
        }
        else
        {
            RebuildGridItems();
            return;
        }

        if (_selectedGridItem is not null
            && string.Equals(_selectedGridItem.Key, key, StringComparison.OrdinalIgnoreCase))
        {
            _selectedGridItem = updated;
            OnPropertyChanged(nameof(SelectedGridItem));
        }

        if (SelectedSeriesHub is not null
            && string.Equals(SelectedSeriesHub.Key, key, StringComparison.OrdinalIgnoreCase))
        {
            var preferredSeason = SelectedSeriesSeason?.Season;
            SelectedSeriesHub = updated;
            RefreshSeriesSeasonChoices(preferredSeason);
        }
    }

    private LibraryGridItem CreateGridItem(
        string key,
        IReadOnlyList<LibraryMediaItem> members)
    {
        var isSeries = members.Count > 0 && members[0].IsSeries;
        var representative = members.FirstOrDefault(item =>
                                 !string.IsNullOrWhiteSpace(item.PosterUrl))
                             ?? members[0];

        var title = isSeries
            ? representative.EffectiveTitle
            : representative.DisplayTitle;

        var year = members
            .Select(item => item.Year)
            .FirstOrDefault(value => value is not null);

        var seasonCount = isSeries
            ? members
                .Select(item => item.Season ?? 0)
                .Distinct()
                .Count()
            : 0;

        var meta = isSeries
            ? LocalizationManager.Format(
                "Library.SeriesHub.CardMeta",
                seasonCount,
                members.Count)
            : representative.SizeLabel;

        return new LibraryGridItem(
            key,
            isSeries,
            title,
            representative.MediaType,
            year,
            members,
            meta);
    }

    private static string CreateGridKey(LibraryMediaItem item)
    {
        if (!item.IsSeries)
            return $"item|{item.Path}";

        var title = item.Title.Trim();
        return $"series|{title}";
    }

    private void OpenSeriesHub(LibraryGridItem hub)
    {
        ClearSingleDetailState();
        SelectedSeriesHub = hub;
        RefreshSeriesSeasonChoices(hub.NextPlayableItem.Season);
    }

    private void RefreshSeriesSeasonChoices(int? preferredSeason = null)
    {
        var hub = SelectedSeriesHub;
        if (hub is null)
        {
            SeriesSeasonChoices.Clear();
            SeriesEpisodes.Clear();
            _selectedSeriesSeason = null;
            _selectedSeriesEpisode = null;
            OnPropertyChanged(nameof(SelectedSeriesSeason));
            OnPropertyChanged(nameof(SelectedSeriesEpisode));
            return;
        }

        var previousSeason = preferredSeason
                             ?? SelectedSeriesSeason?.Season
                             ?? hub.NextPlayableItem.Season
                             ?? 0;

        var choices = hub.Members
            .GroupBy(item => item.Season ?? 0)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var label = group.Key > 0
                    ? LocalizationManager.Format(
                        "Library.SeriesHub.SeasonLabel",
                        group.Key,
                        group.Count())
                    : LocalizationManager.Format(
                        "Library.SeriesHub.OtherEpisodes",
                        group.Count());

                return new LibrarySeriesSeasonChoice(
                    group.Key,
                    group.Count(),
                    label);
            })
            .ToArray();

        SeriesSeasonChoices.Clear();
        foreach (var choice in choices)
            SeriesSeasonChoices.Add(choice);

        _selectedSeriesSeason =
            SeriesSeasonChoices.FirstOrDefault(choice => choice.Season == previousSeason)
            ?? SeriesSeasonChoices.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedSeriesSeason));
        RefreshSeriesEpisodes();
    }

    private void RefreshSeriesEpisodes()
    {
        var hub = SelectedSeriesHub;
        var season = SelectedSeriesSeason;
        var previousPath = SelectedSeriesEpisode?.Path;

        SeriesEpisodes.Clear();

        if (hub is null || season is null)
        {
            _selectedSeriesEpisode = null;
            OnPropertyChanged(nameof(SelectedSeriesEpisode));
            return;
        }

        var episodes = hub.Members
            .Where(item => (item.Season ?? 0) == season.Season)
            .OrderBy(item => item.Episode ?? int.MaxValue)
            .ThenBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        foreach (var episode in episodes)
            SeriesEpisodes.Add(episode);

        _selectedSeriesEpisode =
            (!string.IsNullOrWhiteSpace(previousPath)
                ? SeriesEpisodes.FirstOrDefault(item =>
                    string.Equals(item.Path, previousPath, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? SeriesEpisodes.FirstOrDefault(item =>
                string.Equals(
                    item.Path,
                    hub.NextPlayableItem.Path,
                    StringComparison.OrdinalIgnoreCase))
            ?? SeriesEpisodes.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedSeriesEpisode));
    }

    private void ClearSingleDetailState()
    {
        _selectedDetailCts?.Cancel();
        _selectedDetailCts?.Dispose();
        _selectedDetailCts = null;

        if (_selectedItem is not null)
        {
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
        }

        SelectedDetailItem = null;
        IsSelectedDetailLoading = false;
        SelectedDetailStatusText = string.Empty;
    }

    private void ClearSeriesHubState()
    {
        SelectedSeriesHub = null;
        SeriesSeasonChoices.Clear();
        SeriesEpisodes.Clear();

        if (_selectedSeriesSeason is not null)
        {
            _selectedSeriesSeason = null;
            OnPropertyChanged(nameof(SelectedSeriesSeason));
        }

        if (_selectedSeriesEpisode is not null)
        {
            _selectedSeriesEpisode = null;
            OnPropertyChanged(nameof(SelectedSeriesEpisode));
        }
    }

    private sealed record LibraryArtworkWork(
        LibraryMediaItem Item,
        MediaIdentity Identity,
        string CacheKey);

    private void CloseDetail()
    {
        ClearSingleDetailState();
        ClearSeriesHubState();

        if (_selectedGridItem is not null)
        {
            _selectedGridItem = null;
            OnPropertyChanged(nameof(SelectedGridItem));
        }
    }

    private async void QueueRefresh()
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void QueueCloudCollectionRefresh()
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(true);
            await OpenSelectedCloudCollectionAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CreateCloudCollectionAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCollectionName))
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.NameRequired");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cloudAccount
                .CreateCollectionAsync(NewCollectionName.Trim())
                .ConfigureAwait(true);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Collections.CreateFailed");
                return;
            }

            NewCollectionName = string.Empty;
            ReplaceCollections(result.Collections);
            SelectedCloudCollection = CloudCollections.LastOrDefault(item => !item.IsSystem)
                                      ?? SelectedCloudCollection;
            CloudCollectionsChanged?.Invoke(this, EventArgs.Empty);
            StatusText = LocalizationManager.Get("Cloud.Collections.Created");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RenameCloudCollectionAsync()
    {
        var selected = SelectedCloudCollection;
        if (selected is null || selected.IsSystem)
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.CustomOnly");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewCollectionName))
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.NameRequired");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cloudAccount
                .RenameCollectionAsync(selected.Id, NewCollectionName.Trim())
                .ConfigureAwait(true);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Collections.RenameFailed");
                return;
            }

            ReplaceCollections(result.Collections, selected.Id);
            CloudCollectionsChanged?.Invoke(this, EventArgs.Empty);
            StatusText = LocalizationManager.Get("Cloud.Collections.Renamed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteCloudCollectionAsync()
    {
        var selected = SelectedCloudCollection;
        if (selected is null || selected.IsSystem)
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.CustomOnly");
            return;
        }

        if (!_dialogs.Confirm(LocalizationManager.Format("Cloud.Collections.DeleteConfirm", selected.Name)))
            return;

        try
        {
            IsBusy = true;
            var result = await _cloudAccount
                .DeleteCollectionAsync(selected.Id)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Collections.DeleteFailed");
                return;
            }

            ReplaceCollections(result.Collections);
            if (IsCloudCollectionMode) await RefreshAsync().ConfigureAwait(true);
            CloudCollectionsChanged?.Invoke(this, EventArgs.Empty);
            StatusText = LocalizationManager.Get("Cloud.Collections.Deleted");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task AddSelectedToFavoritesAsync() =>
        AddSelectedToSystemCollectionAsync("favorites");

    private Task AddSelectedToWatchlistAsync() =>
        AddSelectedToSystemCollectionAsync("watchlist");

    private Task AddSelectedToCurrentCollectionAsync() =>
        AddSelectedToCollectionAsync(SelectedCloudCollection);

    private async Task AddSelectedToSystemCollectionAsync(string kind)
    {
        var collection = CloudCollections.FirstOrDefault(item =>
            item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

        if (collection is null)
        {
            await RefreshCloudListsAsync().ConfigureAwait(true);
            collection = CloudCollections.FirstOrDefault(item =>
                item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        }

        await AddSelectedToCollectionAsync(collection).ConfigureAwait(true);
    }

    private async Task AddSelectedToCollectionAsync(CloudCollection? collection)
    {
        var item = IsCloudCollectionMode
            ? SelectedCloudItem is null
                ? null
                : BuildManifestItem(SelectedCloudItem)
            : SelectedItem is null
                ? null
                : BuildManifestItem(SelectedItem);

        if (item is null)
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.SelectMedia");
            return;
        }

        if (collection is null)
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.Unavailable");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cloudAccount
                .AddCollectionItemAsync(collection.Id, item)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Collections.AddFailed");
                return;
            }

            await RefreshCloudListsAsync(quiet: true).ConfigureAwait(true);
            CloudCollectionsChanged?.Invoke(this, EventArgs.Empty);
            StatusText = LocalizationManager.Format("Cloud.Collections.Added", collection.Name);
            if (IsCloudCollectionMode && SelectedCloudCollection?.Id == collection.Id)
                await OpenSelectedCloudCollectionAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveSelectedFromCurrentCollectionAsync()
    {
        var collection = SelectedCloudCollection;
        var mediaKey = IsCloudCollectionMode
            ? SelectedCloudItem?.MediaKey
            : SelectedItem is null
                ? null
                : BuildManifestItem(SelectedItem).MediaKey;

        if (string.IsNullOrWhiteSpace(mediaKey) || collection is null)
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.SelectMedia");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cloudAccount
                .RemoveCollectionItemAsync(collection.Id, mediaKey)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Collections.RemoveFailed");
                return;
            }

            await RefreshCloudListsAsync(quiet: true).ConfigureAwait(true);
            if (IsCloudCollectionMode)
                await OpenSelectedCloudCollectionAsync().ConfigureAwait(true);
            CloudCollectionsChanged?.Invoke(this, EventArgs.Empty);
            StatusText = LocalizationManager.Get("Cloud.Collections.Removed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenCloudListsAsync()
    {
        await RefreshCloudListsAsync(quiet: true).ConfigureAwait(true);
        if (SelectedCloudCollection is not null)
            await OpenSelectedCloudCollectionAsync().ConfigureAwait(true);
    }

    public async Task OpenSelectedCloudCollectionAsync()
    {
        var collection = SelectedCloudCollection;
        if (collection is null)
        {
            StatusText = LocalizationManager.Get("Cloud.Collections.SelectList");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cloudAccount
                .GetCollectionItemsAsync(collection.Id)
                .ConfigureAwait(true);

            if (!result.Success || result.Collection is null)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Cloud.Collections.Unavailable");
                return;
            }

            var local = await _library
                .SearchAsync(null, LibraryMediaFilter.All, 5000)
                .ConfigureAwait(true);

            var localPairs = local
                .Select(item => (Item: item, Manifest: BuildManifestItem(item)))
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

            var search = SearchText.Trim();
            var cloudItems = result.Items
                .Where(item => string.IsNullOrWhiteSpace(search) ||
                               item.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();

            _cloudLocalMatches.Clear();
            var matches = new List<LibraryMediaItem>();
            foreach (var item in cloudItems)
            {
                LibraryMediaItem? localItem = null;
                if (localByKey.TryGetValue(item.MediaKey, out var exact))
                {
                    localItem = exact;
                }
                else
                {
                    var signature = CloudMediaIdentity.CreateMatchSignature(
                        item.Title,
                        item.MediaType,
                        item.Year,
                        item.Season,
                        item.Episode);
                    if (localBySignature.TryGetValue(signature, out var fallback))
                        localItem = fallback;
                }

                if (localItem is null) continue;
                _cloudLocalMatches[item.MediaKey] = localItem;
                matches.Add(localItem);
            }

            CloudItems.Clear();
            foreach (var item in cloudItems) CloudItems.Add(item);
            SelectedCloudItem = CloudItems.FirstOrDefault();

            CloseDetail();
            Items.Clear();
            foreach (var item in matches.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                Items.Add(item);
            RebuildGridItems();

            IsCloudCollectionMode = true;
            OnPropertyChanged(nameof(DisplayItemCount));
            var displayCollectionName = LocalizeCollection(result.Collection).Name;
            _summaryOverride = LocalizationManager.Format(
                "Cloud.Collections.LocalSummary",
                displayCollectionName,
                matches.Count,
                result.Items.Count);
            OnPropertyChanged(nameof(Summary));
            StatusText = LocalizationManager.Format(
                "Cloud.Collections.Opened",
                displayCollectionName,
                result.Items.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ShowLocalLibraryAsync()
    {
        CloudItems.Clear();
        SelectedCloudItem = null;
        _cloudLocalMatches.Clear();
        _summaryOverride = null;
        IsCloudCollectionMode = false;
        OnPropertyChanged(nameof(DisplayItemCount));
        OnPropertyChanged(nameof(Summary));
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task CloseCloudCollectionAsync()
    {
        await ShowLocalLibraryAsync().ConfigureAwait(true);
        StatusText = LocalizationManager.Get("Cloud.Collections.LocalLibrary");
    }

    private static CloudLibraryManifestItem BuildManifestItem(CloudCollectionItem item) =>
        new(
            item.MediaKey,
            item.Title,
            item.MediaType,
            item.Year,
            item.Season,
            item.Episode,
            item.ImdbId,
            item.TmdbId,
            item.SourceKind);

    private CloudLibraryManifestItem BuildManifestItem(LibraryMediaItem item)
    {
        var parsed = _releaseParser.Parse(item.FileName, item.Title);
        var title = string.IsNullOrWhiteSpace(parsed.Title) ? item.Title : parsed.Title.Trim();
        var mediaType = item.MediaType is "movie" or "series" ? item.MediaType : parsed.ContentType;
        var merged = new MediaIdentity(
            title,
            mediaType,
            item.FileName,
            parsed.Year ?? item.Year,
            parsed.Season ?? item.Season,
            parsed.Episode ?? item.Episode,
            parsed.ImdbId,
            parsed.TmdbId);

        return new CloudLibraryManifestItem(
            CloudMediaIdentity.CreateMediaKey(merged, title),
            title,
            merged.ContentType,
            merged.Year,
            merged.Season,
            merged.Episode,
            merged.ImdbId,
            merged.TmdbId,
            "local");
    }

    private void ReplaceCollections(
        IReadOnlyList<CloudCollection> collections,
        Guid? selectedId = null)
    {
        selectedId ??= SelectedCloudCollection?.Id;
        CloudCollections.Clear();
        foreach (var item in collections)
            CloudCollections.Add(LocalizeCollection(item));

        SelectedCloudCollection =
            CloudCollections.FirstOrDefault(item => item.Id == selectedId)
            ?? CloudCollections.FirstOrDefault(item => item.Kind.Equals("favorites", StringComparison.OrdinalIgnoreCase))
            ?? CloudCollections.FirstOrDefault();
    }

    private void RefreshFilterChoices(LibraryMediaFilter selectedValue)
    {
        Filters.Clear();
        Filters.Add(new("Library.Filter.All", LibraryMediaFilter.All));
        Filters.Add(new("Library.Filter.Movies", LibraryMediaFilter.Movies));
        Filters.Add(new("Library.Filter.Series", LibraryMediaFilter.Series));
        Filters.Add(new("Library.Filter.ContinueWatching", LibraryMediaFilter.ContinueWatching));
        Filters.Add(new("Library.Filter.Unwatched", LibraryMediaFilter.Unwatched));
        Filters.Add(new("Library.Filter.Completed", LibraryMediaFilter.Completed));

        _selectedFilter = Filters.FirstOrDefault(item => item.Value == selectedValue)
                          ?? Filters[0];
        OnPropertyChanged(nameof(SelectedFilter));
    }

    private void RefreshSortChoices(LibrarySortMode selectedValue)
    {
        SortChoices.Clear();
        SortChoices.Add(new("Library.Sort.Smart", LibrarySortMode.Smart));
        SortChoices.Add(new("Library.Sort.Title", LibrarySortMode.Title));
        SortChoices.Add(new("Library.Sort.YearNewest", LibrarySortMode.YearNewest));
        SortChoices.Add(new("Library.Sort.RecentlyAdded", LibrarySortMode.RecentlyAdded));
        SortChoices.Add(new("Library.Sort.RecentlyPlayed", LibrarySortMode.RecentlyPlayed));
        SortChoices.Add(new("Library.Sort.Largest", LibrarySortMode.Largest));

        _selectedSort = SortChoices.FirstOrDefault(item => item.Value == selectedValue)
                        ?? SortChoices[0];
        OnPropertyChanged(nameof(SelectedSort));
    }

    private void RefreshScopeChoices(LibraryBrowseScope selectedValue)
    {
        ScopeChoices.Clear();
        ScopeChoices.Add(new("Library.Scope.AllFolders", LibraryBrowseScope.AllFolders));
        ScopeChoices.Add(new("Library.Scope.SelectedFolder", LibraryBrowseScope.SelectedFolder));

        _selectedScope = ScopeChoices.FirstOrDefault(item => item.Value == selectedValue)
                         ?? ScopeChoices[0];
        OnPropertyChanged(nameof(SelectedScope));
    }

    private static LibrarySortMode ParseSortMode(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "title" => LibrarySortMode.Title,
            "year" => LibrarySortMode.YearNewest,
            "added" => LibrarySortMode.RecentlyAdded,
            "played" => LibrarySortMode.RecentlyPlayed,
            "size" => LibrarySortMode.Largest,
            _ => LibrarySortMode.Smart
        };

    private static string SortModeToSetting(LibrarySortMode value) =>
        value switch
        {
            LibrarySortMode.Title => "title",
            LibrarySortMode.YearNewest => "year",
            LibrarySortMode.RecentlyAdded => "added",
            LibrarySortMode.RecentlyPlayed => "played",
            LibrarySortMode.Largest => "size",
            _ => "smart"
        };

    private static LibraryBrowseScope ParseBrowseScope(string? value) =>
        string.Equals(value, "folder", StringComparison.OrdinalIgnoreCase)
            ? LibraryBrowseScope.SelectedFolder
            : LibraryBrowseScope.AllFolders;

    private static IEnumerable<LibraryMediaItem> SortItems(
        IEnumerable<LibraryMediaItem> items,
        LibrarySortMode mode)
    {
        return mode switch
        {
            LibrarySortMode.Title => items
                .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Year ?? int.MaxValue)
                .ThenBy(item => item.Season ?? 0)
                .ThenBy(item => item.Episode ?? 0),

            LibrarySortMode.YearNewest => items
                .OrderByDescending(item => item.Year ?? int.MinValue)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Season ?? 0)
                .ThenBy(item => item.Episode ?? 0),

            LibrarySortMode.RecentlyAdded => items
                .OrderByDescending(item => item.ModifiedAtUtc)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),

            LibrarySortMode.RecentlyPlayed => items
                .OrderByDescending(item => item.LastPlayedAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Season ?? 0)
                .ThenBy(item => item.Episode ?? 0),

            LibrarySortMode.Largest => items
                .OrderByDescending(item => item.SizeBytes)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),

            _ => items
        };
    }

    private static LibrarySmartQuery ParseSmartQuery(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return new LibrarySmartQuery(string.Empty, null, null, null, null);

        int? year = null;
        int? season = null;
        int? episode = null;
        string? mediaType = null;
        var free = new List<string>();

        foreach (var rawToken in text.Split(
                     (char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            var lower = token.ToLowerInvariant();

            if (TryReadNamedNumber(lower, "year:", 1900, 2100, out var parsedYear))
            {
                year = parsedYear;
                continue;
            }

            if (TryReadNamedNumber(lower, "season:", 0, 999, out var parsedSeason))
            {
                season = parsedSeason;
                continue;
            }

            if (TryReadNamedNumber(lower, "episode:", 0, 9999, out var parsedEpisode))
            {
                episode = parsedEpisode;
                continue;
            }

            if (lower.StartsWith("type:", StringComparison.Ordinal))
            {
                mediaType = lower[5..] switch
                {
                    "movie" or "film" => "movie",
                    "series" or "tv" or "dizi" => "series",
                    _ => mediaType
                };

                if (mediaType is not null)
                    continue;
            }

            if (TryReadSeasonEpisodeToken(lower, out var parsedS, out var parsedE))
            {
                season = parsedS ?? season;
                episode = parsedE ?? episode;
                continue;
            }

            free.Add(token);
        }

        return new LibrarySmartQuery(
            string.Join(' ', free),
            year,
            season,
            episode,
            mediaType);
    }

    private static bool TryReadNamedNumber(
        string token,
        string prefix,
        int min,
        int max,
        out int value)
    {
        value = 0;
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return int.TryParse(token[prefix.Length..], out value)
               && value >= min
               && value <= max;
    }

    private static bool TryReadSeasonEpisodeToken(
        string token,
        out int? season,
        out int? episode)
    {
        season = null;
        episode = null;

        if (token.Length >= 4 && token[0] == 's')
        {
            var eIndex = token.IndexOf('e', 1);
            if (eIndex > 1
                && int.TryParse(token[1..eIndex], out var s)
                && int.TryParse(token[(eIndex + 1)..], out var e))
            {
                season = s;
                episode = e;
                return true;
            }

            if (int.TryParse(token[1..], out var onlySeason))
            {
                season = onlySeason;
                return true;
            }
        }

        if (token.Length >= 2
            && token[0] == 'e'
            && int.TryParse(token[1..], out var onlyEpisode))
        {
            episode = onlyEpisode;
            return true;
        }

        return false;
    }

    private sealed record LibrarySmartQuery(
        string Text,
        int? Year,
        int? Season,
        int? Episode,
        string? MediaType);

    private static CloudCollection LocalizeCollection(CloudCollection collection)
    {
        var name = collection.Kind.ToLowerInvariant() switch
        {
            "favorites" => LocalizationManager.Get("Cloud.Collections.Favorites"),
            "watchlist" => LocalizationManager.Get("Cloud.Collections.Watchlist"),
            _ => collection.Name
        };
        return collection with { Name = name };
    }

    private void OpenDatabaseFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.RootDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logger.Error("Kütüphane klasörü açılamadı.", exception);
        }
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();

        RefreshFilterChoices(SelectedFilter.Value);
        RefreshSortChoices(SelectedSort.Value);
        RefreshScopeChoices(SelectedScope.Value);

        var selectedCollectionId = SelectedCloudCollection?.Id;
        var localizedCollections = CloudCollections.Select(LocalizeCollection).ToArray();
        CloudCollections.Clear();
        foreach (var item in localizedCollections) CloudCollections.Add(item);
        SelectedCloudCollection = CloudCollections.FirstOrDefault(item => item.Id == selectedCollectionId)
                                  ?? CloudCollections.FirstOrDefault();

        // CloudCollectionItem stores protocol-safe values such as "movie" and "catalog".
        // Re-add the same records so presentation converters run again after a language switch
        // without changing media identity or anything synchronized to ADB Cloud.
        var selectedMediaKey = SelectedCloudItem?.MediaKey;
        var cloudItems = CloudItems.ToArray();
        CloudItems.Clear();
        foreach (var item in cloudItems) CloudItems.Add(item);
        SelectedCloudItem = CloudItems.FirstOrDefault(item =>
                                string.Equals(item.MediaKey, selectedMediaKey, StringComparison.OrdinalIgnoreCase))
                            ?? CloudItems.FirstOrDefault();

        if (IsCloudCollectionMode && SelectedCloudCollection is not null)
        {
            _summaryOverride = LocalizationManager.Format(
                "Cloud.Collections.LocalSummary",
                SelectedCloudCollection.Name,
                _cloudLocalMatches.Count,
                CloudItems.Count);
        }

        RebuildGridItems();
        OnPropertyChanged(nameof(Summary));
    }

    public ValueTask DisposeAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _artworkCts?.Cancel();
        _artworkCts?.Dispose();
        _selectedDetailCts?.Cancel();
        _selectedDetailCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
