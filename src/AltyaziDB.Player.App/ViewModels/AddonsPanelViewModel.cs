using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using AltyaziDB.Player.Addons;
using AltyaziDB.Player.Addons.Models;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Core.Parsing;
using AltyaziDB.Player.Connections.Models;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class AddonsPanelViewModel : ObservableObject, IDisposable
{
    private readonly IAddonCatalogService _service;
    private readonly ICloudAccountService _cloudAccount;
    private readonly Func<ExternalSearchRequest, CancellationToken, Task<ExternalSearchResult>> _searchExternalReleases;
    private readonly Func<CatalogStream, Task> _openStream;
    private readonly Func<ExternalRelease, Task> _openExternalRelease;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly List<CatalogItem> _allItems = new();
    private readonly Dictionary<string, ExternalRelease> _externalReleasesByStreamKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watchlistKeys = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _favoritesCollectionId;
    private Guid? _watchlistCollectionId;
    private CloudCollection? _targetCollection;

    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _streamCancellation;
    private CatalogFilterOption? _selectedFilter;
    private CatalogItem? _selectedItem;
    private CatalogStream? _selectedStream;
    private CatalogEpisode? _selectedEpisode;
    private AddonRegistration? _selectedAddon;
    private string _addonUrl = string.Empty;
    private string _searchText = string.Empty;
    private string _statusText;
    private string _warningsText = string.Empty;
    private bool _isBusy;
    private bool _allowLocalHttp;
    private bool _legalNoticeAccepted;
    private bool _userOwnsAddonConfirmed;
    private bool _disposed;

    public AddonsPanelViewModel(
        IAddonCatalogService service,
        ICloudAccountService cloudAccount,
        Func<ExternalSearchRequest, CancellationToken, Task<ExternalSearchResult>> searchExternalReleases,
        Func<CatalogStream, Task> openStream,
        Func<ExternalRelease, Task> openExternalRelease,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths)
    {
        _service = service;
        _cloudAccount = cloudAccount;
        _searchExternalReleases = searchExternalReleases;
        _openStream = openStream;
        _openExternalRelease = openExternalRelease;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
        _statusText = LocalizationManager.Get("Addons.Status.AddAddon");

        Addons = new ObservableCollection<AddonRegistration>();
        Filters = new ObservableCollection<CatalogFilterOption>();
        Items = new ObservableCollection<CatalogItem>();
        Streams = new ObservableCollection<CatalogStream>();
        Episodes = new ObservableCollection<CatalogEpisode>();
        TargetCollections = new ObservableCollection<CloudCollection>();

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(forceRefresh: true));
        AddAddonCommand = new AsyncRelayCommand(AddAddonAsync);
        RemoveAddonCommand = new AsyncRelayCommand(RemoveAddonAsync);
        ToggleAddonCommand = new AsyncRelayCommand(ToggleAddonAsync);
        PlayStreamCommand = new AsyncRelayCommand(PlaySelectedStreamAsync);
        OpenAddonCacheCommand = new RelayCommand(OpenAddonCache);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);
        ToggleWatchlistCommand = new AsyncRelayCommand(ToggleWatchlistAsync);
        AddToTargetCollectionCommand = new AsyncRelayCommand(AddSelectedToTargetCollectionAsync);
        ClearTargetCollectionCommand = new RelayCommand(() => SetTargetCollection(null));

        RebuildFilters();
        _ = LoadAddonsAsync();
    }

    public event EventHandler? PortablePreferencesChanged;

    public event EventHandler? CloudCollectionsChanged;

    public ObservableCollection<AddonRegistration> Addons { get; }
    public ObservableCollection<CatalogFilterOption> Filters { get; }
    public ObservableCollection<CatalogItem> Items { get; }
    public ObservableCollection<CatalogStream> Streams { get; }
    public ObservableCollection<CatalogEpisode> Episodes { get; }
    public ObservableCollection<CloudCollection> TargetCollections { get; }

    public ICommand RefreshCommand { get; }
    public ICommand AddAddonCommand { get; }
    public ICommand RemoveAddonCommand { get; }
    public ICommand ToggleAddonCommand { get; }
    public ICommand PlayStreamCommand { get; }
    public ICommand OpenAddonCacheCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleWatchlistCommand { get; }
    public ICommand AddToTargetCollectionCommand { get; }
    public ICommand ClearTargetCollectionCommand { get; }

    public CatalogFilterOption? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                ApplyFilters();
            }
        }
    }

    public CatalogItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedItemTypeLabel));
            OnPropertyChanged(nameof(SelectedItemMetadata));
            UpdateSelectedCollectionState();
            _streamCancellation?.Cancel();
            Episodes.Clear();
            Streams.Clear();
            SelectedEpisode = null;
            SelectedStream = null;
            OnPropertyChanged(nameof(HasEpisodes));
            OnPropertyChanged(nameof(HasStreams));
            if (value is not null)
            {
                _streamCancellation = new CancellationTokenSource();
                _ = LoadItemOptionsAsync(value, _streamCancellation.Token);
            }
        }
    }

    public CatalogEpisode? SelectedEpisode
    {
        get => _selectedEpisode;
        set
        {
            if (!SetProperty(ref _selectedEpisode, value) || SelectedItem is null || value is null)
            {
                return;
            }

            _streamCancellation?.Cancel();
            _streamCancellation?.Dispose();
            _streamCancellation = new CancellationTokenSource();
            Streams.Clear();
            SelectedStream = null;
            OnPropertyChanged(nameof(HasStreams));
            _ = LoadStreamsAsync(SelectedItem, value, _streamCancellation.Token);
        }
    }

    public CatalogStream? SelectedStream
    {
        get => _selectedStream;
        set => SetProperty(ref _selectedStream, value);
    }

    public AddonRegistration? SelectedAddon
    {
        get => _selectedAddon;
        set
        {
            if (SetProperty(ref _selectedAddon, value))
            {
                OnPropertyChanged(nameof(SelectedAddonStateText));
            }
        }
    }

    public string AddonUrl
    {
        get => _addonUrl;
        set => SetProperty(ref _addonUrl, value);
    }

    public bool AllowLocalHttp
    {
        get => _allowLocalHttp;
        set => SetProperty(ref _allowLocalHttp, value);
    }

    public bool LegalNoticeAccepted
    {
        get => _legalNoticeAccepted;
        set => SetProperty(ref _legalNoticeAccepted, value);
    }

    public bool UserOwnsAddonConfirmed
    {
        get => _userOwnsAddonConfirmed;
        set => SetProperty(ref _userOwnsAddonConfirmed, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilters();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string WarningsText
    {
        get => _warningsText;
        private set => SetProperty(ref _warningsText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasAddons => Addons.Count > 0;
    public bool HasItems => Items.Count > 0;
    public bool HasStreams => Streams.Count > 0;
    public bool HasEpisodes => Episodes.Count > 0;
    public bool IsSelectedFavorite => SelectedItem is not null && _favoriteKeys.Contains(BuildManifestItem(SelectedItem).MediaKey);
    public bool IsSelectedWatchlist => SelectedItem is not null && _watchlistKeys.Contains(BuildManifestItem(SelectedItem).MediaKey);
    public string FavoriteActionText => LocalizationManager.Get(IsSelectedFavorite
        ? "Discover.Cloud.RemoveFavorite"
        : "Discover.Cloud.AddFavorite");
    public string WatchlistActionText => LocalizationManager.Get(IsSelectedWatchlist
        ? "Discover.Cloud.RemoveWatchlist"
        : "Discover.Cloud.AddWatchlist");
    public bool HasTargetCollection => _targetCollection is not null;
    public bool HasTargetCollections => TargetCollections.Count > 0;
    public CloudCollection? SelectedTargetCollection
    {
        get => _targetCollection;
        set => SetTargetCollection(value);
    }
    public string TargetCollectionName => _targetCollection?.Name ?? string.Empty;
    public string TargetCollectionActionText => _targetCollection is null
        ? LocalizationManager.Get("Discover.Cloud.AddToList")
        : LocalizationManager.Format("Discover.Cloud.AddToNamedList", _targetCollection.Name);
    public int MovieCount => _allItems.Count(item => item.ContentType == CatalogContentType.Movie);
    public int SeriesCount => _allItems.Count(item => item.ContentType == CatalogContentType.Series);
    public int AnimeCount => _allItems.Count(item => item.ContentType == CatalogContentType.Anime);
    public string SummaryText => string.Format(
        LocalizationManager.Get("Addons.SummaryFormat"),
        Items.Count,
        MovieCount,
        SeriesCount,
        AnimeCount);
    public string SelectedItemTypeLabel => SelectedItem is null
        ? string.Empty
        : GetContentTypeLabel(SelectedItem.ContentType);
    public string SelectedItemMetadata => SelectedItem is null
        ? string.Empty
        : string.Join(" · ", new[]
        {
            SelectedItemTypeLabel,
            SelectedItem.YearLabel,
            string.IsNullOrWhiteSpace(SelectedItem.Rating) ? string.Empty : $"★ {SelectedItem.Rating}",
            SelectedItem.GenreLabel,
            SelectedItem.OriginAddonName
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string SelectedAddonStateText => SelectedAddon is null
        ? string.Empty
        : LocalizationManager.Get(SelectedAddon.IsEnabled ? "Addons.Addon.Enabled" : "Addons.Addon.Disabled");



    public async Task<IReadOnlyList<CloudAddonPreference>> GetCloudAddonPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var addons = await _service.GetAddonsAsync(cancellationToken).ConfigureAwait(false);
            return addons
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Take(100)
                .Select(item => new CloudAddonPreference(
                    item.Id,
                    item.IsEnabled,
                    item.ManifestUrl,
                    item.AllowLocalHttp))
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning($"Bulut eklenti tercihleri hazırlanamadı: {exception.Message}");
            return Array.Empty<CloudAddonPreference>();
        }
    }

    public async Task ApplyCloudAddonPreferencesAsync(
        IReadOnlyList<CloudAddonPreference> preferences,
        bool authoritativeProfiles,
        CancellationToken cancellationToken = default)
    {
        if (preferences.Count == 0 && !authoritativeProfiles) return;

        try
        {
            var desired = preferences
                .Where(item => !string.IsNullOrWhiteSpace(item.AddonId))
                .GroupBy(item => item.AddonId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var changed = false;

            if (authoritativeProfiles)
            {
                foreach (var preference in desired.Values)
                {
                    if (string.IsNullOrWhiteSpace(preference.ManifestUrl))
                        continue;

                    try
                    {
                        await _service.RestoreAddonAsync(
                                preference.ManifestUrl,
                                preference.AllowLocalHttp,
                                preference.IsEnabled,
                                cancellationToken)
                            .ConfigureAwait(false);
                        changed = true;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.Warning(
                            $"Bulut eklentisi geri yüklenemedi ({preference.AddonId}): {exception.Message}");
                    }
                }

                var installedAfterRestore = await _service
                    .GetAddonsAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var addon in installedAfterRestore)
                {
                    if (desired.ContainsKey(addon.Id))
                        continue;

                    await _service.RemoveAddonAsync(addon.Id, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }
            }
            else
            {
                var installed = await _service.GetAddonsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var addon in installed)
                {
                    if (!desired.TryGetValue(addon.Id, out var preference) ||
                        addon.IsEnabled == preference.IsEnabled)
                        continue;

                    await _service
                        .SetAddonEnabledAsync(addon.Id, preference.IsEnabled, cancellationToken)
                        .ConfigureAwait(false);
                    changed = true;
                }
            }

            if (!changed) return;

            var refreshTask = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => RefreshAfterCloudAddonPreferencesAsync());
            await refreshTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"Bulut eklenti profili uygulanamadı: {exception.Message}");
        }
    }

    private async Task RefreshAfterCloudAddonPreferencesAsync()
    {
        await LoadAddonsAsync().ConfigureAwait(true);
        await RefreshAsync(forceRefresh: true).ConfigureAwait(true);
    }

    public void SetTargetCollection(CloudCollection? collection)
    {
        if (collection is { IsSystem: true })
            collection = null;

        if (ReferenceEquals(_targetCollection, collection))
            return;

        if (collection is not null && !collection.IsSystem &&
            TargetCollections.All(item => item.Id != collection.Id))
        {
            TargetCollections.Add(collection);
            OnPropertyChanged(nameof(HasTargetCollections));
        }

        _targetCollection = collection is null
            ? null
            : TargetCollections.FirstOrDefault(item => item.Id == collection.Id) ?? collection;
        OnPropertyChanged(nameof(SelectedTargetCollection));
        OnPropertyChanged(nameof(HasTargetCollection));
        OnPropertyChanged(nameof(TargetCollectionName));
        OnPropertyChanged(nameof(TargetCollectionActionText));

        if (collection is not null)
            StatusText = LocalizationManager.Format("Discover.Cloud.TargetReady", collection.Name);
    }

    private async Task AddSelectedToTargetCollectionAsync()
    {
        var item = SelectedItem;
        var target = _targetCollection;
        if (item is null || target is null) return;

        if (!_cloudAccount.IsCloudCoreConfigured)
        {
            StatusText = LocalizationManager.Get("Discover.Cloud.RequiresAccount");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cloudAccount
                .AddCollectionItemAsync(target.Id, BuildManifestItem(item))
                .ConfigureAwait(true);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Discover.Cloud.Unavailable");
                return;
            }

            CloudCollectionsChanged?.Invoke(this, EventArgs.Empty);
            StatusText = LocalizationManager.Format("Discover.Cloud.AddedToList", target.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshCloudCollectionsAsync()
    {
        if (!_cloudAccount.IsCloudCoreConfigured)
        {
            _favoriteKeys.Clear();
            _watchlistKeys.Clear();
            _favoritesCollectionId = null;
            _watchlistCollectionId = null;
            TargetCollections.Clear();
            SetTargetCollection(null);
            OnPropertyChanged(nameof(HasTargetCollections));
            UpdateSelectedCollectionState();
            return;
        }

        try
        {
            var collections = await _cloudAccount.GetCollectionsAsync().ConfigureAwait(true);
            if (!collections.Success) return;

            var favorites = collections.Collections.FirstOrDefault(item =>
                item.Kind.Equals("favorites", StringComparison.OrdinalIgnoreCase));
            var watchlist = collections.Collections.FirstOrDefault(item =>
                item.Kind.Equals("watchlist", StringComparison.OrdinalIgnoreCase));

            var selectedTargetId = _targetCollection?.Id;
            TargetCollections.Clear();
            foreach (var collection in collections.Collections.Where(item => !item.IsSystem))
                TargetCollections.Add(collection);

            var selectedTarget = selectedTargetId is null
                ? null
                : TargetCollections.FirstOrDefault(item => item.Id == selectedTargetId.Value);
            SetTargetCollection(selectedTarget);
            OnPropertyChanged(nameof(HasTargetCollections));

            _favoritesCollectionId = favorites?.Id;
            _watchlistCollectionId = watchlist?.Id;
            _favoriteKeys.Clear();
            _watchlistKeys.Clear();

            if (favorites is not null)
            {
                var items = await _cloudAccount.GetCollectionItemsAsync(favorites.Id).ConfigureAwait(true);
                if (items.Success)
                    foreach (var item in items.Items) _favoriteKeys.Add(item.MediaKey);
            }

            if (watchlist is not null)
            {
                var items = await _cloudAccount.GetCollectionItemsAsync(watchlist.Id).ConfigureAwait(true);
                if (items.Success)
                    foreach (var item in items.Items) _watchlistKeys.Add(item.MediaKey);
            }

            UpdateSelectedCollectionState();
        }
        catch (Exception exception)
        {
            _logger.Warning($"Keşfet bulut listeleri yenilenemedi: {exception.Message}");
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        await ToggleSystemCollectionAsync(
            "favorites",
            () => _favoritesCollectionId,
            id => _favoritesCollectionId = id,
            _favoriteKeys).ConfigureAwait(true);
    }

    private async Task ToggleWatchlistAsync()
    {
        await ToggleSystemCollectionAsync(
            "watchlist",
            () => _watchlistCollectionId,
            id => _watchlistCollectionId = id,
            _watchlistKeys).ConfigureAwait(true);
    }

    private async Task ToggleSystemCollectionAsync(
        string kind,
        Func<Guid?> getCollectionId,
        Action<Guid?> setCollectionId,
        HashSet<string> keys)
    {
        var item = SelectedItem;
        if (item is null) return;

        if (!_cloudAccount.IsCloudCoreConfigured)
        {
            StatusText = LocalizationManager.Get("Discover.Cloud.RequiresAccount");
            return;
        }

        try
        {
            IsBusy = true;
            if (getCollectionId() is null)
            {
                await RefreshCloudCollectionsAsync().ConfigureAwait(true);
                var collections = await _cloudAccount.GetCollectionsAsync().ConfigureAwait(true);
                if (collections.Success)
                {
                    setCollectionId(collections.Collections.FirstOrDefault(value =>
                        value.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))?.Id);
                }
            }

            var collectionId = getCollectionId();
            if (collectionId is null)
            {
                StatusText = LocalizationManager.Get("Discover.Cloud.Unavailable");
                return;
            }

            var manifest = BuildManifestItem(item);
            if (keys.Contains(manifest.MediaKey))
            {
                var result = await _cloudAccount
                    .RemoveCollectionItemAsync(collectionId.Value, manifest.MediaKey)
                    .ConfigureAwait(true);
                if (!result.Success)
                {
                    StatusText = result.ErrorMessage ?? LocalizationManager.Get("Discover.Cloud.Unavailable");
                    return;
                }
                keys.Remove(manifest.MediaKey);
            }
            else
            {
                var result = await _cloudAccount
                    .AddCollectionItemAsync(collectionId.Value, manifest)
                    .ConfigureAwait(true);
                if (!result.Success)
                {
                    StatusText = result.ErrorMessage ?? LocalizationManager.Get("Discover.Cloud.Unavailable");
                    return;
                }
                keys.Add(manifest.MediaKey);
            }

            UpdateSelectedCollectionState();
            CloudCollectionsChanged?.Invoke(this, EventArgs.Empty);
            StatusText = LocalizationManager.Get("Discover.Cloud.Updated");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CloudLibraryManifestItem BuildManifestItem(CatalogItem item)
    {
        var mediaType = item.ContentType switch
        {
            CatalogContentType.Movie => "movie",
            CatalogContentType.Series => "series",
            CatalogContentType.Anime => "series",
            _ => "unknown"
        };

        var imdbId = item.ExternalId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
                     item.ExternalId.Skip(2).All(char.IsDigit)
            ? item.ExternalId.ToLowerInvariant()
            : null;

        string? tmdbId = null;
        if (item.ExternalId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
            tmdbId = item.ExternalId["tmdb:".Length..];

        var identity = new MediaIdentity(
            item.Title,
            mediaType,
            item.Key,
            item.Year,
            null,
            null,
            imdbId,
            tmdbId);

        return new CloudLibraryManifestItem(
            CloudMediaIdentity.CreateMediaKey(identity, item.Title),
            item.Title,
            mediaType,
            item.Year,
            null,
            null,
            imdbId,
            tmdbId,
            "catalog");
    }

    private void UpdateSelectedCollectionState()
    {
        OnPropertyChanged(nameof(IsSelectedFavorite));
        OnPropertyChanged(nameof(IsSelectedWatchlist));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(WatchlistActionText));
        OnPropertyChanged(nameof(TargetCollectionActionText));
    }

    public async Task EnsureLoadedAsync()
    {
        if (_allItems.Count == 0 && !IsBusy)
        {
            await RefreshAsync(forceRefresh: false).ConfigureAwait(true);
        }
        await RefreshCloudCollectionsAsync().ConfigureAwait(true);
    }

    public Task<string> DownloadTorrentAsync(CatalogStream stream) =>
        _service.DownloadTorrentAsync(stream, _paths.AddonCacheDirectory);

    private async Task LoadAddonsAsync()
    {
        try
        {
            var addons = await _service.GetAddonsAsync().ConfigureAwait(true);
            ReplaceAddons(addons);
        }
        catch (Exception exception)
        {
            _logger.Error("Eklentiler yüklenemedi.", exception);
            StatusText = LocalizationManager.Get("Addons.Status.LoadFailed");
        }
    }

    private async Task RefreshAsync(bool forceRefresh)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Addons.Status.Loading");
            WarningsText = string.Empty;
            var snapshot = await _service.GetLatestAsync(
                    CatalogContentType.All,
                    forceRefresh,
                    cancellationToken)
                .ConfigureAwait(true);

            ReplaceAddons(snapshot.Addons);
            _allItems.Clear();
            _allItems.AddRange(snapshot.Items);
            WarningsText = string.Join(Environment.NewLine, snapshot.Warnings.Take(4));
            ApplyFilters();
            StatusText = snapshot.Addons.Count == 0
                ? LocalizationManager.Get("Addons.Status.AddAddon")
                : _allItems.Count == 0
                    ? LocalizationManager.Get("Addons.Status.NoItems")
                    : string.Format(LocalizationManager.Get("Addons.Status.ReadyFormat"), _allItems.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Eklenti katalogları yüklenemedi.", exception);
            StatusText = LocalizationManager.Get("Addons.Status.LoadFailed");
            WarningsText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddAddonAsync()
    {
        if (string.IsNullOrWhiteSpace(AddonUrl))
        {
            StatusText = LocalizationManager.Get("Addons.Status.AddressRequired");
            return;
        }

        if (!LegalNoticeAccepted || !UserOwnsAddonConfirmed)
        {
            StatusText = LocalizationManager.Get("Addons.Status.ConfirmationRequired");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Addons.Status.ValidatingAddon");
            var addon = await _service.AddAddonAsync(AddonUrl.Trim(), AllowLocalHttp).ConfigureAwait(true);
            AddonUrl = string.Empty;
            AllowLocalHttp = false;
            LegalNoticeAccepted = false;
            UserOwnsAddonConfirmed = false;
            await LoadAddonsAsync().ConfigureAwait(true);
            SelectedAddon = Addons.FirstOrDefault(item => item.Id.Equals(addon.Id, StringComparison.OrdinalIgnoreCase));
            StatusText = string.Format(LocalizationManager.Get("Addons.Status.AddonInstalledFormat"), addon.Name);
            await RefreshAsync(forceRefresh: true).ConfigureAwait(true);
            PortablePreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.Error("Eklenti kurulamadı.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"{LocalizationManager.Get("Addons.Dialog.InstallFailed")}\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveAddonAsync()
    {
        if (SelectedAddon is null)
        {
            StatusText = LocalizationManager.Get("Addons.Status.SelectAddon");
            return;
        }

        var addon = SelectedAddon;
        if (!_dialogs.Confirm(string.Format(LocalizationManager.Get("Addons.Dialog.RemoveFormat"), addon.Name)))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _service.RemoveAddonAsync(addon.Id).ConfigureAwait(true);
            await LoadAddonsAsync().ConfigureAwait(true);
            await RefreshAsync(forceRefresh: true).ConfigureAwait(true);
            PortablePreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.Error("Eklenti kaldırılamadı.", exception);
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleAddonAsync()
    {
        if (SelectedAddon is null)
        {
            StatusText = LocalizationManager.Get("Addons.Status.SelectAddon");
            return;
        }

        try
        {
            IsBusy = true;
            var addonId = SelectedAddon.Id;
            await _service.SetAddonEnabledAsync(addonId, !SelectedAddon.IsEnabled).ConfigureAwait(true);
            await LoadAddonsAsync().ConfigureAwait(true);
            SelectedAddon = Addons.FirstOrDefault(item => item.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase));
            await RefreshAsync(forceRefresh: true).ConfigureAwait(true);
            PortablePreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.Error("Eklenti durumu değiştirilemedi.", exception);
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadItemOptionsAsync(CatalogItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.ContentType is CatalogContentType.Series or CatalogContentType.Anime)
            {
                StatusText = LocalizationManager.Get("Addons.Status.LoadingEpisodes");
                var episodes = await _service.GetEpisodesAsync(item, cancellationToken).ConfigureAwait(true);
                if (cancellationToken.IsCancellationRequested || SelectedItem?.Key != item.Key)
                {
                    return;
                }

                Episodes.Clear();
                foreach (var episode in episodes)
                {
                    Episodes.Add(episode);
                }

                OnPropertyChanged(nameof(HasEpisodes));
                if (Episodes.Count > 0)
                {
                    SelectedEpisode = Episodes[0];
                    return;
                }
            }

            await LoadStreamsAsync(item, null, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("İçerik bölüm bilgileri yüklenemedi.", exception);
            StatusText = LocalizationManager.Get("Addons.Status.StreamFailed");
        }
    }

    private async Task LoadStreamsAsync(
        CatalogItem item,
        CatalogEpisode? episode,
        CancellationToken cancellationToken)
    {
        try
        {
            StatusText = LocalizationManager.Get("Addons.Status.LoadingStreams");

            // Discover uses add-ons as the metadata/catalog layer, while Prowlarr/Torznab
            // connections act as on-demand release providers for the selected title.
            // Do both lookups together so a catalog item can be playable even when its
            // originating add-on does not expose a stream endpoint.
            var addonStreamsTask = _service.GetStreamsAsync(item, episode, cancellationToken);
            var releaseSearchTask = _searchExternalReleases(
                BuildExternalSearchRequest(item, episode),
                cancellationToken);

            await Task.WhenAll(addonStreamsTask, releaseSearchTask).ConfigureAwait(true);
            var addonStreams = await addonStreamsTask.ConfigureAwait(true);
            var externalResult = await releaseSearchTask.ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested || SelectedItem?.Key != item.Key ||
                (episode is not null && SelectedEpisode?.ExternalId != episode.ExternalId))
            {
                return;
            }

            Streams.Clear();
            _externalReleasesByStreamKey.Clear();

            foreach (var stream in addonStreams)
            {
                Streams.Add(stream);
            }

            var externalCount = 0;
            foreach (var release in externalResult.Releases
                         .OrderByDescending(value => value.Seeders ?? -1)
                         .ThenByDescending(value => value.PublishedUtc ?? DateTimeOffset.MinValue))
            {
                var stream = MapExternalReleaseToCatalogStream(release);
                _externalReleasesByStreamKey[stream.Key] = release;
                Streams.Add(stream);
                externalCount++;
            }

            if (externalResult.Warnings.Count > 0)
            {
                WarningsText = string.Join(Environment.NewLine, externalResult.Warnings.Take(4));
            }

            SelectedStream = Streams.FirstOrDefault();
            OnPropertyChanged(nameof(HasStreams));
            StatusText = Streams.Count == 0
                ? LocalizationManager.Get("Addons.Status.NoStreams")
                : string.Format(
                    LocalizationManager.Get("Addons.Status.StreamsReadySourcesFormat"),
                    Streams.Count,
                    addonStreams.Count,
                    externalCount);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("İçerik akış seçenekleri yüklenemedi.", exception);
            StatusText = LocalizationManager.Get("Addons.Status.StreamFailed");
        }
    }

    private static ExternalSearchRequest BuildExternalSearchRequest(CatalogItem item, CatalogEpisode? episode)
    {
        var contentType = item.ContentType switch
        {
            CatalogContentType.Movie => ExternalContentType.Movie,
            CatalogContentType.Series => ExternalContentType.Series,
            CatalogContentType.Anime => ExternalContentType.Anime,
            _ => ExternalContentType.All
        };

        var queryParts = new List<string> { item.Title.Trim() };
        if (episode is not null)
        {
            if (episode.Season is int season && episode.Episode is int episodeNumber)
            {
                queryParts.Add($"S{season:00}E{episodeNumber:00}");
            }
            else if (episode.Episode is int fallbackEpisode)
            {
                queryParts.Add($"E{fallbackEpisode:00}");
            }
        }
        else if (item.Year is int year)
        {
            queryParts.Add(year.ToString());
        }

        return new ExternalSearchRequest(contentType, string.Join(" ", queryParts), 60);
    }

    private static CatalogStream MapExternalReleaseToCatalogStream(ExternalRelease release)
    {
        var key = $"connection:{release.ConnectionId}:{release.Key}";
        var sourceLabel = string.Join(" · ", new[]
        {
            release.ConnectionName,
            release.Indexer,
            release.CategoryLabel
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new CatalogStream(
            key,
            $"connection:{release.ConnectionId}",
            release.ConnectionName,
            CatalogStreamKind.External,
            release.Title,
            sourceLabel,
            release.MagnetUri ?? release.DownloadUrl,
            release.InfoHash,
            null,
            null,
            InferQualityLabel(release.Title),
            release.Seeders,
            release.SizeBytes);
    }

    private static string? InferQualityLabel(string title)
    {
        foreach (var token in new[] { "2160p", "1080p", "720p", "480p" })
        {
            if (title.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
        }

        return null;
    }

    private async Task PlaySelectedStreamAsync()
    {
        if (SelectedStream is null)
        {
            StatusText = LocalizationManager.Get("Addons.Status.SelectStream");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Addons.Status.OpeningStream");
            if (SelectedStream.Kind == CatalogStreamKind.External &&
                _externalReleasesByStreamKey.TryGetValue(SelectedStream.Key, out var release))
            {
                await _openExternalRelease(release).ConfigureAwait(true);
            }
            else
            {
                await _openStream(SelectedStream).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Torrent seçimi iptal edildi.";
        }
        catch (Exception exception)
        {
            _logger.Error("Eklenti akışı açılamadı.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"{LocalizationManager.Get("Addons.Dialog.StreamFailed")}\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReplaceAddons(IReadOnlyList<AddonRegistration> addons)
    {
        var selectedId = SelectedAddon?.Id;
        Addons.Clear();
        foreach (var addon in addons.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Addons.Add(addon);
        }

        SelectedAddon = Addons.FirstOrDefault(item => item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                        ?? Addons.FirstOrDefault();
        OnPropertyChanged(nameof(HasAddons));
        OnPropertyChanged(nameof(SelectedAddonStateText));
    }

    private void ApplyFilters()
    {
        var category = SelectedFilter?.ContentType ?? CatalogContentType.All;
        var query = SearchText.Trim();
        var filtered = _allItems.Where(item =>
            (category == CatalogContentType.All || item.ContentType == category) &&
            (string.IsNullOrWhiteSpace(query) ||
             item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.Genres.Any(genre => genre.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
             item.OriginAddonName.Contains(query, StringComparison.CurrentCultureIgnoreCase)));

        var selectedKey = SelectedItem?.Key;
        Items.Clear();
        foreach (var item in filtered.Take(240))
        {
            Items.Add(item);
        }

        SelectedItem = string.IsNullOrWhiteSpace(selectedKey)
            ? null
            : Items.FirstOrDefault(item => item.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(MovieCount));
        OnPropertyChanged(nameof(SeriesCount));
        OnPropertyChanged(nameof(AnimeCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void RebuildFilters()
    {
        var selectedType = SelectedFilter?.ContentType ?? CatalogContentType.All;
        Filters.Clear();
        Filters.Add(new CatalogFilterOption(CatalogContentType.All, LocalizationManager.Get("Addons.Filter.All")));
        Filters.Add(new CatalogFilterOption(CatalogContentType.Movie, LocalizationManager.Get("Addons.Filter.Movie")));
        Filters.Add(new CatalogFilterOption(CatalogContentType.Series, LocalizationManager.Get("Addons.Filter.Series")));
        Filters.Add(new CatalogFilterOption(CatalogContentType.Anime, LocalizationManager.Get("Addons.Filter.Anime")));
        SelectedFilter = Filters.First(item => item.ContentType == selectedType);
    }

    private static string GetContentTypeLabel(CatalogContentType type) => type switch
    {
        CatalogContentType.Movie => LocalizationManager.Get("Addons.Filter.Movie"),
        CatalogContentType.Series => LocalizationManager.Get("Addons.Filter.Series"),
        CatalogContentType.Anime => LocalizationManager.Get("Addons.Filter.Anime"),
        _ => LocalizationManager.Get("Addons.Filter.All")
    };

    private void OpenAddonCache()
    {
        try
        {
            Directory.CreateDirectory(_paths.AddonCacheDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _paths.AddonCacheDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logger.Error("Eklenti önbellek klasörü açılamadı.", exception);
        }
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        RebuildFilters();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SelectedItemTypeLabel));
        OnPropertyChanged(nameof(SelectedItemMetadata));
        OnPropertyChanged(nameof(SelectedAddonStateText));
        OnPropertyChanged(nameof(TargetCollectionActionText));
        UpdateSelectedCollectionState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
        _service.Dispose();
    }
}

public sealed record CatalogFilterOption(CatalogContentType ContentType, string Label)
{
    public override string ToString() => Label;
}
