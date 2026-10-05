using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.ViewModels;

/// <summary>
/// User-facing runtime API/application credentials.
/// Non-secret preferences live in settings.json; secrets are protected by the
/// existing Windows DPAPI-backed ISecretStore. Empty values remove an override
/// and let packaged application defaults take over.
/// </summary>
public sealed class ApiSettingsPanelViewModel : ObservableObject, IDisposable
{
    public const string TelegramApiHashSecretKey = "telegram:api-hash";
    public const string AltyaziDbApiKeySecretKey = "altyazidb:api-key";
    public const string GofileApiTokenSecretKey = "gofile:api-token";
    public const string AkiraBoxApiBaseUrlSecretKey = "akirabox:api-base-url";
    public const string AkiraBoxApiCredentialSecretKey = "akirabox:api-key";
    public const string GoogleClientIdSecretKey = "cloud:google-client-id";
    public const string GoogleClientSecretSecretKey = "cloud:google-client-secret";
    public const string DropboxClientIdSecretKey = "cloud:dropbox-client-id";
    public const string DropboxClientSecretSecretKey = "cloud:dropbox-client-secret";
    public const string OneDriveClientIdSecretKey = "cloud:onedrive-client-id";
    public const string OneDriveClientSecretSecretKey = "cloud:onedrive-client-secret";
    public const string PCloudClientIdSecretKey = "cloud:pcloud-client-id";
    public const string PCloudClientSecretSecretKey = "cloud:pcloud-client-secret";

    private readonly ISecretStore _secrets;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly Func<Task> _reloadTelegramCredentials;
    private readonly Func<Task> _reloadSubtitleCredentials;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _autoSaveCts;
    private bool _loading;
    private bool _disposed;
    private bool _isBusy;
    private string _statusText = string.Empty;
    private string _telegramApiIdText = string.Empty;
    private string _telegramApiHash = string.Empty;
    private string _altyaziDbApiUrl = string.Empty;
    private string _altyaziDbApiKey = string.Empty;
    private string _gofileApiToken = string.Empty;
    private string _akiraBoxApiBaseUrl = "https://akirabox.com/api";
    private string _akiraBoxApiCredential = string.Empty;
    private string _googleClientId = string.Empty;
    private string _googleClientSecret = string.Empty;
    private string _dropboxClientId = string.Empty;
    private string _dropboxClientSecret = string.Empty;
    private string _oneDriveClientId = string.Empty;
    private string _oneDriveClientSecret = string.Empty;
    private string _pCloudClientId = string.Empty;
    private string _pCloudClientSecret = string.Empty;

    public ApiSettingsPanelViewModel(
        ISecretStore secrets,
        PlayerSettings settings,
        Func<Task> saveSettings,
        Func<Task> reloadTelegramCredentials,
        Func<Task> reloadSubtitleCredentials,
        IAppLogger logger)
    {
        _secrets = secrets;
        _settings = settings;
        _saveSettings = saveSettings;
        _reloadTelegramCredentials = reloadTelegramCredentials;
        _reloadSubtitleCredentials = reloadSubtitleCredentials;
        _logger = logger;
        SaveNowCommand = new AsyncRelayCommand(SaveNowAsync);
        ReloadCommand = new AsyncRelayCommand(LoadAsync);
        _ = LoadAsync();
    }

    public ICommand SaveNowCommand { get; }
    public ICommand ReloadCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TelegramApiIdText
    {
        get => _telegramApiIdText;
        set => SetAndSchedule(ref _telegramApiIdText, value);
    }

    public string TelegramApiHash
    {
        get => _telegramApiHash;
        set => SetAndSchedule(ref _telegramApiHash, value);
    }

    public string AltyaziDbApiUrl
    {
        get => _altyaziDbApiUrl;
        set => SetAndSchedule(ref _altyaziDbApiUrl, value);
    }

    public string AltyaziDbApiKey
    {
        get => _altyaziDbApiKey;
        set => SetAndSchedule(ref _altyaziDbApiKey, value);
    }

    public string GofileApiToken
    {
        get => _gofileApiToken;
        set => SetAndSchedule(ref _gofileApiToken, value);
    }

    public string AkiraBoxApiBaseUrl
    {
        get => _akiraBoxApiBaseUrl;
        set => SetAndSchedule(ref _akiraBoxApiBaseUrl, value);
    }

    public string AkiraBoxApiCredential
    {
        get => _akiraBoxApiCredential;
        set => SetAndSchedule(ref _akiraBoxApiCredential, value);
    }

    public string GoogleClientId
    {
        get => _googleClientId;
        set => SetAndSchedule(ref _googleClientId, value);
    }

    public string GoogleClientSecret
    {
        get => _googleClientSecret;
        set => SetAndSchedule(ref _googleClientSecret, value);
    }

    public string DropboxClientId
    {
        get => _dropboxClientId;
        set => SetAndSchedule(ref _dropboxClientId, value);
    }

    public string DropboxClientSecret
    {
        get => _dropboxClientSecret;
        set => SetAndSchedule(ref _dropboxClientSecret, value);
    }

    public string OneDriveClientId
    {
        get => _oneDriveClientId;
        set => SetAndSchedule(ref _oneDriveClientId, value);
    }

    public string OneDriveClientSecret
    {
        get => _oneDriveClientSecret;
        set => SetAndSchedule(ref _oneDriveClientSecret, value);
    }

    public string PCloudClientId
    {
        get => _pCloudClientId;
        set => SetAndSchedule(ref _pCloudClientId, value);
    }

    public string PCloudClientSecret
    {
        get => _pCloudClientSecret;
        set => SetAndSchedule(ref _pCloudClientSecret, value);
    }

    public async Task LoadAsync()
    {
        if (_disposed) return;
        try
        {
            IsBusy = true;
            _loading = true;
            TelegramApiIdText = _settings.TelegramApiId > 0
                ? _settings.TelegramApiId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            TelegramApiHash = await _secrets.GetAsync(TelegramApiHashSecretKey).ConfigureAwait(true) ?? string.Empty;
            AltyaziDbApiUrl = string.IsNullOrWhiteSpace(_settings.AltyaziDbApiUrl)
                ? "https://altyazidb.com/api/v1"
                : _settings.AltyaziDbApiUrl;
            AltyaziDbApiKey = await _secrets.GetAsync(AltyaziDbApiKeySecretKey).ConfigureAwait(true) ?? string.Empty;
            GofileApiToken = await _secrets.GetAsync(GofileApiTokenSecretKey).ConfigureAwait(true) ?? string.Empty;
            AkiraBoxApiBaseUrl = await _secrets.GetAsync(AkiraBoxApiBaseUrlSecretKey).ConfigureAwait(true)
                ?? "https://akirabox.com/api";
            AkiraBoxApiCredential = await _secrets.GetAsync(AkiraBoxApiCredentialSecretKey).ConfigureAwait(true) ?? string.Empty;
            GoogleClientId = await _secrets.GetAsync(GoogleClientIdSecretKey).ConfigureAwait(true) ?? string.Empty;
            GoogleClientSecret = await _secrets.GetAsync(GoogleClientSecretSecretKey).ConfigureAwait(true) ?? string.Empty;
            DropboxClientId = await _secrets.GetAsync(DropboxClientIdSecretKey).ConfigureAwait(true) ?? string.Empty;
            DropboxClientSecret = await _secrets.GetAsync(DropboxClientSecretSecretKey).ConfigureAwait(true) ?? string.Empty;
            OneDriveClientId = await _secrets.GetAsync(OneDriveClientIdSecretKey).ConfigureAwait(true) ?? string.Empty;
            OneDriveClientSecret = await _secrets.GetAsync(OneDriveClientSecretSecretKey).ConfigureAwait(true) ?? string.Empty;
            PCloudClientId = await _secrets.GetAsync(PCloudClientIdSecretKey).ConfigureAwait(true) ?? string.Empty;
            PCloudClientSecret = await _secrets.GetAsync(PCloudClientSecretSecretKey).ConfigureAwait(true) ?? string.Empty;
            StatusText = LocalizationManager.Get("Settings.Api.Loaded");
        }
        catch (Exception exception)
        {
            _logger.Error("API ayarları yüklenemedi.", exception);
            StatusText = LocalizationManager.Get("Settings.Api.LoadFailed");
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    public async Task SaveNowAsync()
    {
        if (_disposed || _loading) return;

        _autoSaveCts?.Cancel();
        try
        {
            IsBusy = true;
            StatusText = LocalizationManager.Get("Settings.Api.Saving");

            _settings.TelegramApiId = int.TryParse(
                TelegramApiIdText?.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var apiId)
                ? Math.Max(0, apiId)
                : 0;

            _settings.AltyaziDbApiUrl = string.IsNullOrWhiteSpace(AltyaziDbApiUrl)
                ? "https://altyazidb.com/api/v1"
                : AltyaziDbApiUrl.Trim();

            await _secrets.SetAsync(TelegramApiHashSecretKey, NormalizeSecret(TelegramApiHash)).ConfigureAwait(true);
            await _secrets.SetAsync(AltyaziDbApiKeySecretKey, NormalizeSecret(AltyaziDbApiKey)).ConfigureAwait(true);
            await _secrets.SetAsync(GofileApiTokenSecretKey, NormalizeSecret(GofileApiToken)).ConfigureAwait(true);
            await _secrets.SetAsync(
                AkiraBoxApiBaseUrlSecretKey,
                string.IsNullOrWhiteSpace(AkiraBoxApiBaseUrl)
                    ? "https://akirabox.com/api"
                    : AkiraBoxApiBaseUrl.Trim()).ConfigureAwait(true);
            await _secrets.SetAsync(
                AkiraBoxApiCredentialSecretKey,
                NormalizeSecret(AkiraBoxApiCredential)).ConfigureAwait(true);
            await _secrets.SetAsync(GoogleClientIdSecretKey, NormalizeSecret(GoogleClientId)).ConfigureAwait(true);
            await _secrets.SetAsync(GoogleClientSecretSecretKey, NormalizeSecret(GoogleClientSecret)).ConfigureAwait(true);
            await _secrets.SetAsync(DropboxClientIdSecretKey, NormalizeSecret(DropboxClientId)).ConfigureAwait(true);
            await _secrets.SetAsync(DropboxClientSecretSecretKey, NormalizeSecret(DropboxClientSecret)).ConfigureAwait(true);
            await _secrets.SetAsync(OneDriveClientIdSecretKey, NormalizeSecret(OneDriveClientId)).ConfigureAwait(true);
            await _secrets.SetAsync(OneDriveClientSecretSecretKey, NormalizeSecret(OneDriveClientSecret)).ConfigureAwait(true);
            await _secrets.SetAsync(PCloudClientIdSecretKey, NormalizeSecret(PCloudClientId)).ConfigureAwait(true);
            await _secrets.SetAsync(PCloudClientSecretSecretKey, NormalizeSecret(PCloudClientSecret)).ConfigureAwait(true);
            await _saveSettings().ConfigureAwait(true);

            await _reloadTelegramCredentials().ConfigureAwait(true);
            await _reloadSubtitleCredentials().ConfigureAwait(true);

            StatusText = LocalizationManager.Get("Settings.Api.Saved");
        }
        catch (Exception exception)
        {
            _logger.Error("API ayarları kaydedilemedi.", exception);
            StatusText = LocalizationManager.Get("Settings.Api.SaveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        if (!IsBusy)
        {
            StatusText = LocalizationManager.Get("Settings.Api.Loaded");
        }
    }

    private void SetAndSchedule(ref string field, string? value)
    {
        if (!SetProperty(ref field, value ?? string.Empty) || _loading) return;
        ScheduleAutoSave();
    }

    private async void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;
        try
        {
            await Task.Delay(700, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                await SaveNowAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Another field change restarted the debounce window.
        }
    }

    private static string? NormalizeSecret(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
    }
}
