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

public sealed class UpdatePanelViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettingsAsync;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly CrashReportService _crashReports;
    private UpdateManifest? _availableManifest;
    private string _statusText = string.Empty;
    private string _releaseNotes = string.Empty;
    private double _downloadProgress;
    private bool _isBusy;
    private bool _isDownloading;

    public UpdatePanelViewModel(
        IUpdateService updateService,
        PlayerSettings settings,
        Func<Task> saveSettingsAsync,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths,
        CrashReportService crashReports)
    {
        _updateService = updateService;
        _settings = settings;
        _saveSettingsAsync = saveSettingsAsync;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
        _crashReports = crashReports;

        // V1.0.1 RC2 ships one official stable feed. Older settings may contain
        // an empty/custom manifest URL or the retired preview channel; normalize
        // them so upgrading users immediately receive the supported feed.
        var updateSettingsChanged = false;
        if (!string.Equals(
                _settings.UpdateManifestUrl,
                PlayerSettings.OfficialStableUpdateManifestUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            _settings.UpdateManifestUrl = PlayerSettings.OfficialStableUpdateManifestUrl;
            updateSettingsChanged = true;
        }

        if (!string.Equals(_settings.UpdateChannel, "stable", StringComparison.OrdinalIgnoreCase))
        {
            _settings.UpdateChannel = "stable";
            updateSettingsChanged = true;
        }

        if (updateSettingsChanged)
        {
            _ = _saveSettingsAsync();
        }

        StatusText = LocalizationManager.Get("Update.Status.NotChecked");

        Channels = new[] { "stable" };
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(false), () => !IsBusy);
        DownloadAndInstallCommand = new AsyncRelayCommand(
            DownloadAndInstallAsync,
            () => IsUpdateAvailable && !IsBusy);
        OpenUpdateFolderCommand = new RelayCommand(() => OpenFolder(_paths.UpdateDirectory));
        CreateDiagnosticsCommand = new RelayCommand(CreateDiagnostics);
    }

    public IReadOnlyList<string> Channels { get; }
    public ICommand CheckCommand { get; }
    public ICommand DownloadAndInstallCommand { get; }
    public ICommand OpenUpdateFolderCommand { get; }
    public ICommand CreateDiagnosticsCommand { get; }

    public string CurrentVersionText => FormatVersionLabel(_updateService.CurrentVersion.ToString());
    public string AvailableVersionText => _availableManifest is null
        ? string.Empty
        : FormatVersionLabel(_availableManifest.Version);

    public string DeploymentModeText => LocalizationManager.Get(
        _paths.IsPortable ? "Update.Deployment.Portable" : "Update.Deployment.Installed");

    public string ManifestUrl => PlayerSettings.OfficialStableUpdateManifestUrl;

    public string SelectedChannel
    {
        get => "stable";
        set
        {
            if (string.Equals(_settings.UpdateChannel, "stable", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.UpdateChannel = "stable";
            OnPropertyChanged();
            ResetAvailableUpdateState();
            _ = _saveSettingsAsync();
        }
    }

    public bool CanChangeUpdateChannel => false;
    public bool IsPortableDeployment => _paths.IsPortable;

    public string UpdateActionText => LocalizationManager.Get(
        _paths.IsPortable ? "Action.DownloadPortable" : "Action.DownloadInstall");

    public bool CheckOnStartup
    {
        get => _settings.CheckUpdatesOnStartup;
        set
        {
            if (_settings.CheckUpdatesOnStartup == value)
            {
                return;
            }

            _settings.CheckUpdatesOnStartup = value;
            OnPropertyChanged();
            _ = _saveSettingsAsync();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set
        {
            if (!SetProperty(ref _releaseNotes, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasReleaseNotes));
            OnPropertyChanged(nameof(ReleaseNotesDisplayText));
        }
    }

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

    public string ReleaseNotesDisplayText => HasReleaseNotes
        ? ReleaseNotes
        : LocalizationManager.Get("Update.ReleaseNotes.Empty");

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            RaiseCommandStates();
            OnPropertyChanged(nameof(CanChangeUpdateSettings));
        }
    }

    public bool CanChangeUpdateSettings => !IsBusy;
    public bool IsUpdateAvailable => _availableManifest is not null;
    public bool HasUpdateBadge => IsUpdateAvailable;

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public async Task CheckOnStartupAsync()
    {
        if (!CheckOnStartup)
        {
            return;
        }

        await CheckAsync(true).ConfigureAwait(true);
    }

    private async Task CheckAsync(bool quiet)
    {
        ResetAvailableUpdateState(setStatus: false);
        IsBusy = true;
        DownloadProgress = 0;
        try
        {
            StatusText = LocalizationManager.Get("Update.Status.Checking");
            var result = await _updateService
                .CheckAsync(ManifestUrl, SelectedChannel)
                .ConfigureAwait(true);

            _availableManifest = result.IsUpdateAvailable ? result.Manifest : null;

            if (result.IsUpdateAvailable && result.AvailableVersion is not null)
            {
                StatusText = LocalizationManager.Format(
                    "Update.Status.Available",
                    FormatVersionLabel(result.AvailableVersion.ToString()));
                ReleaseNotes = result.Manifest?.ReleaseNotes ?? string.Empty;
            }
            else
            {
                var blockedByPlatform = result.AvailableVersion is not null
                    && result.AvailableVersion > result.CurrentVersion;
                StatusText = blockedByPlatform
                    ? result.Message
                    : LocalizationManager.Get("Update.Status.Current");
                ReleaseNotes = string.Empty;
            }

            NotifyUpdateStateChanged();
        }
        catch (Exception exception)
        {
            _availableManifest = null;
            ReleaseNotes = string.Empty;
            StatusText = LocalizationManager.Format(
                "Update.Status.CheckFailed",
                exception.Message);
            NotifyUpdateStateChanged();
            _logger.Error("Güncelleme denetimi başarısız.", exception);
            if (!quiet)
            {
                _dialogs.ShowError(LocalizationManager.TranslateMessage(StatusText));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadAndInstallAsync()
    {
        if (_availableManifest is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                LocalizationManager.Format(
                    "Update.Confirm.Download",
                    FormatVersionLabel(_availableManifest.Version)),
                LocalizationManager.Get("Update.Dialog.Title")))
        {
            return;
        }

        IsBusy = true;
        IsDownloading = true;
        DownloadProgress = 0;
        try
        {
            StatusText = LocalizationManager.Get(
                _paths.IsPortable
                    ? "Update.Status.DownloadingPortable"
                    : "Update.Status.Downloading");

            var progress = new Progress<double>(value => DownloadProgress = value);
            var download = _paths.IsPortable
                ? await _updateService.DownloadPortableAsync(
                        _availableManifest,
                        _paths.UpdateDirectory,
                        progress)
                    .ConfigureAwait(true)
                : await _updateService.DownloadInstallerAsync(
                        _availableManifest,
                        _paths.UpdateDirectory,
                        progress)
                    .ConfigureAwait(true);

            if (_paths.IsPortable)
            {
                StatusText = LocalizationManager.Format(
                    "Update.Status.PortableReady",
                    Path.GetFileName(download.FilePath));
                OpenFolder(_paths.UpdateDirectory);
                return;
            }

            StatusText = LocalizationManager.Format(
                "Update.Status.DownloadVerified",
                Path.GetFileName(download.FilePath));

            if (!_dialogs.Confirm(
                    LocalizationManager.Get("Update.Confirm.Install"),
                    LocalizationManager.Get("Update.Install.Title")))
            {
                return;
            }

            _updateService.LaunchInstaller(download.FilePath, silent: false);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            StatusText = LocalizationManager.Format(
                "Update.Status.DownloadFailed",
                exception.Message);
            _logger.Error("Güncelleme indirme/kurulum işlemi başarısız.", exception);
            _dialogs.ShowError(LocalizationManager.TranslateMessage(StatusText));
        }
        finally
        {
            IsDownloading = false;
            IsBusy = false;
        }
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(DeploymentModeText));
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(ReleaseNotesDisplayText));
    }

    private static string FormatVersionLabel(string value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        if (!Version.TryParse(normalized, out var version))
        {
            return value ?? string.Empty;
        }

        return version.Build > 0
            ? $"V{version.Major}.{version.Minor}.{version.Build}"
            : $"V{version.Major}.{version.Minor}";
    }

    private void CreateDiagnostics()
    {
        try
        {
            var bundle = _crashReports.CreateDiagnosticsBundle();
            StatusText = $"Tanılama paketi oluşturuldu: {Path.GetFileName(bundle)}";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{bundle}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logger.Error("Tanılama paketi oluşturulamadı.", exception);
            _dialogs.ShowError($"Tanılama paketi oluşturulamadı.\n\n{exception.Message}");
        }
    }

    private void OpenFolder(string path)
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
            _logger.Error("Klasör açılamadı.", exception);
            _dialogs.ShowError($"Klasör açılamadı.\n\n{exception.Message}");
        }
    }

    private void ResetAvailableUpdateState(bool setStatus = true)
    {
        _availableManifest = null;
        ReleaseNotes = string.Empty;
        DownloadProgress = 0;
        NotifyUpdateStateChanged();

        if (setStatus)
        {
            StatusText = LocalizationManager.Get("Update.Status.NotChecked");
        }
    }

    private void NotifyUpdateStateChanged()
    {
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(HasUpdateBadge));
        OnPropertyChanged(nameof(AvailableVersionText));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (CheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadAndInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
