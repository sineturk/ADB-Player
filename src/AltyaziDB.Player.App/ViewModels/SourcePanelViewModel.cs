using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class SourcePanelViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".truehd",
        ".flac", ".mka", ".m4a", ".mp3", ".ogg", ".opus", ".wav"
    };
    private readonly IRemoteSourceService _service;
    private readonly IRcloneCloudService _cloudService;
    private readonly ISecretStore _secretStore;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly Func<RemoteOpenRequest, Task> _openRemote;
    private readonly Action<string> _attachSubtitle;
    private readonly Func<RemoteOpenRequest, Task> _attachRemoteAudio;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;

    private string _publicLinkUrl = string.Empty;
    private string _publicLinkName = string.Empty;
    private SavedPublicLink? _selectedSavedLink;
    private SavedWebDavProfile? _selectedProfile;
    private string _webDavName = "WebDAV";
    private string _webDavUrl = string.Empty;
    private string _webDavUsername = string.Empty;
    private string _webDavPassword = string.Empty;
    private RemoteSourceItem? _selectedItem;
    private RemoteBrowseResult? _browse;
    private WebDavConnection? _currentWebDavConnection;
    private SavedCloudAccount? _currentCloudAccount;
    private RcloneCloudProviderOption? _selectedCloudProvider;
    private SavedCloudAccount? _selectedCloudAccount;
    private string _cloudAccountName = "Google Drive";
    private string _cloudConnectErrorText = string.Empty;
    private bool _hasCloudConnectError;
    private CancellationTokenSource? _activeOAuthCancellation;
    private Uri? _pendingOAuthAuthorizationUri;
    private bool _canOpenOAuthBrowser;
    private string _oauthBrowserStatusText = string.Empty;
    private string _rcloneStatusText = "rclone denetleniyor…";
    private bool _rcloneAvailable;
    private string _statusText = "Bağlantı yapıştırın veya bir WebDAV profili oluşturun.";
    private bool _isBusy;
    private bool _isBrowserVisible;
    private bool _isAudioSelectionMode;

    public SourcePanelViewModel(
        IRemoteSourceService service,
        IRcloneCloudService cloudService,
        ISecretStore secretStore,
        PlayerSettings settings,
        Func<Task> saveSettings,
        Func<RemoteOpenRequest, Task> openRemote,
        Action<string> attachSubtitle,
        Func<RemoteOpenRequest, Task> attachRemoteAudio,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths)
    {
        _service = service;
        _cloudService = cloudService;
        _secretStore = secretStore;
        _settings = settings;
        _saveSettings = saveSettings;
        _openRemote = openRemote;
        _attachSubtitle = attachSubtitle;
        _attachRemoteAudio = attachRemoteAudio;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;

        Items = new ObservableCollection<RemoteSourceItem>();
        CloudProviders = new ObservableCollection<RcloneCloudProviderOption>
        {
            new(RcloneCloudProvider.GoogleDrive, "Google Drive", "Salt okunur Drive erişimi"),
            new(RcloneCloudProvider.Dropbox, "Dropbox", "Dropbox hesabındaki dosyalar"),
            new(RcloneCloudProvider.OneDrive, "OneDrive", "Kişisel veya iş OneDrive hesabı"),
            new(RcloneCloudProvider.PCloud, "pCloud ABD", "ABD bölgesindeki pCloud hesabı"),
            new(RcloneCloudProvider.PCloudEurope, "pCloud Avrupa", "Avrupa bölgesindeki pCloud hesabı")
        };
        SelectedCloudProvider = CloudProviders[0];
        CloudAccounts = new ObservableCollection<SavedCloudAccount>(
            (_settings.CloudAccounts ?? []).OrderByDescending(item => item.LastUsedUtc));
        WebDavProfiles = new ObservableCollection<SavedWebDavProfile>(_settings.WebDavProfiles ?? []);
        SavedPublicLinks = new ObservableCollection<SavedPublicLink>(
            (_settings.SavedPublicLinks ?? []).OrderByDescending(item => item.LastOpenedUtc));
        SelectedCloudAccount = CloudAccounts.FirstOrDefault();
        SelectedSavedLink = SavedPublicLinks.FirstOrDefault();
        SelectedProfile = WebDavProfiles.FirstOrDefault();

        OpenPublicLinkCommand = new AsyncRelayCommand(OpenPublicLinkAsync);
        SavePublicLinkCommand = new AsyncRelayCommand(SavePublicLinkAsync);
        OpenSavedLinkCommand = new AsyncRelayCommand(OpenSavedLinkAsync);
        DeleteSavedLinkCommand = new AsyncRelayCommand(DeleteSavedLinkAsync);
        ConnectWebDavCommand = new AsyncRelayCommand(ConnectWebDavAsync);
        ConnectCloudCommand = new AsyncRelayCommand(ConnectCloudAsync);
        OpenCloudCommand = new AsyncRelayCommand(OpenCloudAsync);
        DisconnectCloudCommand = new AsyncRelayCommand(DisconnectCloudAsync);
        SaveWebDavProfileCommand = new AsyncRelayCommand(SaveWebDavProfileAsync);
        DeleteWebDavProfileCommand = new AsyncRelayCommand(DeleteWebDavProfileAsync);
        OpenSelectedCommand = new AsyncRelayCommand(OpenSelectedAsync);
        AttachSelectedSubtitleCommand = new AsyncRelayCommand(AttachSelectedSubtitleAsync);
        AttachSelectedAudioCommand = new AsyncRelayCommand(AttachSelectedAudioAsync);
        BackCommand = new AsyncRelayCommand(GoBackAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ShowConnectionsHomeCommand = new RelayCommand(ShowConnectionsHome);
        _ = RefreshRcloneStatusAsync();
    }

    public ObservableCollection<RemoteSourceItem> Items { get; }
    public ObservableCollection<RcloneCloudProviderOption> CloudProviders { get; }
    public ObservableCollection<SavedCloudAccount> CloudAccounts { get; }
    public ObservableCollection<SavedWebDavProfile> WebDavProfiles { get; }
    public ObservableCollection<SavedPublicLink> SavedPublicLinks { get; }

    public ICommand OpenPublicLinkCommand { get; }
    public ICommand SavePublicLinkCommand { get; }
    public ICommand OpenSavedLinkCommand { get; }
    public ICommand DeleteSavedLinkCommand { get; }
    public ICommand ConnectWebDavCommand { get; }
    public ICommand ConnectCloudCommand { get; }
    public ICommand OpenCloudCommand { get; }
    public ICommand DisconnectCloudCommand { get; }
    public ICommand SaveWebDavProfileCommand { get; }
    public ICommand DeleteWebDavProfileCommand { get; }
    public ICommand OpenSelectedCommand { get; }
    public ICommand AttachSelectedSubtitleCommand { get; }
    public ICommand AttachSelectedAudioCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ShowConnectionsHomeCommand { get; }

    public RcloneCloudProviderOption? SelectedCloudProvider
    {
        get => _selectedCloudProvider;
        set
        {
            if (!SetProperty(ref _selectedCloudProvider, value) || value is null) return;
            if (string.IsNullOrWhiteSpace(CloudAccountName) || CloudProviders.Any(item => item.Label.Equals(CloudAccountName, StringComparison.CurrentCultureIgnoreCase)))
                CloudAccountName = value.Label;
        }
    }
    public SavedCloudAccount? SelectedCloudAccount { get => _selectedCloudAccount; set => SetProperty(ref _selectedCloudAccount, value); }
    public string CloudAccountName { get => _cloudAccountName; set => SetProperty(ref _cloudAccountName, value); }
    public string CloudConnectErrorText { get => _cloudConnectErrorText; private set => SetProperty(ref _cloudConnectErrorText, value); }
    public bool HasCloudConnectError { get => _hasCloudConnectError; private set => SetProperty(ref _hasCloudConnectError, value); }
    public bool CanOpenOAuthBrowser { get => _canOpenOAuthBrowser; private set => SetProperty(ref _canOpenOAuthBrowser, value); }
    public string OAuthBrowserStatusText { get => _oauthBrowserStatusText; private set => SetProperty(ref _oauthBrowserStatusText, value); }
    public string RcloneStatusText { get => _rcloneStatusText; private set => SetProperty(ref _rcloneStatusText, value); }
    public bool RcloneAvailable { get => _rcloneAvailable; private set { if (SetProperty(ref _rcloneAvailable, value)) OnPropertyChanged(nameof(CloudAvailabilityText)); } }
    public string CloudAvailabilityText => LocalizationManager.Get(RcloneAvailable ? "Panel.CloudReady" : "Panel.CloudUnavailable");

    public string PublicLinkUrl { get => _publicLinkUrl; set => SetProperty(ref _publicLinkUrl, value); }
    public string PublicLinkName { get => _publicLinkName; set => SetProperty(ref _publicLinkName, value); }
    public SavedPublicLink? SelectedSavedLink { get => _selectedSavedLink; set => SetProperty(ref _selectedSavedLink, value); }

    public SavedWebDavProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value) || value is null) return;
            WebDavName = value.Name;
            WebDavUrl = value.BaseUrl;
            WebDavUsername = value.Username;
            _ = LoadProfilePasswordAsync(value);
        }
    }

    public string WebDavName { get => _webDavName; set => SetProperty(ref _webDavName, value); }
    public string WebDavUrl { get => _webDavUrl; set => SetProperty(ref _webDavUrl, value); }
    public string WebDavUsername { get => _webDavUsername; set => SetProperty(ref _webDavUsername, value); }
    public string WebDavPassword { get => _webDavPassword; set => SetProperty(ref _webDavPassword, value); }
    public RemoteSourceItem? SelectedItem { get => _selectedItem; set => SetProperty(ref _selectedItem, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool IsBrowserVisible { get => _isBrowserVisible; private set => SetProperty(ref _isBrowserVisible, value); }
    public bool IsAudioSelectionMode
    {
        get => _isAudioSelectionMode;
        private set
        {
            if (!SetProperty(ref _isAudioSelectionMode, value)) return;
            OnPropertyChanged(nameof(BrowserActionText));
        }
    }
    public string BrowserActionText => LocalizationManager.Get(IsAudioSelectionMode ? "Player.AudioSelection.Attach" : "Action.Open");
    public string BrowserTitle => _browse is null ? "Uzak dosyalar" : $"{_browse.Provider} · {_browse.DisplayName}";
    public bool CanGoBack => !string.IsNullOrWhiteSpace(_browse?.ParentPath);

    private async Task OpenPublicLinkAsync()
    {
        if (string.IsNullOrWhiteSpace(PublicLinkUrl))
        {
            StatusText = "Önce bir bağlantı girin.";
            return;
        }

        var url = PublicLinkUrl.Trim();
        var result = await ResolvePublicLinkAsync(url).ConfigureAwait(true);
        if (result is not null)
        {
            await UpsertPublicLinkAsync(url, PublicLinkName, result).ConfigureAwait(true);
        }
    }

    public Task OpenUrlAsync(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        PublicLinkUrl = url.Trim();
        PublicLinkName = string.Empty;
        return OpenPublicLinkAsync();
    }

    private async Task<RemoteBrowseResult?> ResolvePublicLinkAsync(string url)
    {
        try
        {
            IsBusy = true;
            StatusText = "Bağlantı çözümleniyor…";
            var result = await _service.ResolvePublicLinkAsync(url).ConfigureAwait(true);
            await ApplyBrowseResultAsync(result).ConfigureAwait(true);
            return result;
        }
        catch (Exception exception)
        {
            _logger.Error("Uzak bağlantı açılamadı.", exception);
            StatusText = "Bağlantı açılamadı.";
            _dialogs.ShowError($"Bağlantı açılamadı.\n\n{exception.Message}");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyBrowseResultAsync(RemoteBrowseResult result)
    {
        _browse = result;
        if (result.Provider == RemoteSourceProvider.Rclone && _currentCloudAccount is not null)
        {
            _currentCloudAccount.LastBrowsePath = result.CurrentPath ?? string.Empty;
            _currentCloudAccount.LastUsedUtc = DateTimeOffset.UtcNow;
            _settings.CloudAccounts = CloudAccounts.OrderByDescending(item => item.LastUsedUtc).ToList();
            await _saveSettings().ConfigureAwait(true);
        }
        OnPropertyChanged(nameof(BrowserTitle));
        OnPropertyChanged(nameof(CanGoBack));
        Items.Clear();
        foreach (var item in result.Items) Items.Add(item);
        SelectedItem = Items.FirstOrDefault();
        StatusText = result.StatusMessage ?? $"{Items.Count} öğe bulundu.";
        if (result.DirectOpen is not null)
        {
            if (IsAudioSelectionMode)
            {
                await AttachDirectAudioAsync(result.DirectOpen, result.Provider).ConfigureAwait(true);
                return;
            }

            await _openRemote(result.DirectOpen).ConfigureAwait(true);
            StatusText = $"Açılıyor: {result.DirectOpen.DisplayName}";
            return;
        }
        IsBrowserVisible = true;
    }

    private async Task AttachDirectAudioAsync(RemoteOpenRequest request, RemoteSourceProvider provider)
    {
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? Path.GetFileName(new Uri(request.Source).AbsolutePath)
            : request.DisplayName;
        var extension = Path.GetExtension(displayName);
        if (string.IsNullOrWhiteSpace(extension) && Uri.TryCreate(request.Source, UriKind.Absolute, out var sourceUri))
            extension = Path.GetExtension(sourceUri.AbsolutePath);

        if (ArchiveAudioHelper.IsSupportedArchive(displayName) ||
            ArchiveAudioHelper.IsSupportedArchive("audio" + extension))
        {
            StatusText = "Ses arşivi indiriliyor…";
            var archiveItem = new RemoteSourceItem(
                request.Source,
                string.IsNullOrWhiteSpace(displayName) ? "harici-ses.zip" : displayName,
                RemoteSourceItemKind.Archive,
                request.Source,
                null,
                null,
                null,
                request.HttpHeaders,
                provider.ToString());
            var archivePath = await DownloadItemToCacheAsync(archiveItem).ConfigureAwait(true);
            var extracted = await ArchiveAudioHelper.ExtractAudioAsync(
                archivePath,
                Path.Combine(_paths.RemoteCacheDirectory, "audio-extracted"),
                _dialogs).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                StatusText = "Arşivden ses seçimi iptal edildi.";
                return;
            }
            await _attachRemoteAudio(new RemoteOpenRequest(extracted, Path.GetFileName(extracted), Provider: "archive-audio")).ConfigureAwait(true);
            IsAudioSelectionMode = false;
            StatusText = $"Arşivden harici ses eklendi: {Path.GetFileName(extracted)}";
            return;
        }

        if (!AudioExtensions.Contains(extension))
            throw new InvalidDataException("Bağlantı desteklenen bir ses dosyasını göstermiyor.");

        await _attachRemoteAudio(request).ConfigureAwait(true);
        IsAudioSelectionMode = false;
        StatusText = $"Harici ses akışı eklendi: {displayName}";
    }

    private async Task SavePublicLinkAsync()
    {
        var url = PublicLinkUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            StatusText = "Kaydetmek için geçerli bir bağlantı girin.";
            return;
        }

        await UpsertPublicLinkAsync(url, PublicLinkName, null).ConfigureAwait(true);
        StatusText = "Bağlantı kaydedildi.";
    }

    private async Task<SavedPublicLink> UpsertPublicLinkAsync(string url, string? displayName, RemoteBrowseResult? result)
    {
        var resolvedProvider = result?.DirectOpen?.Provider ?? result?.Provider.ToString() ?? string.Empty;
        var resolvedName = result is not null && HasResolvedMediaName(result)
            ? result.DisplayName
            : string.Empty;
        var name = string.IsNullOrWhiteSpace(displayName)
            ? string.IsNullOrWhiteSpace(resolvedName) ? HostLabel(url) : resolvedName
            : displayName.Trim();
        var existing = SavedPublicLinks.FirstOrDefault(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) SavedPublicLinks.Remove(existing);
        var entry = existing ?? new SavedPublicLink { Url = url };
        entry.Name = name;
        if (!string.IsNullOrWhiteSpace(resolvedProvider)) entry.Provider = resolvedProvider;
        if (result is not null && IsResolvedWebProvider(result.Provider))
            entry.Detail = "WebLink.StreamReady";
        entry.LastOpenedUtc = DateTimeOffset.UtcNow;
        SavedPublicLinks.Insert(0, entry);
        SelectedSavedLink = entry;
        await PersistSavedLinksAsync().ConfigureAwait(true);
        return entry;
    }

    private async Task OpenSavedLinkAsync()
    {
        if (SelectedSavedLink is null)
        {
            StatusText = "Önce kayıtlı bir bağlantı seçin.";
            return;
        }
        await OpenSavedLinkAsync(SelectedSavedLink).ConfigureAwait(true);
    }

    public async Task OpenSavedLinkAsync(SavedPublicLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        SelectedSavedLink = link;
        PublicLinkUrl = link.Url;
        PublicLinkName = link.Name;
        link.LastOpenedUtc = DateTimeOffset.UtcNow;
        await PersistSavedLinksAsync().ConfigureAwait(true);
        var result = await ResolvePublicLinkAsync(link.Url).ConfigureAwait(true);
        if (result is not null)
        {
            link.Provider = result.DirectOpen?.Provider ?? result.Provider.ToString();
            if (HasResolvedMediaName(result))
            {
                if (string.IsNullOrWhiteSpace(link.Name) || link.Name.Equals(HostLabel(link.Url), StringComparison.OrdinalIgnoreCase))
                    link.Name = result.DisplayName;
                if (IsResolvedWebProvider(result.Provider))
                    link.Detail = "WebLink.StreamReady";
            }
            await PersistSavedLinksAsync().ConfigureAwait(true);
        }
    }

    private static bool HasResolvedMediaName(RemoteBrowseResult result) =>
        IsResolvedWebProvider(result.Provider) ||
        (result.Provider == RemoteSourceProvider.GoogleDrive &&
         !string.IsNullOrWhiteSpace(result.DisplayName) &&
         !result.DisplayName.Equals("Google Drive dosyası", StringComparison.OrdinalIgnoreCase));

    private static bool IsResolvedWebProvider(RemoteSourceProvider provider) => provider is
        RemoteSourceProvider.GdFlix or
        RemoteSourceProvider.HubCloud or
        RemoteSourceProvider.VidMoly or
        RemoteSourceProvider.OkRu or
        RemoteSourceProvider.Vk or
        RemoteSourceProvider.PixelDrain;

    private async Task DeleteSavedLinkAsync()
    {
        if (SelectedSavedLink is null) return;
        await DeleteSavedLinkAsync(SelectedSavedLink).ConfigureAwait(true);
    }

    public async Task DeleteSavedLinkAsync(SavedPublicLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!_dialogs.Confirm($"{link.Name} bağlantısı kaldırılsın mı?")) return;
        SavedPublicLinks.Remove(link);
        SelectedSavedLink = SavedPublicLinks.FirstOrDefault();
        await PersistSavedLinksAsync().ConfigureAwait(true);
        StatusText = "Kayıtlı bağlantı silindi.";
    }

    private async Task RefreshRcloneStatusAsync()
    {
        try
        {
            var status = await _cloudService.GetStatusAsync().ConfigureAwait(true);
            RcloneAvailable = status.IsAvailable;
            RcloneStatusText = status.Diagnostic;
            _logger.Info($"rclone durumu: {status.Diagnostic}");
        }
        catch (Exception exception)
        {
            RcloneAvailable = false;
            RcloneStatusText = exception.Message;
        }
    }

    /// <summary>
    /// Show only whether this provider has user-owned credentials. Never copy
    /// Client Secret into the source sheet or display it in diagnostics.
    /// </summary>
    public async Task<bool> HasSavedCloudCredentialsAsync(RcloneCloudProvider provider)
    {
        var prefix = CloudCredentialPrefix(provider);
        if (prefix.Length == 0) return false;
        try
        {
            var id = await _secretStore.GetAsync($"cloud:{prefix}-client-id").ConfigureAwait(true);
            var secret = await _secretStore.GetAsync($"cloud:{prefix}-client-secret").ConfigureAwait(true);
            return !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret);
        }
        catch
        {
            return false;
        }
    }

    private static string CloudCredentialPrefix(RcloneCloudProvider provider) => provider switch
    {
        RcloneCloudProvider.GoogleDrive => "google",
        RcloneCloudProvider.Dropbox => "dropbox",
        RcloneCloudProvider.OneDrive => "onedrive",
        RcloneCloudProvider.PCloud or RcloneCloudProvider.PCloudEurope => "pcloud",
        _ => string.Empty
    };

    /// <summary>Own OAuth apps take precedence; Google BYOC is mandatory for new accounts.</summary>
    public async Task<bool> ConnectCloudProviderAsync(RcloneCloudProvider provider, string? displayName = null,
        string? customClientId = null, string? customClientSecret = null)
    {
        if (IsBusy) return false;
        HasCloudConnectError = false;
        CloudConnectErrorText = string.Empty;

        var selected = CloudProviders.FirstOrDefault(item => item.Provider == provider);
        if (selected is null) return false;

        var id = customClientId?.Trim() ?? string.Empty;
        var secret = customClientSecret?.Trim() ?? string.Empty;
        if ((id.Length > 0) != (secret.Length > 0))
        {
            CloudConnectErrorText = LocalizationManager.Get("R5164.BYOC.PairRequired");
            HasCloudConnectError = true;
            return false;
        }
        if (id.Length > 256 || secret.Length > 1024
            || id.Contains('\n') || id.Contains('\r') || secret.Contains('\n') || secret.Contains('\r'))
        {
            CloudConnectErrorText = LocalizationManager.Get("R5164.BYOC.Invalid");
            HasCloudConnectError = true;
            return false;
        }

        var prefix = CloudCredentialPrefix(provider);
        if (prefix.Length == 0) return false;
        var clientIdKey = $"cloud:{prefix}-client-id";
        var clientSecretKey = $"cloud:{prefix}-client-secret";
        string? previousClientId = null;
        string? previousClientSecret = null;
        var staged = false;
        string selectedId;
        string selectedSecret;

        async Task RestoreCredentialsAsync()
        {
            if (!staged) return;
            if (string.IsNullOrEmpty(previousClientId))
                await _secretStore.RemoveAsync(clientIdKey).ConfigureAwait(true);
            else
                await _secretStore.SetAsync(clientIdKey, previousClientId).ConfigureAwait(true);

            if (string.IsNullOrEmpty(previousClientSecret))
                await _secretStore.RemoveAsync(clientSecretKey).ConfigureAwait(true);
            else
                await _secretStore.SetAsync(clientSecretKey, previousClientSecret).ConfigureAwait(true);
        }

        try
        {
            previousClientId = await _secretStore.GetAsync(clientIdKey).ConfigureAwait(true);
            previousClientSecret = await _secretStore.GetAsync(clientSecretKey).ConfigureAwait(true);
            // Inline values take precedence; otherwise reuse Settings > Cloud BYOC.
            selectedId = id.Length > 0 ? id : previousClientId?.Trim() ?? string.Empty;
            selectedSecret = id.Length > 0 ? secret : previousClientSecret?.Trim() ?? string.Empty;
            // Do not silently accept a half-configured application. Google
            // Drive's retired shared client cannot be used for new remotes.
            // Other providers retain their rclone default when no BYOC
            // credentials are supplied; the sheet explains this fallback.
            if ((selectedId.Length > 0) != (selectedSecret.Length > 0))
            {
                CloudConnectErrorText = LocalizationManager.Get("R5164.BYOC.PairRequired");
                HasCloudConnectError = true;
                return false;
            }
            if (provider == RcloneCloudProvider.GoogleDrive && selectedId.Length == 0)
            {
                CloudConnectErrorText = LocalizationManager.Get("R5164.BYOC.Required");
                HasCloudConnectError = true;
                return false;
            }
            if (id.Length > 0)
            {
                // Temporarily stage this provider's credentials for rclone config.
                // Always restore the previous defaults after a successful/failed
                // connection so older saved accounts keep their OAuth identity.
                staged = true;
                await _secretStore.SetAsync(clientIdKey, selectedId).ConfigureAwait(true);
                await _secretStore.SetAsync(clientSecretKey, selectedSecret).ConfigureAwait(true);
            }
        }
        catch
        {
            try { await RestoreCredentialsAsync().ConfigureAwait(true); } catch { /* protected store unavailable */ }
            CloudConnectErrorText = LocalizationManager.Get("R5164.BYOC.StorageError");
            HasCloudConnectError = true;
            return false;
        }

        SelectedCloudProvider = selected;
        CloudAccountName = string.IsNullOrWhiteSpace(displayName)
            ? selected.Label
            : displayName.Trim();
        var previous = CloudAccounts.Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            await ConnectCloudAsync().ConfigureAwait(true);
            var newAccount = CloudAccounts.FirstOrDefault(item => !previous.Contains(item.Id));
            if (newAccount is null) return false;

            // Pin BOTH inline and settings-derived BYOC credentials to each
            // remote. A later Settings edit must not silently change an older
            // Google Drive, Dropbox, OneDrive or pCloud account's OAuth app.
            if (selectedId.Length > 0)
            {
                await _secretStore.SetAsync(
                    $"cloud:remote:{newAccount.RemoteName}:client-id", selectedId).ConfigureAwait(true);
                await _secretStore.SetAsync(
                    $"cloud:remote:{newAccount.RemoteName}:client-secret", selectedSecret).ConfigureAwait(true);
            }
            else
            {
                // Remember explicit provider-default connections. A later
                // Settings BYOC edit must not change this remote's OAuth app.
                await _secretStore.SetAsync(
                    $"cloud:remote:{newAccount.RemoteName}:shared-client", "true").ConfigureAwait(true);
            }
            return true;
        }
        catch
        {
            CloudConnectErrorText = LocalizationManager.Get("R5164.BYOC.SnapshotError");
            HasCloudConnectError = true;
            return false;
        }
        finally
        {
            if (staged)
            {
                try { await RestoreCredentialsAsync().ConfigureAwait(true); }
                catch
                {
                    _logger.Warning("Bulut BYOC sağlayıcı ayarları geri yüklenemedi: secure_store_failure");
                    CloudConnectErrorText = LocalizationManager.Get("R5164.BYOC.StorageError");
                    HasCloudConnectError = true;
                }
            }
        }
    }

    /// <summary>WebDAV card enters credentials in Windows, using the existing secret store.</summary>
    public async Task<bool> ConnectNewWebDavFromCardAsync()
    {
        if (IsBusy) return false;
        SelectedProfile = null;
        var previous = WebDavProfiles.Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await ConnectWebDavAsync().ConfigureAwait(true);
        return WebDavProfiles.Any(item => !previous.Contains(item.Id));
    }

    public void CancelCloudAuthorization()
    {
        if (_activeOAuthCancellation is { IsCancellationRequested: false } active)
        {
            active.Cancel();
            OAuthBrowserStatusText = "OAuth yetkilendirmesi iptal ediliyor…";
        }
    }

    public void ClearOAuthAuthorizationLink()
    {
        _pendingOAuthAuthorizationUri = null;
        CanOpenOAuthBrowser = false;
        OAuthBrowserStatusText = string.Empty;
    }

    public void OpenOAuthAuthorizationBrowser()
    {
        var uri = _pendingOAuthAuthorizationUri;
        if (uri is null || !CanOpenOAuthBrowser)
            return;

        // Only the loopback authorization address emitted by the active rclone
        // process is opened. Never log/copy a provider token into diagnostics.
        if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback
            || uri.AbsolutePath != "/auth" || !uri.Query.StartsWith("?state=", StringComparison.Ordinal))
            return;

        try
        {
            // Windows can reuse an existing browser and return no new Process
            // object. A successful shell dispatch does not prove consent yet.
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            OAuthBrowserStatusText =
                "Tarayıcı açma isteği gönderildi. Sağlayıcı izin ekranını tamamlayın; açılmadıysa bağlantıyı kopyalayın.";
        }
        catch
        {
            OAuthBrowserStatusText =
                "Tarayıcı otomatik açılamadı. Yetkilendirme bağlantısını kopyalayıp tarayıcınızda açın.";
            _logger.Warning("OAuth yerel tarayıcı başlatma başarısız: shell_open_failed");
        }
    }

    public void CopyOAuthAuthorizationLink()
    {
        var uri = _pendingOAuthAuthorizationUri;
        if (!CanOpenOAuthBrowser || uri is null)
            return;

        try
        {
            // The ephemeral OAuth state stays local to the clipboard and is
            // available only after an explicit button click.
            System.Windows.Clipboard.SetText(uri.AbsoluteUri);
            OAuthBrowserStatusText =
                "Tek kullanımlık yetkilendirme adresi kopyalandı. Tarayıcınızda açın; kimseyle paylaşmayın.";
        }
        catch
        {
            OAuthBrowserStatusText =
                "Yetkilendirme adresi kopyalanamadı. Tarayıcıda aç düğmesini tekrar deneyin.";
        }
    }

    private static (string Code, string Help) DiagnoseOAuthFailure(Exception exception)
    {
        var message = exception.Message.ToLowerInvariant();
        if (exception is TimeoutException || message.Contains("timed out") || message.Contains("timeout"))
            return ("oauth_timeout", "Yetkilendirme süresi doldu. Tarayıcı izinlerini kontrol edip yeniden deneyin.");
        if (message.Contains("access_denied") || message.Contains("authorization declined")
            || message.Contains("authorization denied") || message.Contains("cancelled"))
            return ("oauth_denied", "Hesap yetkilendirmesi reddedildi veya iptal edildi. Sağlayıcı girişini yeniden başlatın.");
        if (message.Contains("invalid_client") || message.Contains("unauthorized_client")
            || message.Contains("invalid client") || message.Contains("redirect_uri_mismatch"))
            return ("oauth_client", "OAuth uygulama kimlik bilgileri veya yönlendirme ayarı geçersiz. Özel Client ID/Secret bilgilerinizi denetleyin.");
        if (message.Contains("oauth_local_port_reserved")
            || (message.Contains(":53682") && (message.Contains("forbidden")
                || message.Contains("access permissions") || message.Contains("access is denied"))))
            return ("oauth_port_reserved", "Windows 53682 OAuth portuna erişimi engelliyor. Hyper-V veya sanallaştırma sistemi bu portu ayırmış olabilir. Salt-okunur port tanılama betiğini çalıştırın.");
        if (message.Contains("oauth_local_port_occupied")
            || message.Contains("bind: address already in use")
            || message.Contains("address already in use")
            || message.Contains("only one usage of each socket"))
            return ("oauth_port_busy", "127.0.0.1:53682 OAuth portu başka bir işlem tarafından kullanılıyor. Mevcut hesapları silmeyin; portu kullanan uygulamayı kontrol edin.");
        if (message.Contains("failed to start auth webserver"))
            return ("oauth_port_server", "rclone yerel OAuth sunucusunu başlatamadı. Windows 53682 portunun durumunu tanılayın; tarayıcı ayarlarını değiştirmek tek başına çözmez.");
        if (message.Contains("failed to open browser")
            || message.Contains("unable to open browser")
            || message.Contains("browser launch failed"))
            return ("oauth_browser", "Varsayılan tarayıcı başlatılamadı. Tarayıcıda aç veya Bağlantıyı kopyala seçeneklerini deneyin.");
        if (exception is FileNotFoundException || message.Contains("rclone.exe"))
            return ("rclone_missing", "Bulut bağlantı motoru bulunamadı. ADB Player'ın eksiksiz test paketini kullanın.");
        if (message.Contains("connection") || message.Contains("network") || message.Contains("dns"))
            return ("oauth_network", "Bağlantı veya DNS sorunu oluştu. İnternet bağlantısını kontrol edin.");
        return ("oauth_unexpected", "Yetkilendirme tamamlanamadı. Sağlayıcı ve OAuth ayarlarını kontrol edin; yeniden deneyin.");
    }

    private async Task ConnectCloudAsync()
    {
        if (SelectedCloudProvider is null)
        {
            StatusText = "Önce bir bulut sağlayıcısı seçin.";
            return;
        }
        if (!RcloneAvailable)
        {
            await RefreshRcloneStatusAsync().ConfigureAwait(true);
            if (!RcloneAvailable)
            {
                _logger.Warning("Bulut motoru kullanılamıyor: rclone_unavailable");
                CloudConnectErrorText = "rclone bağlantı motoru başlatılamadı. Eksiksiz ADB Player paketini kontrol edin.";
                HasCloudConnectError = true;
                StatusText = CloudConnectErrorText;
                return;
            }
        }

        try
        {
            IsBusy = true;
            ClearOAuthAuthorizationLink();
            CloudConnectErrorText = string.Empty;
            HasCloudConnectError = false;
            StatusText = $"{SelectedCloudProvider.Label} için OAuth yetkilendirmesi başlatılıyor…";
            var displayName = string.IsNullOrWhiteSpace(CloudAccountName) ? SelectedCloudProvider.Label : CloudAccountName.Trim();
            var uiContext = SynchronizationContext.Current;
            _activeOAuthCancellation = new CancellationTokenSource();
            var remoteName = await _cloudService.ConnectAsync(
                SelectedCloudProvider.Provider, displayName,
                cancellationToken: _activeOAuthCancellation.Token,
                onAuthorizationLink: uri =>
                {
                    // rclone stderr is read in a worker task. Marshal every
                    // UI update and browser open to the WPF UI context.
                    uiContext?.Post(_ =>
                    {
                        if (!IsBusy) return;
                        _pendingOAuthAuthorizationUri = uri;
                        CanOpenOAuthBrowser = true;
                        OpenOAuthAuthorizationBrowser();
                    }, null);
                }).ConfigureAwait(true);
            var account = new SavedCloudAccount
            {
                DisplayName = displayName,
                RemoteName = remoteName,
                Provider = SelectedCloudProvider.Provider,
                LastUsedUtc = DateTimeOffset.UtcNow
            };
            CloudAccounts.Insert(0, account);
            SelectedCloudAccount = account;
            _settings.CloudAccounts = CloudAccounts.ToList();
            await _saveSettings().ConfigureAwait(true);
            StatusText = $"{account.ProviderLabel} hesabı bağlandı.";
            await OpenCloudAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            CloudConnectErrorText = "OAuth yetkilendirmesi iptal edildi.";
            HasCloudConnectError = true;
            StatusText = CloudConnectErrorText;
        }
        catch (Exception exception)
        {
            // rclone stderr may contain OAuth codes, tokens or callback URLs.
            // Do not log or display the raw exception. Avoid a second modal
            // while the source-specific connect sheet is already open.
            var diagnosis = DiagnoseOAuthFailure(exception);
            _logger.Warning($"rclone bulut bağlantısı tamamlanamadı: {diagnosis.Code}");
            CloudConnectErrorText = diagnosis.Help;
            HasCloudConnectError = true;
            StatusText = diagnosis.Help;
        }
        finally
        {
            ClearOAuthAuthorizationLink();
            _activeOAuthCancellation?.Dispose();
            _activeOAuthCancellation = null;
            IsBusy = false;
        }
    }

    // A persisted cloud resume ID is NOT the temporary rclone HTTP proxy URL.
    // Re-resolve it through the same authenticated cloud account on every play.
    public bool IsConnectedCloudSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("rclone", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.AbsolutePath == "/")
            return false;

        var remoteName = Uri.UnescapeDataString(uri.Host);
        return CloudAccounts.Any(account =>
            account.RemoteName.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task OpenPersistentCloudMediaAsync(string source)
    {
        if (!IsConnectedCloudSource(source))
        {
            StatusText = "Bulut medya kaynağına ait hesap artık bağlı değil.";
            _dialogs.ShowError(StatusText);
            return;
        }

        var uri = new Uri(source, UriKind.Absolute);
        var remoteName = Uri.UnescapeDataString(uri.Host);
        var account = CloudAccounts.First(item =>
            item.RemoteName.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
        var filename = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(filename))
        {
            StatusText = "Bulut dosyasının adı bulunamadı.";
            return;
        }

        try
        {
            IsBusy = true;
            var item = new RemoteSourceItem(
                source, filename, RemoteSourceItemKind.Video,
                source, null, null, null,
                Provider: $"rclone:{account.RemoteName}");
            var request = await _cloudService.CreateOpenRequestAsync(account, item).ConfigureAwait(true);
            await _openRemote(request).ConfigureAwait(true);
            StatusText = $"Açılıyor: {filename}";
        }
        catch (Exception exception)
        {
            _logger.Error("Bulut geçmişi yeniden açılamadı.", exception);
            StatusText = DescribeRemotePlaybackFailure(exception);
            _dialogs.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenCloudAsync()
    {
        if (SelectedCloudAccount is null)
        {
            StatusText = "Önce bağlı bir bulut hesabı seçin.";
            return;
        }

        try
        {
            IsBusy = true;
            _currentCloudAccount = SelectedCloudAccount;
            _currentWebDavConnection = null;
            StatusText = $"{SelectedCloudAccount.ProviderLabel} dosyaları yükleniyor…";
            RemoteBrowseResult result;
            try
            {
                result = await _cloudService.BrowseAsync(
                    SelectedCloudAccount,
                    SelectedCloudAccount.LastBrowsePath).ConfigureAwait(true);
            }
            catch when (!string.IsNullOrWhiteSpace(SelectedCloudAccount.LastBrowsePath))
            {
                SelectedCloudAccount.LastBrowsePath = string.Empty;
                result = await _cloudService.BrowseAsync(SelectedCloudAccount).ConfigureAwait(true);
            }
            SelectedCloudAccount.LastUsedUtc = DateTimeOffset.UtcNow;
            _settings.CloudAccounts = CloudAccounts.OrderByDescending(item => item.LastUsedUtc).ToList();
            await _saveSettings().ConfigureAwait(true);
            await ApplyBrowseResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("rclone bulut hesabı açılamadı.", exception);
            StatusText = "Bulut hesabı açılamadı.";
            _dialogs.ShowError($"Bulut hesabı açılamadı.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    public async Task OpenCloudAccountAsync(SavedCloudAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        SelectedCloudAccount = account;
        await OpenCloudAsync().ConfigureAwait(true);
    }

    private async Task DisconnectCloudAsync()
    {
        if (SelectedCloudAccount is null) return;
        await DisconnectCloudAsync(SelectedCloudAccount).ConfigureAwait(true);
    }

    public async Task DisconnectCloudAsync(SavedCloudAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        SelectedCloudAccount = account;
        if (!_dialogs.Confirm($"{account} hesabının bağlantısı kaldırılsın mı?")) return;

        try
        {
            IsBusy = true;
            await _cloudService.DisconnectAsync(account.RemoteName).ConfigureAwait(true);
            // Never retain custom client credentials for a disconnected remote.
            try
            {
                await _secretStore.RemoveAsync($"cloud:remote:{account.RemoteName}:client-id").ConfigureAwait(true);
                await _secretStore.RemoveAsync($"cloud:remote:{account.RemoteName}:client-secret").ConfigureAwait(true);
                await _secretStore.RemoveAsync($"cloud:remote:{account.RemoteName}:shared-client").ConfigureAwait(true);
            }
            catch
            {
                _logger.Warning("Bağlantı kaldırıldı; eski özel OAuth kimliği temizlenemedi.");
            }
            CloudAccounts.Remove(account);
            if (ReferenceEquals(_currentCloudAccount, account)) _currentCloudAccount = null;
            SelectedCloudAccount = CloudAccounts.FirstOrDefault();
            _settings.CloudAccounts = CloudAccounts.ToList();
            await _saveSettings().ConfigureAwait(true);
            Items.Clear();
            IsBrowserVisible = false;
            StatusText = "Bulut hesabı kaldırıldı.";
        }
        catch (Exception exception)
        {
            _logger.Error("rclone bulut hesabı kaldırılamadı.", exception);
            _dialogs.ShowError($"Bulut hesabı kaldırılamadı.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task ConnectWebDavAsync()
    {
        if (string.IsNullOrWhiteSpace(WebDavUrl))
        {
            StatusText = "WebDAV adresini girin.";
            return;
        }

        try
        {
            IsBusy = true;
            _currentWebDavConnection = new WebDavConnection(WebDavUrl.Trim(), WebDavUsername.Trim(), WebDavPassword);
            StatusText = "WebDAV sunucusuna bağlanılıyor…";
            var result = await _service.BrowseWebDavAsync(_currentWebDavConnection).ConfigureAwait(true);
            await SaveWebDavProfileAsync().ConfigureAwait(true);
            await ApplyBrowseResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("WebDAV bağlantısı başarısız.", exception);
            StatusText = "WebDAV bağlantısı başarısız.";
            _dialogs.ShowError($"WebDAV bağlantısı kurulamadı.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenWebDavProfileAsync(SavedWebDavProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        SelectedProfile = profile;
        WebDavName = profile.Name;
        WebDavUrl = profile.BaseUrl;
        WebDavUsername = profile.Username;
        WebDavPassword = string.IsNullOrWhiteSpace(profile.SecretKey)
            ? string.Empty
            : await _secretStore.GetAsync(profile.SecretKey).ConfigureAwait(true) ?? string.Empty;
        await ConnectWebDavAsync().ConfigureAwait(true);
    }

    private async Task SaveWebDavProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(WebDavUrl))
        {
            StatusText = "WebDAV adresini girin.";
            return;
        }

        var profile = SelectedProfile ?? new SavedWebDavProfile();
        profile.Name = string.IsNullOrWhiteSpace(WebDavName) ? "WebDAV" : WebDavName.Trim();
        profile.BaseUrl = WebDavUrl.Trim();
        profile.Username = WebDavUsername.Trim();
        profile.SecretKey = string.IsNullOrWhiteSpace(profile.SecretKey) ? $"webdav:{profile.Id}:password" : profile.SecretKey;
        await _secretStore.SetAsync(profile.SecretKey, WebDavPassword).ConfigureAwait(true);
        if (!WebDavProfiles.Contains(profile)) WebDavProfiles.Add(profile);
        SelectedProfile = profile;
        _settings.WebDavProfiles = WebDavProfiles.ToList();
        await _saveSettings().ConfigureAwait(true);
        StatusText = "WebDAV profili ve parolası güvenli olarak kaydedildi.";
    }

    private async Task DeleteWebDavProfileAsync()
    {
        if (SelectedProfile is null) return;
        await DeleteWebDavProfileAsync(SelectedProfile).ConfigureAwait(true);
    }

    public async Task DeleteWebDavProfileAsync(SavedWebDavProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_dialogs.Confirm($"{profile.Name} ağ konumu kaldırılsın mı?")) return;
        if (!string.IsNullOrWhiteSpace(profile.SecretKey))
            await _secretStore.RemoveAsync(profile.SecretKey).ConfigureAwait(true);
        WebDavProfiles.Remove(profile);
        SelectedProfile = WebDavProfiles.FirstOrDefault();
        WebDavPassword = string.Empty;
        _settings.WebDavProfiles = WebDavProfiles.ToList();
        await _saveSettings().ConfigureAwait(true);
        StatusText = "WebDAV profili silindi.";
    }

    public async Task OpenSelectedAsync()
    {
        if (SelectedItem is null)
        {
            StatusText = "Önce bir dosya veya klasör seçin.";
            return;
        }

        if (SelectedItem.IsFolder)
        {
            await BrowseFolderAsync(SelectedItem.BrowsePath).ConfigureAwait(true);
            return;
        }

        if (IsAudioSelectionMode)
        {
            await AttachSelectedAudioAsync().ConfigureAwait(true);
            return;
        }

        if (!SelectedItem.IsPlayable)
        {
            StatusText = "Seçilen öğe video değil.";
            return;
        }

        try
        {
            IsBusy = true;
            if (SelectedItem.Provider?.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_currentCloudAccount is null) throw new InvalidOperationException("Etkin bulut hesabı bulunamadı.");
                var request = await _cloudService.CreateOpenRequestAsync(_currentCloudAccount, SelectedItem).ConfigureAwait(true);
                await _openRemote(request).ConfigureAwait(true);
            }
            else if (SelectedItem.Provider?.Equals("PixelDrain", StringComparison.OrdinalIgnoreCase) == true)
            {
                var result = await _service.ResolvePublicLinkAsync(SelectedItem.OpenUrl!).ConfigureAwait(true);
                var request = result.DirectOpen ?? throw new InvalidOperationException("PixelDrain video akışı doğrulanamadı.");
                await _openRemote(request).ConfigureAwait(true);
            }
            else
            {
                await _openRemote(new RemoteOpenRequest(
                    SelectedItem.OpenUrl!,
                    SelectedItem.Name,
                    SelectedItem.HttpHeaders,
                    SelectedItem.Provider)).ConfigureAwait(true);
            }
            StatusText = $"Açılıyor: {SelectedItem.Name}";
        }
        catch (Exception exception)
        {
            _logger.Error("Uzak video açılamadı.", exception);
            // User-facing dialogs intentionally hide all paragraphs after a
            // blank line. Show a SAFE classified error in the first paragraph
            // while preserving the original exception privately in app logs.
            StatusText = DescribeRemotePlaybackFailure(exception);
            _dialogs.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string DescribeRemotePlaybackFailure(Exception exception)
    {
        // Do not display arbitrary exception text: cloud URLs, OAuth tokens,
        // server auth credentials and local paths may appear in raw errors.
        var message = exception.Message ?? string.Empty;
        var http = System.Text.RegularExpressions.Regex.Match(
            message, @"\bHTTP\s+(401|403|404|416|429|500|502|503|504)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (http.Success)
            return $"Video açılamadı: bulut akışı HTTP {http.Groups[1].Value} döndürdü. Drive erişimini ve rclone günlüğünü kontrol edin.";
        if (exception is TimeoutException ||
            message.Contains("zaman aşım", StringComparison.OrdinalIgnoreCase))
            return "Video açılamadı: bulut akışının ilk Range isteği zaman aşımına uğradı. rclone günlüğünü kontrol edin.";
        if (message.Contains("Range", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("kısmi içerik", StringComparison.OrdinalIgnoreCase))
            return "Video açılamadı: uzak kaynak HTTP Range isteğini karşılamadı.";
        if (message.Contains("sunucusu erken kapandı", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("sunucusu zamanında hazır", StringComparison.OrdinalIgnoreCase))
            return "Video açılamadı: yerel rclone medya sunucusu başlatılamadı. rclone günlüğünü kontrol edin.";
        if (message.Contains("mpv", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Oynatıcı", StringComparison.OrdinalIgnoreCase))
            return "Video açılamadı: libmpv oynatıcı komutu başarısız. Player günlüğünü kontrol edin.";
        return $"Video açılamadı ({exception.GetType().Name}). Nedeni belirlemek için Player ve rclone günlüklerini kontrol edin.";
    }

    private async Task BrowseFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            IsBusy = true;
            var result = path.StartsWith("rclone://", StringComparison.OrdinalIgnoreCase)
                ? _currentCloudAccount is not null
                    ? await _cloudService.BrowseAsync(_currentCloudAccount, path).ConfigureAwait(true)
                    : throw new InvalidOperationException("Etkin bulut hesabı bulunamadı.")
                : path.StartsWith("pcloud://", StringComparison.OrdinalIgnoreCase) ||
                  path.StartsWith("gofile://", StringComparison.OrdinalIgnoreCase)
                    ? await _service.ResolvePublicLinkAsync(path).ConfigureAwait(true)
                    : _currentWebDavConnection is not null
                        ? await _service.BrowseWebDavAsync(_currentWebDavConnection, path).ConfigureAwait(true)
                        : throw new InvalidOperationException("WebDAV bağlantı bilgisi bulunamadı.");
            await ApplyBrowseResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Uzak klasör açılamadı.", exception);
            StatusText = "Klasör açılamadı.";
            _dialogs.ShowError($"Klasör açılamadı.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private Task GoBackAsync() => CanGoBack ? BrowseFolderAsync(_browse!.ParentPath) : Task.CompletedTask;

    public void BeginAudioSelection()
    {
        IsAudioSelectionMode = true;
        IsBrowserVisible = false;
        SelectedItem = null;
        StatusText = "Bulut hesabı, paylaşım bağlantısı veya WebDAV konumu açın; ardından ses dosyasını ya da ses arşivini seçin.";
    }

    public void EndAudioSelection() => IsAudioSelectionMode = false;

    public void ShowConnectionsHome()
    {
        IsBrowserVisible = false;
        SelectedItem = null;
        StatusText = "Bağlantılarınız hazır.";
    }

    private async Task RefreshAsync()
    {
        if (_browse?.Provider == RemoteSourceProvider.Rclone && _currentCloudAccount is not null)
        {
            await BrowseFolderAsync(_browse.CurrentPath).ConfigureAwait(true);
            return;
        }
        if (_browse?.Provider == RemoteSourceProvider.WebDav && _currentWebDavConnection is not null)
        {
            await BrowseFolderAsync(_browse.CurrentPath).ConfigureAwait(true);
            return;
        }
        if (_browse?.Provider == RemoteSourceProvider.PCloud && !string.IsNullOrWhiteSpace(_browse.CurrentPath))
        {
            await BrowseFolderAsync(_browse.CurrentPath).ConfigureAwait(true);
            return;
        }
    }

    private Task AttachSelectedSubtitleAsync() => DownloadAndAttachAsync(RemoteSourceItemKind.Subtitle, _attachSubtitle, "altyazı");

    private async Task AttachSelectedAudioAsync()
    {
        if (SelectedItem is null || SelectedItem.Kind is not (RemoteSourceItemKind.Audio or RemoteSourceItemKind.Archive))
        {
            StatusText = "Bir ses dosyası veya ZIP, RAR ya da 7-Zip arşivi seçin.";
            return;
        }

        try
        {
            IsBusy = true;
            if (SelectedItem.Kind == RemoteSourceItemKind.Archive)
            {
                StatusText = "Ses arşivi indiriliyor…";
                var archivePath = await DownloadItemToCacheAsync(SelectedItem).ConfigureAwait(true);
                StatusText = "Arşivdeki ses dosyaları inceleniyor…";
                var extracted = await ArchiveAudioHelper.ExtractAudioAsync(
                    archivePath,
                    Path.Combine(_paths.RemoteCacheDirectory, "audio-extracted"),
                    _dialogs).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    StatusText = "Arşivden ses seçimi iptal edildi.";
                    return;
                }
                await _attachRemoteAudio(new RemoteOpenRequest(
                    extracted,
                    Path.GetFileName(extracted),
                    Provider: "archive-audio")).ConfigureAwait(true);
                StatusText = $"Arşivden harici ses eklendi: {Path.GetFileName(extracted)}";
                IsAudioSelectionMode = false;
                return;
            }

            StatusText = "Uzak ses akışı açılıyor…";
            RemoteOpenRequest request;
            if (SelectedItem.Provider?.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_currentCloudAccount is null) throw new InvalidOperationException("Etkin bulut hesabı bulunamadı.");
                request = await _cloudService.CreateOpenRequestAsync(_currentCloudAccount, SelectedItem).ConfigureAwait(true);
            }
            else if (SelectedItem.Provider?.Equals("PixelDrain", StringComparison.OrdinalIgnoreCase) == true)
            {
                var source = SelectedItem.OpenUrl ?? throw new InvalidOperationException("Ses bağlantısı bulunamadı.");
                var result = await _service.ResolvePublicLinkAsync(source).ConfigureAwait(true);
                request = result.DirectOpen ?? throw new InvalidOperationException("PixelDrain ses akışı doğrulanamadı.");
            }
            else
            {
                var source = SelectedItem.OpenUrl ?? throw new InvalidOperationException("Ses bağlantısı bulunamadı.");
                request = new RemoteOpenRequest(source, SelectedItem.Name, SelectedItem.HttpHeaders, SelectedItem.Provider);
            }
            await _attachRemoteAudio(request).ConfigureAwait(true);
            StatusText = $"Harici ses akışı eklendi: {SelectedItem.Name}";
            IsAudioSelectionMode = false;
        }
        catch (Exception exception)
        {
            _logger.Error("Uzak ses akışı açılamadı.", exception);
            StatusText = "Uzak ses akışı açılamadı.";
            _dialogs.ShowError($"Ses akışı açılamadı.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task<string> DownloadItemToCacheAsync(RemoteSourceItem item)
    {
        Directory.CreateDirectory(_paths.RemoteCacheDirectory);
        var fileName = SafeFileName(item.Name);
        var path = Path.Combine(_paths.RemoteCacheDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{fileName}");
        if (item.Provider?.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (_currentCloudAccount is null) throw new InvalidOperationException("Etkin bulut hesabı bulunamadı.");
            await _cloudService.DownloadToFileAsync(_currentCloudAccount, item, path).ConfigureAwait(true);
        }
        else
        {
            await _service.DownloadToFileAsync(item, path).ConfigureAwait(true);
        }
        return path;
    }

    private async Task DownloadAndAttachAsync(RemoteSourceItemKind expectedKind, Action<string> attach, string label)
    {
        if (SelectedItem is null || SelectedItem.Kind != expectedKind)
        {
            StatusText = $"Önce bir {label} dosyası seçin.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Uzak {label} indiriliyor…";
            Directory.CreateDirectory(_paths.RemoteCacheDirectory);
            var fileName = SafeFileName(SelectedItem.Name);
            var path = Path.Combine(_paths.RemoteCacheDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{fileName}");
            if (SelectedItem.Provider?.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_currentCloudAccount is null) throw new InvalidOperationException("Etkin bulut hesabı bulunamadı.");
                await _cloudService.DownloadToFileAsync(_currentCloudAccount, SelectedItem, path).ConfigureAwait(true);
            }
            else
            {
                var bytes = await _service.DownloadAsync(SelectedItem).ConfigureAwait(true);
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            }
            attach(path);
            StatusText = $"{label} indirildi ve eklendi: {fileName}";
        }
        catch (Exception exception)
        {
            _logger.Error($"Uzak {label} indirilemedi.", exception);
            StatusText = $"Uzak {label} indirilemedi.";
            _dialogs.ShowError($"Dosya indirilemedi.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task LoadProfilePasswordAsync(SavedWebDavProfile profile)
    {
        WebDavPassword = string.IsNullOrWhiteSpace(profile.SecretKey)
            ? string.Empty
            : await _secretStore.GetAsync(profile.SecretKey).ConfigureAwait(true) ?? string.Empty;
    }

    private async Task PersistSavedLinksAsync()
    {
        _settings.SavedPublicLinks = SavedPublicLinks.Take(50).ToList();
        await _saveSettings().ConfigureAwait(true);
    }

    private static string HostLabel(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "Bağlantı";

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "uzak-dosya" : safe;
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        OnPropertyChanged(nameof(CloudAvailabilityText));
        OnPropertyChanged(nameof(BrowserTitle));
    }

    public async ValueTask DisposeAsync()
    {
        CancelCloudAuthorization();
        _service.Dispose();
        await _cloudService.DisposeAsync().ConfigureAwait(false);
    }
}
