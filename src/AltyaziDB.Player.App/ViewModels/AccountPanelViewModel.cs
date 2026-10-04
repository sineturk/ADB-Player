using System.Collections.ObjectModel;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class AccountPanelViewModel : ObservableObject
{
    private readonly ICloudAccountService _service;
    private readonly IMediaMetadataService _metadataService;
    private readonly IAppLogger _logger;
    private CloudAccountSnapshot? _snapshot;
    private CloudAccountCoreSnapshot? _coreSnapshot;
    private CloudDevice? _selectedDevice;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _displayName = string.Empty;
    private string _profileDisplayName = string.Empty;
    private string _statusText = string.Empty;
    private string _internalApiStatusText = string.Empty;
    private bool _rememberMe = true;
    private bool _isBusy;
    private bool _isRegisterMode;
    private int _cloudLibraryCount;
    private int _cloudContinueCount;
    private int _favoriteCount;
    private int _watchlistCount;
    private DateTimeOffset? _lastCloudSyncAt;

    public AccountPanelViewModel(
        ICloudAccountService service,
        IMediaMetadataService metadataService,
        IAppLogger logger)
    {
        _service = service;
        _metadataService = metadataService;
        _logger = logger;
        FavoriteItems = new ObservableCollection<CloudCollectionItem>();
        WatchlistItems = new ObservableCollection<CloudCollectionItem>();

        SignInCommand = new AsyncRelayCommand(SignInAsync, () => IsConfigured && !IsSignedIn);
        SignUpCommand = new AsyncRelayCommand(SignUpAsync, () => IsConfigured && !IsSignedIn);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => IsSignedIn);
        RefreshSessionCommand = new AsyncRelayCommand(RestoreSessionAsync);
        RefreshCloudCoreCommand = new AsyncRelayCommand(
            RefreshCloudCoreAsync,
            () => IsSignedIn && IsCloudCoreConfigured);
        SaveProfileCommand = new AsyncRelayCommand(
            SaveProfileAsync,
            () => IsSignedIn && IsCloudCoreConfigured && !string.IsNullOrWhiteSpace(ProfileDisplayName));
        RemoveDeviceCommand = new AsyncRelayCommand(
            RemoveSelectedDeviceAsync,
            () => IsSignedIn && IsCloudCoreConfigured && SelectedDevice is { IsCurrentDevice: false });
        TestInternalApiCommand = new AsyncRelayCommand(
            TestInternalApiAsync,
            () => IsSignedIn && IsCloudCoreConfigured && !IsBusy);
        ToggleModeCommand = new RelayCommand(() => IsRegisterMode = !IsRegisterMode);

        InternalApiStatusText = LocalizationManager.Get("Account.InternalApi.NotTested");
        StatusText = IsConfigured
            ? LocalizationManager.Get("Account.Status.Ready")
            : LocalizationManager.Get("Account.Status.NotConfigured");

        _ = RestoreSessionAsync();
    }

    public event EventHandler? CloudSessionReady;

    public bool IsConfigured => _service.IsConfigured;
    public bool IsCloudCoreConfigured => _service.IsCloudCoreConfigured;
    public bool IsSignedIn => _snapshot is not null;
    public bool IsCloudSynced => _coreSnapshot is not null;
    public CloudAccountUser? CurrentUser => _snapshot?.User;
    public CloudProfile? CloudProfile => _coreSnapshot?.Profile;
    public CloudDevice? CurrentDevice => _coreSnapshot?.CurrentDevice;
    public IReadOnlyList<CloudDevice> Devices => _coreSnapshot?.Devices ?? Array.Empty<CloudDevice>();
    public ObservableCollection<CloudCollectionItem> FavoriteItems { get; }
    public ObservableCollection<CloudCollectionItem> WatchlistItems { get; }

    public string HeaderText =>
        CloudProfile?.DisplayName
        ?? CurrentUser?.DisplayName
        ?? LocalizationManager.Get("Account.Header.Guest");

    public string AvatarText
    {
        get
        {
            var source = CloudProfile?.DisplayName;
            if (string.IsNullOrWhiteSpace(source)) source = CurrentUser?.DisplayName;
            if (string.IsNullOrWhiteSpace(source)) source = CurrentUser?.Email;
            if (string.IsNullOrWhiteSpace(source)) return "A";
            return source.Trim()[0].ToString().ToUpperInvariant();
        }
    }

    public string EmailText => CurrentUser?.Email ?? Email;
    public string CultureLanguageTag => LocalizationManager.CurrentLanguageCode;
    public string AuthEndpoint => _service.AuthBaseUrl;
    public string ApiEndpoint => _service.ApiBaseUrl;
    public string DeviceCountText => LocalizationManager.Format("Account.Devices.Count", Devices.Count);
    public int CloudLibraryCount => _cloudLibraryCount;
    public int CloudContinueCount => _cloudContinueCount;
    public int FavoriteCount => _favoriteCount;
    public int WatchlistCount => _watchlistCount;
    public string CloudLibraryCountText => LocalizationManager.Format("Account.CloudLibrary.Count", CloudLibraryCount);
    public string CloudContinueCountText => LocalizationManager.Format("Account.CloudLibrary.ContinueCount", CloudContinueCount);
    public string FavoriteCountText => LocalizationManager.Format("Account.CloudCollections.Count", FavoriteCount);
    public string WatchlistCountText => LocalizationManager.Format("Account.CloudCollections.Count", WatchlistCount);
    public string LastCloudSyncText => _lastCloudSyncAt is null
        ? LocalizationManager.Get("Account.CloudLibrary.NotSynced")
        : LocalizationManager.Format("Account.CloudLibrary.LastSync", _lastCloudSyncAt.Value.LocalDateTime);

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value ?? string.Empty);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value ?? string.Empty);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value ?? string.Empty);
    }

    public string ProfileDisplayName
    {
        get => _profileDisplayName;
        set
        {
            if (!SetProperty(ref _profileDisplayName, value ?? string.Empty)) return;
            RaiseCommandStates();
        }
    }

    public CloudDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            RaiseCommandStates();
        }
    }

    public bool RememberMe
    {
        get => _rememberMe;
        set => SetProperty(ref _rememberMe, value);
    }

    public bool IsRegisterMode
    {
        get => _isRegisterMode;
        set
        {
            if (!SetProperty(ref _isRegisterMode, value)) return;
            Password = string.Empty;
            StatusText = LocalizationManager.Get(value ? "Account.Status.RegisterHint" : "Account.Status.SignInHint");
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommandStates();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string InternalApiStatusText
    {
        get => _internalApiStatusText;
        private set => SetProperty(ref _internalApiStatusText, value);
    }

    public ICommand SignInCommand { get; }
    public ICommand SignUpCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand RefreshSessionCommand { get; }
    public ICommand RefreshCloudCoreCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand RemoveDeviceCommand { get; }
    public ICommand TestInternalApiCommand { get; }
    public ICommand ToggleModeCommand { get; }

    public async Task RestoreSessionAsync()
    {
        if (!IsConfigured)
        {
            ApplySnapshot(null);
            ApplyCoreSnapshot(null);
            StatusText = LocalizationManager.Get("Account.Status.NotConfigured");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Account.Status.Restoring");
            var snapshot = await _service.RestoreSessionAsync().ConfigureAwait(true);
            ApplySnapshot(snapshot);

            if (snapshot is null)
            {
                ApplyCoreSnapshot(null);
                StatusText = LocalizationManager.Get("Account.Status.SignInHint");
                return;
            }

            await BootstrapCloudCoreAsync(snapshot).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("ADB Cloud oturumu geri yüklenemedi.", exception);
            ApplySnapshot(null);
            ApplyCoreSnapshot(null);
            StatusText = LocalizationManager.Get("Account.Status.Unavailable");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SignInAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Account.Status.SigningIn");
            var result = await _service.SignInAsync(Email, Password, RememberMe).ConfigureAwait(true);
            if (!result.Success || result.Snapshot is null)
            {
                StatusText = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? LocalizationManager.Get("Account.Status.SignInFailed")
                    : result.ErrorMessage;
                return;
            }

            ApplySnapshot(result.Snapshot);
            Password = string.Empty;
            await BootstrapCloudCoreAsync(result.Snapshot).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SignUpAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Account.Status.Creating");
            var result = await _service.SignUpAsync(DisplayName, Email, Password).ConfigureAwait(true);
            if (!result.Success || result.Snapshot is null)
            {
                StatusText = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? LocalizationManager.Get("Account.Status.SignUpFailed")
                    : result.ErrorMessage;
                return;
            }

            ApplySnapshot(result.Snapshot);
            Password = string.Empty;
            await BootstrapCloudCoreAsync(result.Snapshot).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BootstrapCloudCoreAsync(CloudAccountSnapshot snapshot)
    {
        if (!IsCloudCoreConfigured)
        {
            ApplyCoreSnapshot(null);
            StatusText = LocalizationManager.Get("Account.Status.SignedInCoreUnavailable");
            return;
        }

        StatusText = LocalizationManager.Get("Account.Status.SyncingProfile");
        var result = await _service.BootstrapAccountCoreAsync(
            snapshot,
            LocalizationManager.CurrentLanguageCode).ConfigureAwait(true);

        if (!result.Success || result.Snapshot is null)
        {
            ApplyCoreSnapshot(null);
            StatusText = LocalizationManager.Format(
                "Account.Status.SignedInCloudPending",
                result.ErrorMessage ?? LocalizationManager.Get("Account.Status.Unavailable"));
            return;
        }

        ApplyCoreSnapshot(result.Snapshot);
        await RefreshCollectionSummaryAsync().ConfigureAwait(true);
        StatusText = LocalizationManager.Get("Account.Status.SignedInSynced");
        CloudSessionReady?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshCloudCoreAsync()
    {
        if (!IsSignedIn || !IsCloudCoreConfigured) return;

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Account.Status.SyncingProfile");
            var result = await _service.RefreshAccountCoreAsync().ConfigureAwait(true);
            if (!result.Success || result.Snapshot is null)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Account.Status.Unavailable");
                return;
            }

            ApplyCoreSnapshot(result.Snapshot);
            await RefreshCollectionSummaryAsync().ConfigureAwait(true);
            StatusText = LocalizationManager.Get("Account.Status.SignedInSynced");
            CloudSessionReady?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshCollectionSummaryAsync()
    {
        if (!IsSignedIn || !IsCloudCoreConfigured)
        {
            ApplyCollectionCounts(0, 0);
            ReplaceCollectionItems(FavoriteItems, Array.Empty<CloudCollectionItem>());
            ReplaceCollectionItems(WatchlistItems, Array.Empty<CloudCollectionItem>());
            return;
        }

        try
        {
            var result = await _service.GetCollectionsAsync().ConfigureAwait(true);
            if (!result.Success) return;

            var favorites = result.Collections.FirstOrDefault(item =>
                item.Kind.Equals("favorites", StringComparison.OrdinalIgnoreCase));
            var watchlist = result.Collections.FirstOrDefault(item =>
                item.Kind.Equals("watchlist", StringComparison.OrdinalIgnoreCase));

            ApplyCollectionCounts(favorites?.ItemCount ?? 0, watchlist?.ItemCount ?? 0);

            var favoriteItems = favorites is null
                ? null
                : await _service.GetCollectionItemsAsync(favorites.Id).ConfigureAwait(true);
            var watchlistItems = watchlist is null
                ? null
                : await _service.GetCollectionItemsAsync(watchlist.Id).ConfigureAwait(true);

            if (favoriteItems is { Success: true })
                ReplaceCollectionItems(FavoriteItems, favoriteItems.Items);
            else if (favorites is null)
                ReplaceCollectionItems(FavoriteItems, Array.Empty<CloudCollectionItem>());

            if (watchlistItems is { Success: true })
                ReplaceCollectionItems(WatchlistItems, watchlistItems.Items);
            else if (watchlist is null)
                ReplaceCollectionItems(WatchlistItems, Array.Empty<CloudCollectionItem>());
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud liste özeti yenilenemedi: {exception.Message}");
        }
    }

    private static void ReplaceCollectionItems(
        ObservableCollection<CloudCollectionItem> target,
        IReadOnlyList<CloudCollectionItem> source)
    {
        target.Clear();
        foreach (var item in source.OrderByDescending(item => item.AddedAt))
            target.Add(item);
    }

    private void ApplyCollectionCounts(int favorites, int watchlist)
    {
        _favoriteCount = Math.Max(0, favorites);
        _watchlistCount = Math.Max(0, watchlist);
        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(WatchlistCount));
        OnPropertyChanged(nameof(FavoriteCountText));
        OnPropertyChanged(nameof(WatchlistCountText));
    }

    private async Task SaveProfileAsync()
    {
        if (!IsSignedIn || !IsCloudCoreConfigured) return;

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Account.Status.SavingProfile");
            var result = await _service.UpdateProfileAsync(
                ProfileDisplayName,
                CloudProfile?.AvatarUrl ?? CurrentUser?.ImageUrl,
                LocalizationManager.CurrentLanguageCode).ConfigureAwait(true);

            if (!result.Success || result.Snapshot is null)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Account.Status.ProfileSaveFailed");
                return;
            }

            ApplyCoreSnapshot(result.Snapshot);
            StatusText = LocalizationManager.Get("Account.Status.ProfileSaved");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveSelectedDeviceAsync()
    {
        var device = SelectedDevice;
        if (device is null || device.IsCurrentDevice || !IsCloudCoreConfigured) return;

        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Account.Status.RemovingDevice");
            var result = await _service.RemoveDeviceAsync(device.Id).ConfigureAwait(true);
            if (!result.Success || result.Snapshot is null)
            {
                StatusText = result.ErrorMessage ?? LocalizationManager.Get("Account.Status.DeviceRemoveFailed");
                return;
            }

            ApplyCoreSnapshot(result.Snapshot);
            StatusText = LocalizationManager.Get("Account.Status.DeviceRemoved");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SignOutAsync()
    {
        try
        {
            IsBusy = true;
            await _service.SignOutAsync().ConfigureAwait(true);
            ApplySnapshot(null);
            ApplyCoreSnapshot(null);
            Password = string.Empty;
            InternalApiStatusText = LocalizationManager.Get("Account.InternalApi.NotTested");
            StatusText = LocalizationManager.Get("Account.Status.SignedOut");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestInternalApiAsync()
    {
        if (!IsSignedIn || !IsCloudCoreConfigured)
        {
            InternalApiStatusText = LocalizationManager.Get("Account.InternalApi.RequiresAccount");
            return;
        }

        try
        {
            IsBusy = true;
            InternalApiStatusText = LocalizationManager.Get("Account.InternalApi.Testing");

            var identity = new MediaIdentity(
                "The Invite",
                "movie",
                "The.Invite.2026",
                2026);

            var result = await _metadataService
                .ResolveAsync(identity, "account-diagnostics")
                .ConfigureAwait(true);

            if (!result.Success)
            {
                InternalApiStatusText = LocalizationManager.Format(
                    "Account.InternalApi.Failed",
                    result.Error ?? LocalizationManager.Get("Account.InternalApi.UnknownError"));
                return;
            }

            if (!result.Matched || result.Media is null)
            {
                InternalApiStatusText = LocalizationManager.Get("Account.InternalApi.NoMatch");
                return;
            }

            var media = result.Media;
            var match = result.Match;
            var adbId = media.AdbId is null
                ? "-"
                : media.AdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var confidence = Math.Clamp(match?.Confidence ?? 0, 0, 1);
            var posterState = string.IsNullOrWhiteSpace(media.PosterUrl)
                ? LocalizationManager.Get("Account.InternalApi.PosterMissing")
                : LocalizationManager.Get("Account.InternalApi.PosterReady");

            InternalApiStatusText = LocalizationManager.Format(
                "Account.InternalApi.Success",
                media.Title,
                media.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                match?.Source ?? "unknown",
                match?.Method ?? "unknown",
                confidence,
                adbId,
                result.Subtitles.TurkishCount,
                result.Subtitles.EnglishCount,
                posterState);
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Player Internal API testi başarısız: {exception.Message}");
            InternalApiStatusText = LocalizationManager.Format(
                "Account.InternalApi.Failed",
                exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }


    public void ApplyCloudLibrarySnapshot(IReadOnlyList<CloudWatchProgressEntry> entries)
    {
        _cloudLibraryCount = entries.Count;
        _cloudContinueCount = entries.Count(item => item.CanContinue);
        _lastCloudSyncAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(CloudLibraryCount));
        OnPropertyChanged(nameof(CloudContinueCount));
        OnPropertyChanged(nameof(CloudLibraryCountText));
        OnPropertyChanged(nameof(CloudContinueCountText));
        OnPropertyChanged(nameof(FavoriteCountText));
        OnPropertyChanged(nameof(WatchlistCountText));
        OnPropertyChanged(nameof(LastCloudSyncText));
    }

    private void ApplySnapshot(CloudAccountSnapshot? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot is null)
        {
            _cloudLibraryCount = 0;
            _cloudContinueCount = 0;
            ApplyCollectionCounts(0, 0);
            ReplaceCollectionItems(FavoriteItems, Array.Empty<CloudCollectionItem>());
            ReplaceCollectionItems(WatchlistItems, Array.Empty<CloudCollectionItem>());
            _lastCloudSyncAt = null;
            OnPropertyChanged(nameof(CloudLibraryCount));
            OnPropertyChanged(nameof(CloudContinueCount));
            OnPropertyChanged(nameof(CloudLibraryCountText));
            OnPropertyChanged(nameof(CloudContinueCountText));
            OnPropertyChanged(nameof(LastCloudSyncText));
        }
        OnPropertyChanged(nameof(CurrentUser));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(AvatarText));
        OnPropertyChanged(nameof(EmailText));
        RaiseCommandStates();
    }

    private void ApplyCoreSnapshot(CloudAccountCoreSnapshot? snapshot)
    {
        _coreSnapshot = snapshot;
        ProfileDisplayName = snapshot?.Profile?.DisplayName ?? CurrentUser?.DisplayName ?? string.Empty;
        SelectedDevice = snapshot?.CurrentDevice ?? snapshot?.Devices.FirstOrDefault();

        OnPropertyChanged(nameof(CloudProfile));
        OnPropertyChanged(nameof(CurrentDevice));
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(CloudLibraryCountText));
        OnPropertyChanged(nameof(CloudContinueCountText));
        OnPropertyChanged(nameof(FavoriteCountText));
        OnPropertyChanged(nameof(WatchlistCountText));
        OnPropertyChanged(nameof(LastCloudSyncText));
        OnPropertyChanged(nameof(IsCloudSynced));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(AvatarText));
        OnPropertyChanged(nameof(ApiEndpoint));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (SignInCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SignUpCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SignOutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCloudCoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveDeviceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TestInternalApiCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(CultureLanguageTag));
        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(CloudLibraryCountText));
        OnPropertyChanged(nameof(CloudContinueCountText));
        OnPropertyChanged(nameof(FavoriteCountText));
        OnPropertyChanged(nameof(WatchlistCountText));
        OnPropertyChanged(nameof(LastCloudSyncText));
        if (!IsBusy && string.IsNullOrWhiteSpace(InternalApiStatusText))
            InternalApiStatusText = LocalizationManager.Get("Account.InternalApi.NotTested");

        if (IsSignedIn && IsCloudCoreConfigured && _coreSnapshot is not null)
        {
            _ = SaveLocaleAsync();
        }

        if (!IsBusy)
        {
            StatusText = IsSignedIn
                ? IsCloudSynced
                    ? LocalizationManager.Get("Account.Status.SignedInSynced")
                    : LocalizationManager.Get("Account.Status.SignedInCoreUnavailable")
                : IsConfigured
                    ? LocalizationManager.Get(IsRegisterMode ? "Account.Status.RegisterHint" : "Account.Status.SignInHint")
                    : LocalizationManager.Get("Account.Status.NotConfigured");
        }
    }

    private async Task SaveLocaleAsync()
    {
        try
        {
            var result = await _service.UpdateProfileAsync(
                ProfileDisplayName,
                CloudProfile?.AvatarUrl ?? CurrentUser?.ImageUrl,
                LocalizationManager.CurrentLanguageCode).ConfigureAwait(true);

            if (result.Success && result.Snapshot is not null)
            {
                ApplyCoreSnapshot(result.Snapshot);
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"ADB Cloud dil eşitlemesi tamamlanamadı: {exception.Message}");
        }
    }
}
