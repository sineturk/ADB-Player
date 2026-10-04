using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.App.Localization;
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
    private string _statusText = "Güncelleme denetlenmedi.";
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
        StatusText = LocalizationManager.Get("Update.Status.NotChecked");

        Channels = new[] { "stable", "preview" };
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(false), () => !IsBusy);
        DownloadAndInstallCommand = new AsyncRelayCommand(DownloadAndInstallAsync, () => IsUpdateAvailable && !IsBusy);
        OpenUpdateFolderCommand = new RelayCommand(() => OpenFolder(_paths.UpdateDirectory));
        CreateDiagnosticsCommand = new RelayCommand(CreateDiagnostics);
    }

    public IReadOnlyList<string> Channels { get; }
    public ICommand CheckCommand { get; }
    public ICommand DownloadAndInstallCommand { get; }
    public ICommand OpenUpdateFolderCommand { get; }
    public ICommand CreateDiagnosticsCommand { get; }

    public string CurrentVersionText => $"V{_updateService.CurrentVersion.Major}.{_updateService.CurrentVersion.Minor}";
    public string DeploymentModeText => LocalizationManager.Get(
        _paths.IsPortable ? "Update.Deployment.Portable" : "Update.Deployment.Installed");

    public string ManifestUrl
    {
        get => _settings.UpdateManifestUrl;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_settings.UpdateManifestUrl == normalized)
            {
                return;
            }

            _settings.UpdateManifestUrl = normalized;
            OnPropertyChanged();
            ResetAvailableUpdateState();
            _ = _saveSettingsAsync();
        }
    }

    public string SelectedChannel
    {
        get => string.IsNullOrWhiteSpace(_settings.UpdateChannel) ? "stable" : _settings.UpdateChannel;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "stable" : value.Trim().ToLowerInvariant();
            if (_settings.UpdateChannel == normalized)
            {
                return;
            }

            _settings.UpdateChannel = normalized;
            OnPropertyChanged();
            ResetAvailableUpdateState();
            _ = _saveSettingsAsync();
        }
    }

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

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public async Task CheckOnStartupAsync()
    {
        if (!CheckOnStartup || string.IsNullOrWhiteSpace(ManifestUrl))
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
            var result = await _updateService.CheckAsync(ManifestUrl, SelectedChannel).ConfigureAwait(true);
            _availableManifest = result.IsUpdateAvailable ? result.Manifest : null;
            StatusText = result.IsUpdateAvailable && result.AvailableVersion is not null
                ? LocalizationManager.Format(
                    "Update.Status.Available",
                    FormatVersionLabel(result.AvailableVersion.ToString()))
                : result.Message;
            ReleaseNotes = result.Manifest?.ReleaseNotes ?? string.Empty;
            OnPropertyChanged(nameof(IsUpdateAvailable));
            RaiseCommandStates();
        }
        catch (Exception exception)
        {
            _availableManifest = null;
            ReleaseNotes = string.Empty;
            StatusText = $"Güncelleme denetlenemedi: {exception.Message}";
            OnPropertyChanged(nameof(IsUpdateAvailable));
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
            StatusText = LocalizationManager.Get("Update.Status.Downloading");
            var progress = new Progress<double>(value => DownloadProgress = value);
            var download = await _updateService.DownloadInstallerAsync(
                    _availableManifest,
                    _paths.UpdateDirectory,
                    progress)
                .ConfigureAwait(true);

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
            StatusText = $"Güncelleme indirilemedi: {exception.Message}";
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
        OnPropertyChanged(nameof(IsUpdateAvailable));
        RaiseCommandStates();

        if (setStatus)
        {
            StatusText = LocalizationManager.Get("Update.Status.NotChecked");
        }
    }

    private void RaiseCommandStates()
    {
        (CheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadAndInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
