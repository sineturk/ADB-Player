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
        AppPaths paths)
    {
        _service = service;
        _settings = settings;
        _saveSettings = saveSettings;
        _openSource = openSource;
        _dialogs = dialogs;
        _logger = logger;
        _paths = paths;
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
    public TorrentSessionSnapshot? Session { get => _session; private set { if (SetProperty(ref _session, value)) { OnPropertyChanged(nameof(SessionTitle)); OnPropertyChanged(nameof(ProgressPercent)); } } }
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
        await PrepareMagnetCoreAsync(cancellationToken, propagateFailure).ConfigureAwait(true);
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
        await PrepareTorrentPathCoreAsync(filePath, cancellationToken, propagateFailure).ConfigureAwait(true);
    }

    private async Task EnsureInitializedAsync()
    {
        await _service.InitializeAsync(new TorrentEngineConfiguration(
            _paths.TorrentCacheDirectory,
            _settings.TorrentDownloadLimitKiB,
            _settings.TorrentUploadLimitKiB,
            _settings.TorrentCacheLimitGiB)).ConfigureAwait(true);
    }

    private async Task PrepareMagnetCoreAsync(CancellationToken externalCancellation, bool propagateFailure)
    {
        if (!LegalNoticeAccepted) { StatusText = "Magnet kullanımı için yasal kullanım onayını işaretleyin."; return; }
        if (string.IsNullOrWhiteSpace(MagnetUri)) { StatusText = "Bir magnet bağlantısı girin."; return; }

        var preparation = BeginPreparation(externalCancellation);
        try
        {
            await ResetActiveSessionAsync().ConfigureAwait(true);
            IsBusy = true;
            StatusText = "Torrent metadata bilgisi alınıyor…";
            await EnsureInitializedAsync().ConfigureAwait(true);
            ApplySnapshot(await _service.PrepareMagnetAsync(MagnetUri.Trim(), preparation.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            ClearSessionUi();
            StatusText = "Torrent hazırlama iptal edildi.";
            if (propagateFailure) throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Magnet hazırlanamadı.", exception);
            ClearSessionUi();
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
        if (!LegalNoticeAccepted) { StatusText = "Torrent kullanımı için yasal kullanım onayını işaretleyin."; return; }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { StatusText = "Torrent dosyası bulunamadı."; return; }

        var preparation = BeginPreparation(externalCancellation);
        try
        {
            await ResetActiveSessionAsync().ConfigureAwait(true);
            IsBusy = true;
            StatusText = "Torrent dosyası okunuyor…";
            await EnsureInitializedAsync().ConfigureAwait(true);
            ApplySnapshot(await _service.PrepareTorrentFileAsync(path, preparation.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            ClearSessionUi();
            StatusText = "Torrent hazırlama iptal edildi.";
            if (propagateFailure) throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Torrent dosyası hazırlanamadı.", exception);
            ClearSessionUi();
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
            await _openSource(source.LocalPath, displayTitle).ConfigureAwait(true);
            StatusText = $"Torrent oynatılıyor: {displayTitle}";
        }
        catch (Exception exception)
        {
            _logger.Error("Torrent videosu başlatılamadı.", exception);
            StatusText = exception.Message;
            _dialogs.ShowError($"Torrent videosu başlatılamadı.\n\n{exception.Message}");
        }
        finally { IsBusy = false; }
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

        var delete = _dialogs.Confirm("Torrent oturumu kapatılsın ve indirilen geçici dosyalar silinsin mi?");
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
        ClearSessionUi();
        if (existing is null)
        {
            return;
        }

        try
        {
            await _service.RemoveAsync(existing.SessionId, deleteFiles: false).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning($"Önceki torrent oturumu temizlenemedi: {exception.Message}");
        }
    }

    private void ClearSessionUi()
    {
        Files.Clear();
        SelectedFile = null;
        Session = null;
    }

    private void OpenCache()
    {
        Directory.CreateDirectory(_paths.TorrentCacheDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.TorrentCacheDirectory) { UseShellExecute = true });
    }

    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(SessionTitle));
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
