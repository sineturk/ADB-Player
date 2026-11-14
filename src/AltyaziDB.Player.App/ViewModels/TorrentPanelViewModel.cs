using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;
using AltyaziDB.Player.Torrent;

namespace AltyaziDB.Player.App.ViewModels;

public sealed class TorrentPanelViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ITorrentService _service;
    private readonly PlayerSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly Func<string, string?, Task> _openSource;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly ISecretStore _secretStore;
    private readonly QBittorrentHandoffClient _qbClient = new();
    private const string PasswordKey = "qbittorrent.webui.password.v1";
    private string _qbUrl;
    private string _qbUsername;
    private string _qbPasswordInput = string.Empty;
    private string _qbStatus;
    private bool _qbBusy;
    private bool _qbHasSavedPassword;
    private string _qbConnectionState;
    private string? _qbVersion;
    private bool _qbAutoHandoff;
    private readonly HashSet<string> _handedOff = new(StringComparer.Ordinal);
    private readonly HashSet<string> _autoHandoffAttemptedSessions = new(StringComparer.Ordinal);
    private bool _torrentPlaybackActive;
    private bool _observedTorrentPlayback;
    private string? _activeTorrentPlaybackSource;
    private string _autoHandoffStage = "Idle";
    private bool _autoHandoffInProgress;
    private double _handoffProgressPercent;
    // Preserve the last Discover/Prowlarr selection even while metadata is loading,
    // or after the active streaming session is transferred/closed.
    private string _discoverSourceTitle = string.Empty;
    private string _discoverSourceState = "Idle";
    private string? _discoverSourceSessionId;
    public bool HasDiscoverSource => !string.IsNullOrWhiteSpace(_discoverSourceTitle);
    public string DiscoverSourceTitle => HasDiscoverSource
        ? string.Format(LocalizationManager.Get("R5.Torrent.Discover.SourceFormat"), _discoverSourceTitle)
        : string.Empty;
    public string DiscoverSourceStatusText => HasDiscoverSource
        ? LocalizationManager.Get("R5.Torrent.Discover." + _discoverSourceState)
        : string.Empty;

    private void SetDiscoverSourceState(string state, string? title = null)
    {
        if (title is not null)
        {
            _discoverSourceTitle = title;
            _discoverSourceSessionId = null;
        }
        _discoverSourceState = state;
        OnPropertyChanged(nameof(HasDiscoverSource));
        OnPropertyChanged(nameof(DiscoverSourceTitle));
        OnPropertyChanged(nameof(DiscoverSourceStatusText));
    }

    // A direct HTTP stream plays in mpv without creating any MonoTorrent session.
    // Keep this distinction visible in both modern and legacy Torrent panels;
    // do not fabricate a download percentage or attempt qBittorrent seeding.
    public void NotifyDirectSourceOpened(string displayTitle)
    {
        if (string.IsNullOrWhiteSpace(displayTitle)) return;
        SetDiscoverSourceState("Direct", displayTitle.Trim());
    }

    public double HandoffDisplayPercent => Session?.ProgressPercent ?? _handoffProgressPercent;

    public string AutomaticHandoffStage => _autoHandoffStage;
    public string AutomaticHandoffStatus =>
        LocalizationManager.Get("R5.Torrent.Auto." + _autoHandoffStage);
    public bool HasTorrentActivity => Session is not null ||
        _autoHandoffStage is "Checking" or "Transferring" or "Verifying"
            or "Starting" or "Started" or "Failed";

    private void SetAutomaticHandoffStage(string stage)
    {
        if (string.Equals(_autoHandoffStage, stage, StringComparison.Ordinal)) return;
        _autoHandoffStage = stage;
        OnPropertyChanged(nameof(AutomaticHandoffStage));
        OnPropertyChanged(nameof(AutomaticHandoffStatus));
        OnPropertyChanged(nameof(HasTorrentActivity));
    }
    private string? _initializedDownloadDirectory;
    private string DownloadDirectoryForNewSession =>
        string.IsNullOrWhiteSpace(_settings.TorrentDownloadDirectory)
            ? _paths.TorrentCacheDirectory
            : _settings.TorrentDownloadDirectory;
    private string _magnetUri = string.Empty;
    private string _statusText;
    private bool _isBusy;
    private bool _legalNoticeAccepted;
    private TorrentSessionSnapshot? _session;
    private TorrentFileItem? _selectedFile;
    private int? _preferredFileIndex;
    private CancellationTokenSource? _prepareCancellation;
    private string? _preferredDisplayTitle;

    public TorrentPanelViewModel(
        ITorrentService service,
        PlayerSettings settings,
        Func<Task> saveSettings,
        Func<string, string?, Task> openSource,
        IUserDialogService dialogs,
        IAppLogger logger,
        AppPaths paths,
        ISecretStore secretStore)
    {
        _service = service;
        _settings = settings;
        _saveSettings = saveSettings;
        _openSource = openSource;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
        _secretStore = secretStore;
        _qbUrl = settings.QBittorrentWebUiUrl;
        _qbUsername = settings.QBittorrentUsername;
        _qbAutoHandoff = settings.QBittorrentAutoHandoff;
        // Connection state and transfer-operation messages are independent.
        // A saved endpoint is not considered online until it has been verified.
        _qbConnectionState = string.IsNullOrWhiteSpace(settings.QBittorrentUsername)
            ? "NotConfigured" : "Unchecked";
        _qbStatus = string.Empty;
        _logger.Info($"Torrent akış motoru: {service.EngineDiagnostic}");
        _statusText = LocalizationManager.Get(service.IsEngineAvailable ? "Panel.TorrentReady" : "Panel.TorrentUnavailable");
        _legalNoticeAccepted = settings.TorrentLegalNoticeAccepted;
        Files = new ObservableCollection<TorrentFileItem>();
        PrepareMagnetCommand = new AsyncRelayCommand(() => PrepareMagnetCoreAsync(CancellationToken.None, propagateFailure: false));
        PrepareTorrentFileCommand = new AsyncRelayCommand(PrepareTorrentFileAsync);
        StartSelectedCommand = new AsyncRelayCommand(StartSelectedAsync);
        PauseCommand = new AsyncRelayCommand(PauseAsync);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        OpenCacheCommand = new RelayCommand(OpenCache);
        SaveQBittorrentCommand = new AsyncRelayCommand(SaveQBittorrentAsync);
        CheckQBittorrentCommand = new AsyncRelayCommand(RefreshQBittorrentConnectionAsync);
        TransferToQBittorrentCommand = new AsyncRelayCommand(TransferToQBittorrentAsync);
        SmartHandoffCommand = new AsyncRelayCommand(SmartHandoffAsync);
        _service.SessionChanged += OnSessionChanged;
    }

    public ObservableCollection<TorrentFileItem> Files { get; }
    public ICommand PrepareMagnetCommand { get; }
    public ICommand PrepareTorrentFileCommand { get; }
    public ICommand StartSelectedCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenCacheCommand { get; }
    public ICommand SaveQBittorrentCommand { get; }
    public ICommand CheckQBittorrentCommand { get; }
    public ICommand TransferToQBittorrentCommand { get; }
    public ICommand SmartHandoffCommand { get; }
    public string QBittorrentUrl
    {
        get => _qbUrl;
        set
        {
            if (SetProperty(ref _qbUrl, value) && !QBittorrentBusy)
                SetQBittorrentConnectionState("Edited");
        }
    }
    public string QBittorrentUsername
    {
        get => _qbUsername;
        set
        {
            if (SetProperty(ref _qbUsername, value) && !QBittorrentBusy)
                SetQBittorrentConnectionState("Edited");
        }
    }
    // An unsaved draft only: never serialized or logged.
    public string QBittorrentPasswordInput
    {
        get => _qbPasswordInput;
        set
        {
            if (SetProperty(ref _qbPasswordInput, value) &&
                !string.IsNullOrEmpty(value) && !QBittorrentBusy)
                SetQBittorrentConnectionState("Edited");
        }
    }
    // Transport status is deliberately separate from torrent-transfer status.
    public string QBittorrentConnectionState => _qbConnectionState;
    public string QBittorrentConnectionStatus =>
        LocalizationManager.Get("R5.Torrent.Qb.State." + _qbConnectionState);
    public bool QBittorrentHasSavedPassword
    {
        get => _qbHasSavedPassword;
        private set => SetProperty(ref _qbHasSavedPassword, value);
    }
    public string QBittorrentVersion => _qbVersion ?? string.Empty;
    public string QBittorrentStatus { get => _qbStatus; private set => SetProperty(ref _qbStatus, value); }
    public bool QBittorrentBusy { get => _qbBusy; private set => SetProperty(ref _qbBusy, value); }

    private void SetQBittorrentConnectionState(string state)
    {
        if (_qbConnectionState == state) return;
        _qbConnectionState = state;
        OnPropertyChanged(nameof(QBittorrentConnectionState));
        OnPropertyChanged(nameof(QBittorrentConnectionStatus));
    }

    private bool HasUnsavedQBittorrentEdits =>
        !string.Equals(QBittorrentUrl?.Trim(), _settings.QBittorrentWebUiUrl?.Trim(),
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(QBittorrentUsername?.Trim(), _settings.QBittorrentUsername?.Trim(),
            StringComparison.Ordinal) ||
        !string.IsNullOrEmpty(QBittorrentPasswordInput);

    /// <summary>
    /// Called when the Torrent page opens and by the Check connection button.
    /// Checks the SAVED settings, using the DPAPI password without showing it.
    /// Never saves settings or overwrites a newly typed password.
    /// </summary>
    public async Task RefreshQBittorrentConnectionAsync()
    {
        if (QBittorrentBusy) return;
        if (string.IsNullOrWhiteSpace(_settings.QBittorrentUsername) ||
            string.IsNullOrWhiteSpace(_settings.QBittorrentWebUiUrl))
        {
            QBittorrentHasSavedPassword = false;
            SetQBittorrentConnectionState("NotConfigured");
            return;
        }
        if (HasUnsavedQBittorrentEdits)
        {
            SetQBittorrentConnectionState("Edited");
            return;
        }

        try
        {
            QBittorrentBusy = true;
            SetQBittorrentConnectionState("Checking");
            var password = await _secretStore.GetAsync(PasswordKey).ConfigureAwait(true);
            QBittorrentHasSavedPassword = !string.IsNullOrEmpty(password);
            if (!QBittorrentHasSavedPassword)
            {
                SetQBittorrentConnectionState("PasswordMissing");
                return;
            }

            var version = await _qbClient.TestAsync(
                _settings.QBittorrentWebUiUrl,
                _settings.QBittorrentUsername,
                password!).ConfigureAwait(true);
            if (HasUnsavedQBittorrentEdits)
            {
                SetQBittorrentConnectionState("Edited");
                return;
            }
            _qbVersion = version;
            OnPropertyChanged(nameof(QBittorrentVersion));
            SetQBittorrentConnectionState("Connected");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Log no server response, username, URL, tracker passkey or password.
            _logger.Warning("qBittorrent durum kontrolü tamamlanamadı.");
            SetQBittorrentConnectionState("Unavailable");
        }
        finally { QBittorrentBusy = false; }
    }
    public bool QBittorrentAutoHandoffRestricted =>
        _qbAutoHandoff && !string.IsNullOrWhiteSpace(_settings.TorrentDownloadDirectory);

    public bool QBittorrentAutoHandoff
    {
        get => _qbAutoHandoff;
        set
        {
            if (!SetProperty(ref _qbAutoHandoff, value)) return;
            _settings.QBittorrentAutoHandoff = value;
            OnPropertyChanged(nameof(QBittorrentAutoHandoffRestricted));
            _ = _saveSettings();
            if (value) TryStartAutomaticHandoff(); else SetAutomaticHandoffStage("Off");
        }
    }
    public string MagnetUri { get => _magnetUri; set => SetProperty(ref _magnetUri, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool LegalNoticeAccepted
    {
        get => _legalNoticeAccepted;
        set
        {
            if (!SetProperty(ref _legalNoticeAccepted, value)) return;
            _settings.TorrentLegalNoticeAccepted = value;
            _ = _saveSettings();
        }
    }
    public TorrentSessionSnapshot? Session
    {
        get => _session;
        private set
        {
            if (SetProperty(ref _session, value))
            {
                OnPropertyChanged(nameof(SessionTitle));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(HandoffDisplayPercent));
                OnPropertyChanged(nameof(HasTorrentActivity));
            }
        }
    }
    public string SessionTitle => Session?.Name ?? "Torrent oturumu yok";
    public double ProgressPercent => Session?.ProgressPercent ?? 0;
    public TorrentFileItem? SelectedFile { get => _selectedFile; set => SetProperty(ref _selectedFile, value); }
    public bool EngineAvailable => _service.IsEngineAvailable;
    public string EngineDiagnostic => _service.EngineDiagnostic;
    public string AvailabilityText => LocalizationManager.Get(EngineAvailable ? "Panel.TorrentReady" : "Panel.TorrentUnavailable");

    public Task OpenMagnetAsync(
        string magnetUri,
        int? preferredFileIndex = null,
        CancellationToken cancellationToken = default,
        string? displayTitle = null) =>
        OpenMagnetInternalAsync(magnetUri, preferredFileIndex, cancellationToken, displayTitle, propagateFailure: false);

    public Task OpenMagnetOrThrowAsync(
        string magnetUri,
        int? preferredFileIndex = null,
        CancellationToken cancellationToken = default,
        string? displayTitle = null) =>
        OpenMagnetInternalAsync(magnetUri, preferredFileIndex, cancellationToken, displayTitle, propagateFailure: true);

    public Task OpenTorrentFileAsync(
        string filePath,
        int? preferredFileIndex = null,
        CancellationToken cancellationToken = default,
        string? displayTitle = null) =>
        OpenTorrentFileInternalAsync(filePath, preferredFileIndex, cancellationToken, displayTitle, propagateFailure: false);

    public Task OpenTorrentFileOrThrowAsync(
        string filePath,
        int? preferredFileIndex = null,
        CancellationToken cancellationToken = default,
        string? displayTitle = null) =>
        OpenTorrentFileInternalAsync(filePath, preferredFileIndex, cancellationToken, displayTitle, propagateFailure: true);

    private async Task OpenMagnetInternalAsync(
        string magnetUri,
        int? preferredFileIndex,
        CancellationToken cancellationToken,
        string? displayTitle,
        bool propagateFailure)
    {
        _preferredFileIndex = preferredFileIndex;
        _preferredDisplayTitle = string.IsNullOrWhiteSpace(displayTitle) ? null : displayTitle.Trim();
        MagnetUri = magnetUri?.Trim() ?? string.Empty;
        if (propagateFailure && _preferredDisplayTitle is not null)
            SetDiscoverSourceState("Preparing", _preferredDisplayTitle);
        try
        {
            await PrepareMagnetCoreAsync(cancellationToken, propagateFailure).ConfigureAwait(true);
            if (propagateFailure && Session is null)
                throw new InvalidOperationException(LocalizationManager.Get("R5.Torrent.Discover.NoSession"));
            if (propagateFailure && _preferredDisplayTitle is not null)
            {
                _discoverSourceSessionId = Session?.SessionId;
                SetDiscoverSourceState("Ready");
            }
        }
        catch
        {
            if (propagateFailure && _preferredDisplayTitle is not null)
                SetDiscoverSourceState("Failed");
            throw;
        }
    }

    private async Task OpenTorrentFileInternalAsync(
        string filePath,
        int? preferredFileIndex,
        CancellationToken cancellationToken,
        string? displayTitle,
        bool propagateFailure)
    {
        _preferredFileIndex = preferredFileIndex;
        _preferredDisplayTitle = string.IsNullOrWhiteSpace(displayTitle) ? null : displayTitle.Trim();
        if (propagateFailure && _preferredDisplayTitle is not null)
            SetDiscoverSourceState("Preparing", _preferredDisplayTitle);
        try
        {
            await PrepareTorrentPathCoreAsync(filePath, cancellationToken, propagateFailure).ConfigureAwait(true);
            if (propagateFailure && Session is null)
                throw new InvalidOperationException(LocalizationManager.Get("R5.Torrent.Discover.NoSession"));
            if (propagateFailure && _preferredDisplayTitle is not null)
            {
                _discoverSourceSessionId = Session?.SessionId;
                SetDiscoverSourceState("Ready");
            }
        }
        catch
        {
            if (propagateFailure && _preferredDisplayTitle is not null)
                SetDiscoverSourceState("Failed");
            throw;
        }
    }

    private async Task SaveQBittorrentAsync()
    {
        if (QBittorrentBusy) return;
        try
        {
            QBittorrentBusy = true;
            SetQBittorrentConnectionState("Checking");
            var url = QBittorrentUrl.Trim();
            var username = QBittorrentUsername.Trim();
            // Empty input retains the encrypted password, including after restart.
            var password = string.IsNullOrEmpty(QBittorrentPasswordInput)
                ? await _secretStore.GetAsync(PasswordKey).ConfigureAwait(true) ?? string.Empty
                : QBittorrentPasswordInput;
            var version = await _qbClient.TestAsync(url, username, password).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(QBittorrentPasswordInput))
                await _secretStore.SetAsync(PasswordKey, password).ConfigureAwait(true);
            _settings.QBittorrentWebUiUrl = url;
            _settings.QBittorrentUsername = username;
            await _saveSettings().ConfigureAwait(true);
            QBittorrentPasswordInput = string.Empty;
            QBittorrentHasSavedPassword = true;
            _qbVersion = version;
            OnPropertyChanged(nameof(QBittorrentVersion));
            QBittorrentStatus = string.Empty;
            SetQBittorrentConnectionState(HasUnsavedQBittorrentEdits ? "Edited" : "Connected");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning("qBittorrent bağlantısı doğrulanamadı.");
            SetQBittorrentConnectionState("Unavailable");
            QBittorrentStatus = string.Empty;
        }
        finally { QBittorrentBusy = false; }
    }

    private Task SmartHandoffAsync() => PerformSmartHandoffAsync(false, null);

    private async Task PerformSmartHandoffAsync(bool automatic, string? expectedSessionId)
    {
        if (QBittorrentBusy || IsBusy || _autoHandoffInProgress) return;
        var session = Session;
        if (automatic && (!_qbAutoHandoff || _torrentPlaybackActive ||
                          session is null || session.SessionId != expectedSessionId))
            return;
        if (session is null)
        {
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Smart.NoSession");
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.TorrentDownloadDirectory))
        {
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Smart.ChooseFolder");
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.QBittorrentUsername))
        {
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Qb.Configure");
            return;
        }

        var ownershipReleased = false;
        try
        {
            _autoHandoffInProgress = true;
            QBittorrentBusy = true;
            SetAutomaticHandoffStage("Checking");
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Smart.Checking");
            // Snapshot paths correspond to the active engine directory, NOT to a
            // newly chosen setting that will take effect after restart.
            var directory = _initializedDownloadDirectory ?? DownloadDirectoryForNewSession;
            if (!string.Equals(directory, _settings.TorrentDownloadDirectory,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Settings.TorrentStorage.Restart");

            var plan = await SmartTorrentHandoffPlanner.CreateAsync(session, directory)
                .ConfigureAwait(true);
            var password = await _secretStore.GetAsync(PasswordKey).ConfigureAwait(true) ?? string.Empty;
            await _qbClient.CheckSmartHandoffAsync(
                _settings.QBittorrentWebUiUrl, _settings.QBittorrentUsername,
                password, plan).ConfigureAwait(true);

            // Preparation and qBittorrent duplicate checking are read-only.
            // Never stop the active playback without an explicit confirmation.
            if (!automatic && !_dialogs.Confirm(LocalizationManager.Get("R5.Torrent.Smart.Confirm")))
            {
                QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Smart.Cancelled");
                SetAutomaticHandoffStage("Cancelled");
                return;
            }

            // Removing with KeepAllData closes the streaming HTTP server and
            // torrent manager. qBittorrent is the sole writer from here on.
            SetAutomaticHandoffStage("Transferring");
            await _service.RemoveAsync(session.SessionId, deleteFiles: false).ConfigureAwait(true);
            ownershipReleased = true;
            ClearSessionUi();
            _preferredFileIndex = null;
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Smart.Verifying");
            SetAutomaticHandoffStage("Verifying");

            var progress = new Progress<QBittorrentHandoffProgress>(update =>
            {
                _handoffProgressPercent = Math.Clamp(update.Fraction * 100d, 0d, 100d);
                OnPropertyChanged(nameof(HandoffDisplayPercent));
                SetAutomaticHandoffStage(update.Stage);
            });
            await _qbClient.AddWithRecheckAsync(
                _settings.QBittorrentWebUiUrl, _settings.QBittorrentUsername,
                password, plan, progress: progress).ConfigureAwait(true);
            _handedOff.Add(plan.TorrentFile);
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Smart.Submitted");
            SetAutomaticHandoffStage("Started");
            if (HasDiscoverSource && session.SessionId == _discoverSourceSessionId)
                SetDiscoverSourceState("Transferred");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A qBittorrent failure after the manager has been stopped must
            // NEVER restart a competing downloader against the same files.
            // Do not log URLs, tracker passkeys, credentials or exception text.
            _logger.Warning("Akıllı torrent aktarımı tamamlanamadı.");
            // Planner and WebUI invalid-operation errors are fixed actionable
            // messages without raw tracker URLs; never expose arbitrary errors.
            var reason = exception is InvalidOperationException
                ? LocalizationManager.TranslateMessage(exception.Message)
                : LocalizationManager.Get("R5.Torrent.Smart.Failed");
            QBittorrentStatus = ownershipReleased
                ? LocalizationManager.Get("R5.Torrent.Smart.SafeStopped") + " " + reason
                : reason;
            SetAutomaticHandoffStage("Failed");
        }
        finally
        {
            QBittorrentBusy = false;
            _autoHandoffInProgress = false;
        }
    }

    private Task TransferToQBittorrentAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settings.TorrentDownloadDirectory) &&
            Session is not null)
        {
            // The common transfer button takes the same protected path as
            // Smart Handoff when shared storage is configured.
            return SmartHandoffAsync();
        }
        return TransferSourceToQBittorrentAsync(Session?.Source, CancellationToken.None);
    }

    private async Task TransferSourceToQBittorrentAsync(
        string? source, CancellationToken cancellationToken)
    {
        if (QBittorrentBusy) return;
        if (string.IsNullOrWhiteSpace(source))
        {
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Qb.NoTorrent");
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.QBittorrentUsername))
        {
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Qb.Configure");
            return;
        }
        try
        {
            QBittorrentBusy = true;
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Qb.Sending");
            var password = await _secretStore.GetAsync(PasswordKey, cancellationToken)
                .ConfigureAwait(true) ?? string.Empty;
            await _qbClient.AddAsync(
                _settings.QBittorrentWebUiUrl, _settings.QBittorrentUsername,
                password, source, cancellationToken).ConfigureAwait(true);
            _handedOff.Add(source);
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Qb.Sent");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // WebUI errors can contain tracker passkeys. Do not log exception text.
            _logger.Warning("qBittorrent aktarımı başarısız.");
            QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Qb.Failed");
        }
        finally { QBittorrentBusy = false; }
    }

    private Task AutoHandoffIfEnabledAsync(CancellationToken cancellationToken) =>
        // With a custom download folder, the new completion watcher controls
        // automatic ownership transfer. Never run concurrent downloaders.
        _qbAutoHandoff && string.IsNullOrWhiteSpace(_settings.TorrentDownloadDirectory) &&
        Session is { } current && !_handedOff.Contains(current.Source)
            ? TransferSourceToQBittorrentAsync(current.Source, cancellationToken)
            : Task.CompletedTask;

    private bool SelectedDownloadComplete(TorrentSessionSnapshot session) =>
        session.TotalLength > 0 && session.CompletedLength >= session.TotalLength &&
        session.Files.Any(file => file.IsSelected && file.IsVideo &&
                                  file.Length > 0 && file.CompletedLength >= file.Length);

    private void TryStartAutomaticHandoff()
    {
        if (!_qbAutoHandoff)
        {
            if (!_autoHandoffInProgress) SetAutomaticHandoffStage("Off");
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.TorrentDownloadDirectory))
        {
            SetAutomaticHandoffStage("Independent");
            return;
        }
        var current = Session;
        if (current is null || _autoHandoffInProgress || QBittorrentBusy) return;
        if (_autoHandoffAttemptedSessions.Contains(current.SessionId)) return;
        if (!SelectedDownloadComplete(current))
        {
            SetAutomaticHandoffStage("Downloading");
            return;
        }
        if (_torrentPlaybackActive)
        {
            SetAutomaticHandoffStage("WaitingPlayback");
            return;
        }
        if (IsBusy) return;
        if (!string.Equals(_initializedDownloadDirectory, _settings.TorrentDownloadDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            SetAutomaticHandoffStage("RestartRequired");
            return;
        }
        // Explicit opt-in permits this one automatic transition per session.
        // Failures preserve data; repeat attempts require a manual action.
        _autoHandoffAttemptedSessions.Add(current.SessionId);
        _ = PerformSmartHandoffAsync(automatic: true, expectedSessionId: current.SessionId);
    }

    public void NotifyPlayerSnapshot(string? source, bool isIdle)
    {
        if (string.IsNullOrWhiteSpace(_activeTorrentPlaybackSource)) return;
        if (!isIdle && string.Equals(source, _activeTorrentPlaybackSource,
                StringComparison.OrdinalIgnoreCase))
        {
            _observedTorrentPlayback = true;
            return;
        }
        if (!_observedTorrentPlayback) return;
        _torrentPlaybackActive = false;
        _activeTorrentPlaybackSource = null;
        _observedTorrentPlayback = false;
        TryStartAutomaticHandoff();
    }

    public void NotifyPlaybackEnded()
    {
        if (!_torrentPlaybackActive) return;
        _torrentPlaybackActive = false;
        _activeTorrentPlaybackSource = null;
        _observedTorrentPlayback = false;
        TryStartAutomaticHandoff();
    }

    private async Task EnsureInitializedAsync()
    {
        // Engines reuse their initialized storage paths. Settings changes take
        // effect on the next application launch, not during an active transfer.
        var directory = _initializedDownloadDirectory ?? DownloadDirectoryForNewSession;
        await _service.InitializeAsync(new TorrentEngineConfiguration(
            directory,
            _settings.TorrentDownloadLimitKiB,
            _settings.TorrentUploadLimitKiB,
            _settings.TorrentCacheLimitGiB,
            UsePartialFiles: string.Equals(directory, _paths.TorrentCacheDirectory,
                StringComparison.OrdinalIgnoreCase))).ConfigureAwait(true);
        _initializedDownloadDirectory ??= directory;
    }

    private async Task PrepareMagnetCoreAsync(CancellationToken externalCancellation, bool propagateFailure)
    {
        if (!LegalNoticeAccepted)
        {
            StatusText = LocalizationManager.Get("R5.Torrent.Discover.LegalRequired");
            if (propagateFailure) throw new InvalidOperationException(StatusText);
            return;
        }
        if (string.IsNullOrWhiteSpace(MagnetUri))
        {
            StatusText = LocalizationManager.Get("R5.Torrent.Discover.MagnetRequired");
            if (propagateFailure) throw new InvalidOperationException(StatusText);
            return;
        }

        var preparation = BeginPreparation(externalCancellation);
        try
        {
            await ResetActiveSessionAsync().ConfigureAwait(true);
            IsBusy = true;
            StatusText = "Torrent metadata bilgisi alınıyor…";
            await EnsureInitializedAsync().ConfigureAwait(true);
            ApplySnapshot(await _service.PrepareMagnetAsync(MagnetUri.Trim(), preparation.Token).ConfigureAwait(true));
            await AutoHandoffIfEnabledAsync(preparation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            if (Session is null) ClearSessionUi();
            StatusText = "Torrent hazırlama iptal edildi.";
            if (propagateFailure) throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Magnet hazırlanamadı.", exception);
            if (Session is null) ClearSessionUi();
            StatusText = exception.Message;
            if (propagateFailure) throw;
            _dialogs.ShowError(
                exception is InvalidDataException
                    ? exception.Message
                    : $"Magnet bağlantısı açılamadı.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
            EndPreparation(preparation);
        }
    }

    private async Task PrepareTorrentFileAsync()
    {
        var path = _dialogs.PickTorrentFile();
        if (string.IsNullOrWhiteSpace(path)) return;
        await PrepareTorrentPathCoreAsync(path, CancellationToken.None, propagateFailure: false).ConfigureAwait(true);
    }

    private async Task PrepareTorrentPathCoreAsync(
        string path,
        CancellationToken externalCancellation,
        bool propagateFailure)
    {
        if (!LegalNoticeAccepted)
        {
            StatusText = LocalizationManager.Get("R5.Torrent.Discover.LegalRequired");
            if (propagateFailure) throw new InvalidOperationException(StatusText);
            return;
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText = LocalizationManager.Get("R5.Torrent.Discover.FileMissing");
            if (propagateFailure) throw new InvalidOperationException(StatusText);
            return;
        }

        var preparation = BeginPreparation(externalCancellation);
        try
        {
            await ResetActiveSessionAsync().ConfigureAwait(true);
            IsBusy = true;
            StatusText = "Torrent dosyası okunuyor…";
            await EnsureInitializedAsync().ConfigureAwait(true);
            ApplySnapshot(await _service.PrepareTorrentFileAsync(path, preparation.Token).ConfigureAwait(true));
            await AutoHandoffIfEnabledAsync(preparation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            if (Session is null) ClearSessionUi();
            StatusText = "Torrent hazırlama iptal edildi.";
            if (propagateFailure) throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Torrent dosyası hazırlanamadı.", exception);
            if (Session is null) ClearSessionUi();
            StatusText = exception.Message;
            if (propagateFailure) throw;
            _dialogs.ShowError(
                exception is InvalidDataException
                    ? exception.Message
                    : $"Torrent dosyası açılamadı.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
            EndPreparation(preparation);
        }
    }

    private async Task StartSelectedAsync()
    {
        if (Session is null || SelectedFile is null) { StatusText = "Önce torrent içinden bir video seçin."; return; }
        try
        {
            IsBusy = true;
            StatusText = "Oynatma için ilk parçalar hazırlanıyor…";
            var source = await _service.StartSelectedFileAsync(Session.SessionId, SelectedFile.Index).ConfigureAwait(true);
            var displayTitle = string.IsNullOrWhiteSpace(_preferredDisplayTitle)
                ? source.DisplayName
                : _preferredDisplayTitle;
            _activeTorrentPlaybackSource = source.LocalPath;
            _torrentPlaybackActive = true;
            _observedTorrentPlayback = false;
            await _openSource(source.LocalPath, displayTitle).ConfigureAwait(true);
            StatusText = $"Torrent oynatılıyor: {displayTitle}";
        }
        catch (Exception exception)
        {
            _torrentPlaybackActive = false;
            _activeTorrentPlaybackSource = null;
            _observedTorrentPlayback = false;
            _logger.Error("Torrent videosu başlatılamadı.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"Torrent videosu başlatılamadı.\n\n{exception.Message}");
        }
        finally
        {
            IsBusy = false;
            TryStartAutomaticHandoff();
        }
    }

    private async Task PauseAsync()
    {
        if (Session is null) return;
        try { await _service.PauseAsync(Session.SessionId).ConfigureAwait(true); }
        catch (Exception exception) { StatusText = exception.Message; }
    }

    private async Task ResumeAsync()
    {
        if (Session is null) return;
        try { await _service.ResumeAsync(Session.SessionId).ConfigureAwait(true); }
        catch (Exception exception) { StatusText = exception.Message; }
    }

    private async Task RemoveAsync()
    {
        _prepareCancellation?.Cancel();

        var session = Session;
        if (session is null)
        {
            ClearSessionUi();
            _preferredFileIndex = null;
            StatusText = "Torrent hazırlama iptal edildi.";
            return;
        }

        // User-selected download folders are not disposable Player caches.
        // Never delete data from a folder qBittorrent may be seeding from.
        var customDirectory = !string.Equals(
            _initializedDownloadDirectory ?? DownloadDirectoryForNewSession,
            _paths.TorrentCacheDirectory, StringComparison.OrdinalIgnoreCase);
        var confirmationKey = customDirectory
            ? "Settings.TorrentStorage.CloseKeep"
            : "Settings.TorrentStorage.CloseDelete";
        var closeRequested = _dialogs.Confirm(LocalizationManager.Get(confirmationKey));
        if (!closeRequested) return;
        var delete = !customDirectory && _dialogs.Confirm(
            LocalizationManager.Get("Settings.TorrentStorage.ConfirmDelete"));
        try
        {
            await _service.RemoveAsync(session.SessionId, delete).ConfigureAwait(true);
            ClearSessionUi();
            _preferredFileIndex = null;
            StatusText = "Torrent oturumu kapatıldı.";
        }
        catch (OperationCanceledException)
        {
            ClearSessionUi();
            _preferredFileIndex = null;
            StatusText = "Torrent oturumu kapatıldı.";
        }
        catch (Exception exception)
        {
            ClearSessionUi();
            _preferredFileIndex = null;
            StatusText = exception.Message;
        }
    }

    private void OnSessionChanged(object? sender, TorrentSessionSnapshot snapshot) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(TorrentSessionSnapshot snapshot)
    {
        if (snapshot.State == TorrentSessionState.Removed)
        {
            if (Session?.SessionId == snapshot.SessionId)
            {
                ClearSessionUi();
            }
            return;
        }

        if (Session is not null && Session.SessionId != snapshot.SessionId) return;
        Session = snapshot;
        _handoffProgressPercent = snapshot.ProgressPercent;
        Files.Clear();
        foreach (var file in snapshot.Files.Where(file => file.IsVideo).OrderByDescending(file => file.Length)) Files.Add(file);
        SelectedFile = Files.FirstOrDefault(file => file.Index == _preferredFileIndex)
                       ?? Files.FirstOrDefault(file => file.Index == SelectedFile?.Index)
                       ?? Files.FirstOrDefault();
        if (snapshot.State is TorrentSessionState.Ready or TorrentSessionState.Downloading or TorrentSessionState.Complete)
        {
            _preferredFileIndex = null;
        }
        StatusText = snapshot.StatusLabel;
        TryStartAutomaticHandoff();
    }

    private CancellationTokenSource BeginPreparation(CancellationToken externalCancellation)
    {
        _prepareCancellation?.Cancel();
        var current = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        _prepareCancellation = current;
        return current;
    }

    private void EndPreparation(CancellationTokenSource current)
    {
        if (ReferenceEquals(_prepareCancellation, current))
        {
            _prepareCancellation = null;
        }
        current.Dispose();
    }

    private async Task ResetActiveSessionAsync()
    {
        var existing = Session;
        if (existing is null)
        {
            ClearSessionUi();
            return;
        }

        // A new Discover selection must never dispose the HTTP streaming
        // manager currently serving mpv. The user can change sources after
        // playback has actually stopped.
        if (_torrentPlaybackActive)
            throw new InvalidOperationException(
                LocalizationManager.Get("R5.Torrent.Discover.ActivePlayback"));

        try
        {
            await _service.RemoveAsync(existing.SessionId, deleteFiles: false).ConfigureAwait(true);
            // The removed event also clears the UI, but do not leave a stale
            // session in place if its Dispatcher callback has not run yet.
            if (Session?.SessionId == existing.SessionId)
                ClearSessionUi();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the session visible/recoverable if the engine unregister
            // fails. Never continue by registering another manager blindly.
            _logger.Warning("Önceki torrent oturumu motor tarafından kaldırılamadı; içerik korundu.");
            throw new InvalidOperationException(
                LocalizationManager.Get("R5.Torrent.Discover.CleanupFailed"), exception);
        }
    }

    private void ClearSessionUi()
    {
        Files.Clear();
        SelectedFile = null;
        Session = null;
        _torrentPlaybackActive = false;
        _activeTorrentPlaybackSource = null;
        _observedTorrentPlayback = false;
    }

    private void OpenCache()
    {
        var directory = _initializedDownloadDirectory ?? DownloadDirectoryForNewSession;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(SessionTitle));
        // Do not reset a verified connection to "Disconnected" on language change.
        OnPropertyChanged(nameof(QBittorrentConnectionStatus));
        OnPropertyChanged(nameof(AutomaticHandoffStatus));
        OnPropertyChanged(nameof(DiscoverSourceTitle));
        OnPropertyChanged(nameof(DiscoverSourceStatusText));
    }

    public async ValueTask DisposeAsync()
    {
        _prepareCancellation?.Cancel();
        _service.SessionChanged -= OnSessionChanged;
        await _service.DisposeAsync().ConfigureAwait(false);
        _prepareCancellation?.Dispose();
        _prepareCancellation = null;
    }
}
