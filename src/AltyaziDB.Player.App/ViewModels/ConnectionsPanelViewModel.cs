using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Connections;
using AltyaziDB.Player.Connections.Models;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.ViewModels;

public sealed record ConnectionTypeOption(ExternalConnectionType Type, string Label)
{
    public override string ToString() => Label;
}

public sealed record ExternalContentFilterOption(ExternalContentType ContentType, string Label)
{
    public override string ToString() => Label;
}

public sealed class ConnectionsPanelViewModel : ObservableObject, IDisposable
{
    private readonly IExternalConnectionService _service;
    private readonly Func<ExternalRelease, Task> _openRelease;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;

    private CancellationTokenSource? _operationCancellation;
    private ConnectionProfile? _selectedProfile;
    private ExternalRelease? _selectedRelease;
    private ConnectionTypeOption? _selectedConnectionType;
    private ExternalContentFilterOption? _selectedContentFilter;
    private string? _editingId;
    private string _connectionName = string.Empty;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _movieCategories = "2000";
    private string _seriesCategories = "5000";
    private string _animeCategories = "5070";
    private string _otherCategories = string.Empty;
    private int _resultLimit = 100;
    private bool _allowLocalHttp;
    private bool _legalNoticeAccepted;
    private bool _userOwnsServiceConfirmed;
    private string _searchText = string.Empty;
    private string _statusText;
    private string _warningsText = string.Empty;
    private bool _isBusy;
    private bool _isLoadingSelection;
    private bool _hasLoadedProfilesOnce;
    private bool _disposed;

    public ConnectionsPanelViewModel(
        IExternalConnectionService service,
        Func<ExternalRelease, Task> openRelease,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths)
    {
        _service = service;
        _openRelease = openRelease;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
        _statusText = LocalizationManager.Get("Connections.Status.NoConnections");

        Profiles = new ObservableCollection<ConnectionProfile>();
        Releases = new ObservableCollection<ExternalRelease>();
        ConnectionTypes = new ObservableCollection<ConnectionTypeOption>();
        ContentFilters = new ObservableCollection<ExternalContentFilterOption>();

        NewConnectionCommand = new RelayCommand(StartNewConnection);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        SaveConnectionCommand = new AsyncRelayCommand(SaveConnectionAsync);
        RemoveConnectionCommand = new AsyncRelayCommand(RemoveConnectionAsync);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync);
        RefreshProfilesCommand = new AsyncRelayCommand(LoadProfilesAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        RefreshReleasesCommand = new AsyncRelayCommand(RefreshReleasesAsync);
        PlayReleaseCommand = new AsyncRelayCommand(PlaySelectedReleaseAsync);
        OpenConnectionDataCommand = new RelayCommand(OpenConnectionData);
        UseLocalProwlarrCommand = new RelayCommand(UseLocalProwlarr);

        RebuildOptions();
        StartNewConnection();
        _ = LoadProfilesAsync();
    }

    public ObservableCollection<ConnectionProfile> Profiles { get; }
    public ObservableCollection<ExternalRelease> Releases { get; }
    public ObservableCollection<ConnectionTypeOption> ConnectionTypes { get; }
    public ObservableCollection<ExternalContentFilterOption> ContentFilters { get; }

    public ICommand NewConnectionCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand SaveConnectionCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand ToggleConnectionCommand { get; }
    public ICommand RefreshProfilesCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand RefreshReleasesCommand { get; }
    public ICommand PlayReleaseCommand { get; }
    public ICommand OpenConnectionDataCommand { get; }
    public ICommand UseLocalProwlarrCommand { get; }

    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value) || _isLoadingSelection || value is null)
            {
                OnPropertyChanged(nameof(SelectedProfileStatusText));
                return;
            }

            OnPropertyChanged(nameof(SelectedProfileStatusText));
            _ = LoadProfileConfigurationAsync(value.Id);
        }
    }

    public ExternalRelease? SelectedRelease
    {
        get => _selectedRelease;
        set => SetProperty(ref _selectedRelease, value);
    }

    public ConnectionTypeOption? SelectedConnectionType
    {
        get => _selectedConnectionType;
        set
        {
            if (!SetProperty(ref _selectedConnectionType, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTypeSupportsSearch));
            OnPropertyChanged(nameof(IsProwlarrSelected));
            OnPropertyChanged(nameof(ApiKeyRequirementText));
            OnPropertyChanged(nameof(ConnectionGuideTitle));
            OnPropertyChanged(nameof(ConnectionGuideText));
            OnPropertyChanged(nameof(ConnectionCredentialsText));
            OnPropertyChanged(nameof(BaseUrlHintText));
            if (_editingId is null)
            {
                ApplyTypeDefaults(value?.Type ?? ExternalConnectionType.Prowlarr);
            }
        }
    }

    public ExternalContentFilterOption? SelectedContentFilter
    {
        get => _selectedContentFilter;
        set => SetProperty(ref _selectedContentFilter, value);
    }

    public string ConnectionName
    {
        get => _connectionName;
        set => SetProperty(ref _connectionName, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string MovieCategories
    {
        get => _movieCategories;
        set => SetProperty(ref _movieCategories, value);
    }

    public string SeriesCategories
    {
        get => _seriesCategories;
        set => SetProperty(ref _seriesCategories, value);
    }

    public string AnimeCategories
    {
        get => _animeCategories;
        set => SetProperty(ref _animeCategories, value);
    }

    public string OtherCategories
    {
        get => _otherCategories;
        set => SetProperty(ref _otherCategories, value);
    }

    public int ResultLimit
    {
        get => _resultLimit;
        set => SetProperty(ref _resultLimit, Math.Clamp(value, 1, 300));
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

    public bool UserOwnsServiceConfirmed
    {
        get => _userOwnsServiceConfirmed;
        set => SetProperty(ref _userOwnsServiceConfirmed, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
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

    public bool HasProfiles => Profiles.Count > 0;
    public bool HasReleases => Releases.Count > 0;
    public bool HasSearchConnections => Profiles.Any(profile => profile.IsEnabled && profile.LastTestSucceeded && profile.SupportsReleaseSearch);
    public bool SelectedTypeSupportsSearch => SelectedConnectionType?.Type is ExternalConnectionType.Prowlarr or ExternalConnectionType.Torznab;
    public bool IsProwlarrSelected => SelectedConnectionType?.Type == ExternalConnectionType.Prowlarr;
    public bool IsEditing => _editingId is not null;
    public string FormTitle => LocalizationManager.Get(IsEditing ? "Connections.Form.EditTitle" : "Connections.Form.NewTitle");
    public string ApiKeyRequirementText => SelectedConnectionType?.Type == ExternalConnectionType.Torznab
        ? LocalizationManager.Get("Connections.ApiKey.Optional")
        : LocalizationManager.Get("Connections.ApiKey.Required");

    public string ConnectionGuideTitle => LocalizationManager.Get(SelectedConnectionType?.Type switch
    {
        ExternalConnectionType.Torznab => "Connections.Guide.Torznab.Title",
        ExternalConnectionType.Sonarr => "Connections.Guide.Sonarr.Title",
        ExternalConnectionType.Radarr => "Connections.Guide.Radarr.Title",
        _ => "Connections.Guide.Prowlarr.Title"
    });

    public string ConnectionGuideText => LocalizationManager.Get(SelectedConnectionType?.Type switch
    {
        ExternalConnectionType.Torznab => "Connections.Guide.Torznab.Text",
        ExternalConnectionType.Sonarr => "Connections.Guide.Sonarr.Text",
        ExternalConnectionType.Radarr => "Connections.Guide.Radarr.Text",
        _ => "Connections.Guide.Prowlarr.Text"
    });

    public string ConnectionCredentialsText => LocalizationManager.Get(SelectedConnectionType?.Type switch
    {
        ExternalConnectionType.Torznab => "Connections.Guide.Torznab.Credentials",
        ExternalConnectionType.Sonarr => "Connections.Guide.Sonarr.Credentials",
        ExternalConnectionType.Radarr => "Connections.Guide.Radarr.Credentials",
        _ => "Connections.Guide.Prowlarr.Credentials"
    });

    public string BaseUrlHintText => LocalizationManager.Get(SelectedConnectionType?.Type switch
    {
        ExternalConnectionType.Torznab => "Connections.UrlHint.Torznab",
        ExternalConnectionType.Sonarr => "Connections.UrlHint.Sonarr",
        ExternalConnectionType.Radarr => "Connections.UrlHint.Radarr",
        _ => "Connections.UrlHint.Prowlarr"
    });

    public string SelectedProfileStatusText => SelectedProfile is null
        ? string.Empty
        : SelectedProfile.LastTestSucceeded
            ? string.Format(
                LocalizationManager.Get(SelectedProfile.Type switch
                {
                    ExternalConnectionType.Prowlarr => "Connections.Profile.Connected.Prowlarr",
                    ExternalConnectionType.Torznab => "Connections.Profile.Connected.Torznab",
                    ExternalConnectionType.Sonarr => "Connections.Profile.Connected.Sonarr",
                    ExternalConnectionType.Radarr => "Connections.Profile.Connected.Radarr",
                    _ => "Connections.Profile.ConnectedFormat"
                }),
                SelectedProfile.LastVersion ?? "-",
                SelectedProfile.LastItemCount?.ToString() ?? "-")
            : LocalizationManager.Get("Connections.Profile.NotVerified");
    public string ReleaseSummaryText => string.Format(
        LocalizationManager.Get("Connections.Release.SummaryFormat"),
        Releases.Count,
        Profiles.Count(profile => profile.IsEnabled && profile.LastTestSucceeded && profile.SupportsReleaseSearch));

    public async Task EnsureLoadedAsync()
    {
        if (Profiles.Count == 0 && !IsBusy)
        {
            await LoadProfilesAsync().ConfigureAwait(true);
        }
    }

    public Task<string?> ResolveMagnetAsync(ExternalRelease release) =>
        _service.ResolveMagnetAsync(release);

    public Task<string> DownloadTorrentAsync(ExternalRelease release) =>
        _service.DownloadTorrentAsync(release, _paths.ConnectionCacheDirectory);

    private async Task LoadProfilesAsync()
    {
        try
        {
            IsBusy = true;
            var isInitialLoad = !_hasLoadedProfilesOnce;
            var selectedId = SelectedProfile?.Id ?? _editingId;
            var profiles = await _service.GetProfilesAsync().ConfigureAwait(true);
            ConnectionProfile? initialProfileToLoad = null;

            _isLoadingSelection = true;
            try
            {
                Profiles.Clear();
                foreach (var profile in profiles)
                {
                    Profiles.Add(profile);
                }

                var targetProfile = string.IsNullOrWhiteSpace(selectedId)
                    ? (isInitialLoad ? Profiles.FirstOrDefault() : null)
                    : Profiles.FirstOrDefault(item => item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));

                SelectedProfile = targetProfile;
                if (isInitialLoad && string.IsNullOrWhiteSpace(selectedId))
                {
                    initialProfileToLoad = targetProfile;
                }
            }
            finally
            {
                _isLoadingSelection = false;
            }

            _hasLoadedProfilesOnce = true;
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(HasSearchConnections));
            OnPropertyChanged(nameof(ReleaseSummaryText));
            OnPropertyChanged(nameof(SelectedProfileStatusText));
            StatusText = Profiles.Count == 0
                ? LocalizationManager.Get("Connections.Status.NoConnections")
                : string.Format(LocalizationManager.Get("Connections.Status.ReadyFormat"), Profiles.Count);

            if (initialProfileToLoad is not null)
            {
                await LoadProfileConfigurationAsync(initialProfileToLoad.Id).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Harici bağlantılar yüklenemedi.", exception);
            StatusText = LocalizationManager.Get("Connections.Status.LoadFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadProfileConfigurationAsync(string profileId)
    {
        try
        {
            IsBusy = true;
            var configuration = await _service.GetConfigurationAsync(profileId).ConfigureAwait(true);
            if (configuration is null || SelectedProfile?.Id != profileId)
            {
                return;
            }

            _editingId = configuration.Id;
            ConnectionName = configuration.Name;
            SelectedConnectionType = ConnectionTypes.FirstOrDefault(item => item.Type == configuration.Type);
            BaseUrl = configuration.BaseUrl;
            ApiKey = string.Empty;
            MovieCategories = configuration.MovieCategories;
            SeriesCategories = configuration.SeriesCategories;
            AnimeCategories = configuration.AnimeCategories;
            OtherCategories = configuration.OtherCategories;
            ResultLimit = configuration.ResultLimit;
            AllowLocalHttp = configuration.AllowLocalHttp;
            LegalNoticeAccepted = true;
            UserOwnsServiceConfirmed = true;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(FormTitle));
            StatusText = configuration.HasStoredApiKey
                ? LocalizationManager.Get("Connections.Status.EditLoadedWithSecret")
                : LocalizationManager.Get("Connections.Status.EditLoaded");
        }
        catch (Exception exception)
        {
            _logger.Error("Bağlantı ayarları yüklenemedi.", exception);
            StatusText = LocalizationManager.Get("Connections.Status.LoadFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartNewConnection()
    {
        _editingId = null;
        _isLoadingSelection = true;
        try
        {
            SelectedProfile = null;
        }
        finally
        {
            _isLoadingSelection = false;
        }

        ConnectionName = string.Empty;
        SelectedConnectionType = ConnectionTypes.FirstOrDefault(item => item.Type == ExternalConnectionType.Prowlarr);
        BaseUrl = "http://localhost:9696";
        ApiKey = string.Empty;
        MovieCategories = "2000";
        SeriesCategories = "5000";
        AnimeCategories = "5070";
        OtherCategories = string.Empty;
        ResultLimit = 100;
        AllowLocalHttp = true;
        LegalNoticeAccepted = false;
        UserOwnsServiceConfirmed = false;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SelectedProfileStatusText));
        StatusText = LocalizationManager.Get("Connections.Status.EnterDetails");
    }

    private void ApplyTypeDefaults(ExternalConnectionType type)
    {
        MovieCategories = "2000";
        SeriesCategories = "5000";
        AnimeCategories = "5070";
        OtherCategories = string.Empty;
        ResultLimit = 100;
        if (string.IsNullOrWhiteSpace(ConnectionName))
        {
            ConnectionName = type.ToString();
        }

        if (type == ExternalConnectionType.Prowlarr && string.IsNullOrWhiteSpace(BaseUrl))
        {
            UseLocalProwlarr();
        }
        else if (type != ExternalConnectionType.Prowlarr &&
                 BaseUrl.Equals("http://localhost:9696", StringComparison.OrdinalIgnoreCase))
        {
            BaseUrl = string.Empty;
            AllowLocalHttp = false;
        }
    }

    private void UseLocalProwlarr()
    {
        BaseUrl = "http://localhost:9696";
        AllowLocalHttp = true;
        if (string.IsNullOrWhiteSpace(ConnectionName))
        {
            ConnectionName = "Prowlarr";
        }
    }

    private SaveConnectionRequest CreateRequest()
    {
        var type = SelectedConnectionType?.Type ?? ExternalConnectionType.Prowlarr;
        var baseUrl = BaseUrl;
        var allowLocalHttp = AllowLocalHttp;
        if (type == ExternalConnectionType.Prowlarr && string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://localhost:9696";
            allowLocalHttp = true;
            BaseUrl = baseUrl;
            AllowLocalHttp = true;
        }

        return new SaveConnectionRequest(
            _editingId,
            ConnectionName,
            type,
            baseUrl,
            ApiKey,
            MovieCategories,
            SeriesCategories,
            AnimeCategories,
            OtherCategories,
            ResultLimit,
            allowLocalHttp,
            LegalNoticeAccepted,
            UserOwnsServiceConfirmed);
    }

    private async Task TestConnectionAsync()
    {
        CancelCurrentOperation();
        var cancellationToken = _operationCancellation!.Token;
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Connections.Status.Testing");
            var result = await _service.TestDraftAsync(CreateRequest(), cancellationToken).ConfigureAwait(true);
            StatusText = result.Message;
            if (!result.IsSuccess)
            {
                _dialogs.ShowError(result.Message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveConnectionAsync()
    {
        CancelCurrentOperation();
        var cancellationToken = _operationCancellation!.Token;
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Connections.Status.Saving");
            var profile = await _service.SaveAsync(CreateRequest(), cancellationToken).ConfigureAwait(true);
            _editingId = profile.Id;
            ApiKey = string.Empty;
            await LoadProfilesAsync().ConfigureAwait(true);
            _isLoadingSelection = true;
            try
            {
                SelectedProfile = Profiles.FirstOrDefault(item => item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _isLoadingSelection = false;
            }

            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(FormTitle));
            StatusText = string.Format(LocalizationManager.Get("Connections.Status.SavedFormat"), profile.Name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Harici bağlantı kaydedilemedi.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveConnectionAsync()
    {
        if (SelectedProfile is null)
        {
            StatusText = LocalizationManager.Get("Connections.Status.SelectConnection");
            return;
        }

        var profile = SelectedProfile;
        if (!_dialogs.Confirm(string.Format(LocalizationManager.Get("Connections.Dialog.RemoveFormat"), profile.Name)))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _service.RemoveAsync(profile.Id).ConfigureAwait(true);
            Releases.Clear();
            SelectedRelease = null;
            OnPropertyChanged(nameof(HasReleases));
            OnPropertyChanged(nameof(ReleaseSummaryText));
            StartNewConnection();
            await LoadProfilesAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Harici bağlantı kaldırılamadı.", exception);
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleConnectionAsync()
    {
        if (SelectedProfile is null)
        {
            StatusText = LocalizationManager.Get("Connections.Status.SelectConnection");
            return;
        }

        try
        {
            IsBusy = true;
            var id = SelectedProfile.Id;
            await _service.SetEnabledAsync(id, !SelectedProfile.IsEnabled).ConfigureAwait(true);
            await LoadProfilesAsync().ConfigureAwait(true);
            _isLoadingSelection = true;
            try
            {
                SelectedProfile = Profiles.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _isLoadingSelection = false;
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Harici bağlantı durumu değiştirilemedi.", exception);
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task RefreshReleasesAsync()
    {
        SearchText = string.Empty;
        return SearchAsync();
    }

    private async Task SearchAsync()
    {
        CancelCurrentOperation();
        var cancellationToken = _operationCancellation!.Token;
        try
        {
            IsBusy = true;
            WarningsText = string.Empty;
            StatusText = LocalizationManager.Get("Connections.Status.Searching");
            var request = new ExternalSearchRequest(
                SelectedContentFilter?.ContentType ?? ExternalContentType.All,
                SearchText.Trim(),
                300);
            var result = await _service.SearchAsync(request, cancellationToken).ConfigureAwait(true);
            Releases.Clear();
            foreach (var release in result.Releases)
            {
                Releases.Add(release);
            }

            SelectedRelease = Releases.FirstOrDefault();
            WarningsText = string.Join(Environment.NewLine, result.Warnings.Take(5));
            OnPropertyChanged(nameof(HasReleases));
            OnPropertyChanged(nameof(ReleaseSummaryText));
            StatusText = Releases.Count == 0
                ? LocalizationManager.Get("Connections.Status.NoResults")
                : string.Format(LocalizationManager.Get("Connections.Status.ResultsFormat"), Releases.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Harici kaynak araması başarısız oldu.", exception);
            StatusText = LocalizationManager.Get("Connections.Status.SearchFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PlaySelectedReleaseAsync()
    {
        if (SelectedRelease is null)
        {
            StatusText = LocalizationManager.Get("Connections.Status.SelectRelease");
            return;
        }

        if (!_dialogs.Confirm(LocalizationManager.Get("Connections.Dialog.OpenRelease")))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Connections.Status.OpeningRelease");
            await _openRelease(SelectedRelease).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Harici bağlantı sonucu açılamadı.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenConnectionData()
    {
        try
        {
            Directory.CreateDirectory(_paths.ConnectionCacheDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _paths.ConnectionCacheDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logger.Error("Bağlantı veri klasörü açılamadı.", exception);
            StatusText = LocalizationManager.Get("Connections.Status.FolderFailed");
        }
    }

    private void CancelCurrentOperation()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
    }

    private void RebuildOptions()
    {
        var selectedType = SelectedConnectionType?.Type ?? ExternalConnectionType.Prowlarr;
        var selectedFilter = SelectedContentFilter?.ContentType ?? ExternalContentType.All;

        ConnectionTypes.Clear();
        ConnectionTypes.Add(new ConnectionTypeOption(ExternalConnectionType.Prowlarr, LocalizationManager.Get("Connections.Type.Prowlarr")));
        ConnectionTypes.Add(new ConnectionTypeOption(ExternalConnectionType.Torznab, LocalizationManager.Get("Connections.Type.Torznab")));
        ConnectionTypes.Add(new ConnectionTypeOption(ExternalConnectionType.Sonarr, LocalizationManager.Get("Connections.Type.Sonarr")));
        ConnectionTypes.Add(new ConnectionTypeOption(ExternalConnectionType.Radarr, LocalizationManager.Get("Connections.Type.Radarr")));
        _selectedConnectionType = ConnectionTypes.First(item => item.Type == selectedType);
        OnPropertyChanged(nameof(SelectedConnectionType));

        ContentFilters.Clear();
        ContentFilters.Add(new ExternalContentFilterOption(ExternalContentType.All, LocalizationManager.Get("Connections.Filter.All")));
        ContentFilters.Add(new ExternalContentFilterOption(ExternalContentType.Movie, LocalizationManager.Get("Connections.Filter.Movie")));
        ContentFilters.Add(new ExternalContentFilterOption(ExternalContentType.Series, LocalizationManager.Get("Connections.Filter.Series")));
        ContentFilters.Add(new ExternalContentFilterOption(ExternalContentType.Anime, LocalizationManager.Get("Connections.Filter.Anime")));
        ContentFilters.Add(new ExternalContentFilterOption(ExternalContentType.Other, LocalizationManager.Get("Connections.Filter.Other")));
        _selectedContentFilter = ContentFilters.First(item => item.ContentType == selectedFilter);
        OnPropertyChanged(nameof(SelectedContentFilter));
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        RebuildOptions();
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(ApiKeyRequirementText));
        OnPropertyChanged(nameof(ConnectionGuideTitle));
        OnPropertyChanged(nameof(ConnectionGuideText));
        OnPropertyChanged(nameof(ConnectionCredentialsText));
        OnPropertyChanged(nameof(BaseUrlHintText));
        OnPropertyChanged(nameof(SelectedProfileStatusText));
        OnPropertyChanged(nameof(ReleaseSummaryText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _service.Dispose();
    }
}
