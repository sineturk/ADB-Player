using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Streaming;

namespace AltyaziDB.Player.Torrent;

/// <summary>
/// Seek konumunu BitTorrent parca onceligine donusturen yonetilen akış motoru.
/// CreateHttpStreamAsync tarafindan uretilen localhost Range URL'si libmpv'ye
/// verilir; dosyanin tamamlanmasi beklenmez.
/// </summary>
public sealed class MonoTorrentStreamingService : ITorrentService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".vob"
    };

    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    // A TorrentManager may already be registered while metadata is still loading,
    // or when an earlier cleanup failed. Guard registry ownership across Add/Remove.
    private readonly SemaphoreSlim _registryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ClientEngine? _engine;
    private TorrentEngineConfiguration? _configuration;
    private Task? _pollTask;
    private bool _disposed;

    public MonoTorrentStreamingService(IAppLogger logger)
    {
        _logger = logger;
        IsEngineAvailable = true;
        EngineDiagnostic = "MonoTorrent yerleşik akış motoru hazır.";
        _logger.Info(EngineDiagnostic);
    }

    public event EventHandler<TorrentSessionSnapshot>? SessionChanged;

    public bool IsEngineAvailable { get; }
    public string EngineDiagnostic { get; }

    public Task InitializeAsync(TorrentEngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_engine is not null) return Task.CompletedTask;

        Directory.CreateDirectory(configuration.CacheDirectory);
        var engineCache = Path.Combine(configuration.CacheDirectory, ".engine");
        Directory.CreateDirectory(engineCache);
        var streamPort = ReserveTcpPort();
        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = engineCache,
            HttpStreamingPrefix = $"http://127.0.0.1:{streamPort}/",
            MaximumDownloadRate = Math.Max(0, configuration.DownloadLimitKiB) * 1024,
            MaximumUploadRate = Math.Max(0, configuration.UploadLimitKiB) * 1024,

            // MonoTorrent 3.0.2 defaults are intentionally conservative for a
            // general-purpose client (150 global connections / 8 half-open).
            // Player streaming benefits from a faster initial peer ramp-up.
            MaximumConnections = 240,
            MaximumHalfOpenConnections = 16,
            ConnectionTimeout = TimeSpan.FromSeconds(8),

            // A larger write cache reduces short stalls while streaming pieces
            // are being prioritised around the current playback position.
            DiskCacheBytes = 16 * 1024 * 1024,

            // User-selected shared folders retain original filenames for qBittorrent recheck.
            UsePartialFiles = configuration.UsePartialFiles,
            AllowLocalPeerDiscovery = true,
            AllowPortForwarding = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true
        };
        _engine = new ClientEngine(settings.ToSettings());
        _configuration = configuration;
        _pollTask = Task.Run(() => PollLoopAsync(_lifetime.Token));
        _logger.Info($"MonoTorrent HTTP Range sunucusu hazır: 127.0.0.1:{streamPort}");
        return Task.CompletedTask;
    }

    public async Task<TorrentSessionSnapshot> PrepareMagnetAsync(string magnetUri, CancellationToken cancellationToken = default)
    {
        var engine = EnsureInitialized();
        if (!Uri.TryCreate(magnetUri.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Geçerli bir magnet bağlantısı girin.");

        var magnet = MagnetLink.Parse(magnetUri.Trim());
        var (session, created) = await RegisterStreamingSessionAsync(
            magnet.InfoHashes, magnetUri.Trim(),
            () => engine.AddStreamingAsync(
                magnet, _configuration!.CacheDirectory, CreateStreamingTorrentSettings()),
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (created)
                Raise(CreateSnapshot(session, TorrentSessionState.Metadata));
            // Never stop/restart an already downloading or playing manager.
            if (session.Manager.State == TorrentState.Stopped)
                await session.Manager.StartAsync().ConfigureAwait(false);
            await session.Manager.WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (created)
                await DisableUnselectedFilesAsync(session.Manager, selected: null).ConfigureAwait(false);
            var snapshot = CreateSnapshot(session, TorrentSessionState.Ready);
            TorrentContentSafety.ValidatePlayableVideo(snapshot.Files);
            LogBlockedSidecars(snapshot);
            Raise(snapshot);
            return snapshot;
        }
        catch (Exception exception)
        {
            // A reused session may still be playing or downloading on another
            // attempt. Never remove or delete it when this attempt times out.
            if (created)
                await RemoveFailedSessionAsync(
                    session, deleteData: exception is InvalidDataException).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TorrentSessionSnapshot> PrepareTorrentFileAsync(string torrentFilePath, CancellationToken cancellationToken = default)
    {
        var engine = EnsureInitialized();
        if (!File.Exists(torrentFilePath)) throw new FileNotFoundException("Torrent dosyası bulunamadı.", torrentFilePath);
        var torrent = await MonoTorrent.Torrent.LoadAsync(torrentFilePath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var (session, created) = await RegisterStreamingSessionAsync(
            torrent.InfoHashes, torrentFilePath,
            () => engine.AddStreamingAsync(
                torrent, _configuration!.CacheDirectory, CreateStreamingTorrentSettings()),
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (created)
                await DisableUnselectedFilesAsync(session.Manager, selected: null).ConfigureAwait(false);
            if (session.Manager.State == TorrentState.Stopped)
                await session.Manager.StartAsync().ConfigureAwait(false);
            await session.Manager.WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = CreateSnapshot(session, TorrentSessionState.Ready);
            TorrentContentSafety.ValidatePlayableVideo(snapshot.Files);
            LogBlockedSidecars(snapshot);
            Raise(snapshot);
            return snapshot;
        }
        catch (Exception exception)
        {
            if (created)
                await RemoveFailedSessionAsync(
                    session, deleteData: exception is InvalidDataException).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(SessionState Session, bool Created)> RegisterStreamingSessionAsync(
        InfoHashes infoHashes,
        string source,
        Func<Task<TorrentManager>> addStreaming,
        CancellationToken cancellationToken)
    {
        await _registryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var engine = EnsureInitialized();
            var existing = engine.Torrents.FirstOrDefault(
                candidate => candidate.InfoHashes.Equals(infoHashes));
            if (existing is not null)
            {
                // Avoid adopting a non-streaming manager; it cannot serve HTTP
                // Range requests to the media player.
                if (existing.StreamProvider is null)
                    throw new InvalidOperationException(
                        "Bu torrent zaten akış desteklemeyen bir oturumda kayıtlı.");

                var tracked = _sessions.Values.FirstOrDefault(
                    session => ReferenceEquals(session.Manager, existing));
                if (tracked is not null)
                    return (tracked, false);

                // Previous cleanup may have failed after the view model forgot
                // its session. Recover ownership without touching existing files.
                var recovered = new SessionState(Guid.NewGuid().ToString("N"), source, existing);
                _sessions[recovered.Id] = recovered;
                _logger.Warning("MonoTorrent kaydı var ancak panel oturumu yoktu; mevcut yönetici korundu ve yeniden bağlandı.");
                return (recovered, false);
            }

            var manager = await addStreaming().ConfigureAwait(false);
            var registered = new SessionState(Guid.NewGuid().ToString("N"), source, manager);
            _sessions[registered.Id] = registered;
            return (registered, true);
        }
        finally
        {
            _registryGate.Release();
        }
    }

    public async Task<TorrentPlaybackSource> StartSelectedFileAsync(
        string sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId);
        var files = session.Manager.Files;
        if (fileIndex < 1 || fileIndex > files.Count)
            throw new InvalidOperationException("Oynatılabilir torrent dosyası seçilmedi.");
        var selected = files[fileIndex - 1];
        if (!IsVideo(selected.Path)) throw new InvalidOperationException("Seçilen torrent öğesi video değil.");
        if (TorrentContentSafety.IsBlockedFileName(selected.Path))
            throw new InvalidDataException("Riskli torrent dosyaları oynatma için seçilemez.");

        await session.StreamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.HttpStream?.Dispose();
            session.HttpStream = null;
            await DisableUnselectedFilesAsync(session.Manager, selected).ConfigureAwait(false);
            await session.Manager.StartAsync().ConfigureAwait(false);

            // MonoTorrent 3.0.2'nin streaming varsayilanini kullan:
            // ilk ve son gerekli piece'ler tamamlanmadan HTTP URL'sini mpv'ye verme.
            // Bu, 0:00'da gri ekranla ilk piece'i beklemek yerine kisa bir
            // "oynatma icin hazirlaniyor" asamasi olusturur ve container probing'i
            // (ozellikle dosya sonuna bakan formatlarda) daha kararlı hale getirir.
            session.HttpStream = await session.Manager.StreamProvider!
                .CreateHttpStreamAsync(selected, prebuffer: true, cancellationToken)
                .ConfigureAwait(false);
            Raise(CreateSnapshot(session, TorrentSessionState.Downloading));
            return new TorrentPlaybackSource(session.Id, fileIndex, session.HttpStream.FullUri, Path.GetFileName(selected.Path));
        }
        finally
        {
            session.StreamGate.Release();
        }
    }

    public async Task PauseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        await session.Manager.PauseAsync().ConfigureAwait(false);
        Raise(CreateSnapshot(session, TorrentSessionState.Paused));
    }

    public async Task ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        await session.Manager.StartAsync().ConfigureAwait(false);
        Raise(CreateSnapshot(session, TorrentSessionState.Downloading));
    }

    public async Task RemoveAsync(string sessionId, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        // Do not discard the session from _sessions until the underlying engine
        // confirms removal. Otherwise a failed cleanup leaves an orphan manager
        // and the next AddStreamingAsync fails as "already registered".
        await _registryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;
            await session.StreamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var removed = false;
            try
            {
                var removedSnapshot = CreateSnapshot(session, TorrentSessionState.Removed);
                session.HttpStream?.Dispose();
                session.HttpStream = null;
                // Do not swallow removal errors and clear the UI as if removal worked.
                await session.Manager.StopAsync().ConfigureAwait(false);
                if (_engine is not null)
                    await _engine.RemoveAsync(
                        session.Manager,
                        deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.KeepAllData).ConfigureAwait(false);
                _sessions.TryRemove(sessionId, out _);
                removed = true;
                Raise(removedSnapshot);
            }
            finally
            {
                session.StreamGate.Release();
                if (removed)
                    session.StreamGate.Dispose();
            }
        }
        finally
        {
            _registryGate.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var session in _sessions.Values)
                {
                    try { Raise(CreateSnapshot(session)); }
                    catch (Exception exception) { _logger.Warning($"Torrent durumu okunamadı: {exception.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private TorrentSessionSnapshot CreateSnapshot(SessionState session, TorrentSessionState? forcedState = null)
    {
        var manager = session.Manager;
        var files = manager.Files.Select((file, index) =>
        {
            var completed = (long)Math.Clamp(file.Length * file.BitField.PercentComplete / 100d, 0, file.Length);
            return new TorrentFileItem(
                index + 1,
                Path.GetFileName(file.Path),
                file.FullPath,
                file.Length,
                completed,
                file.Priority != Priority.DoNotDownload,
                IsVideo(file.Path));
        }).ToArray();
        var total = files.Where(file => file.IsSelected).Sum(file => file.Length);
        var completedTotal = files.Where(file => file.IsSelected).Sum(file => file.CompletedLength);
        var state = forcedState ?? MapState(manager.State.ToString());
        var error = manager.Error is null ? null : manager.Error.Exception?.Message ?? manager.Error.Reason.ToString();
        return new TorrentSessionSnapshot(
            session.Id,
            manager.Name,
            session.Source,
            error is null ? state : TorrentSessionState.Error,
            files,
            completedTotal,
            total,
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.Peers.Seeds,
            error);
    }

    private static TorrentSessionState MapState(string value) => value switch
    {
        "Metadata" => TorrentSessionState.Metadata,
        "Paused" => TorrentSessionState.Paused,
        "Downloading" or "Hashing" or "Starting" or "FetchingHashes" => TorrentSessionState.Downloading,
        "Seeding" => TorrentSessionState.Complete,
        "Error" => TorrentSessionState.Error,
        _ => TorrentSessionState.Ready
    };

    private static async Task DisableUnselectedFilesAsync(TorrentManager manager, ITorrentManagerFile? selected)
    {
        foreach (var file in manager.Files)
        {
            var priority = ReferenceEquals(file, selected) ? Priority.High : Priority.DoNotDownload;
            if (file.Priority != priority)
                await manager.SetFilePriorityAsync(file, priority).ConfigureAwait(false);
        }
    }

    private TorrentSettings CreateStreamingTorrentSettings()
    {
        var configuration = _configuration
            ?? throw new InvalidOperationException("Torrent akış motoru başlatılmadı.");

        return new TorrentSettingsBuilder
        {
            // MonoTorrent 3.0.2 defaults: DHT/PEX acik, torrent basina 60 baglanti.
            // Streaming profilinde daha genis bir peer havuzu ve sinirsiz indirme
            // kullanilir; kullanicinin global hiz limitleri EngineSettings'te kalir.
            AllowDht = true,
            AllowPeerExchange = true,
            MaximumConnections = 100,
            MaximumDownloadRate = 0,
            MaximumUploadRate = 0,
            UploadSlots = 8,
            CreateContainingDirectory = true
        }.ToSettings();
    }

    private async Task RemoveFailedSessionAsync(SessionState session, bool deleteData = false)
    {
        // Preserve ownership if the engine refuses to unregister the manager;
        // the next attempt can recover it by its exact info-hash.
        try
        {
            await RemoveAsync(session.Id, deleteFiles: deleteData).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Do not log magnet URLs, tracker tokens or private passkeys.
            _logger.Warning("Torrent metadata hatasından sonra motor temizliği tamamlanamadı; kayıt ve dosyalar korundu.");
        }
    }

    private void LogBlockedSidecars(TorrentSessionSnapshot snapshot)
    {
        var blocked = TorrentContentSafety.GetBlockedFiles(snapshot.Files);
        if (blocked.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", blocked.Take(4).Select(file => file.Name));
        var suffix = blocked.Count > 4 ? $" (+{blocked.Count - 4})" : string.Empty;
        _logger.Warning(
            $"Torrentte {blocked.Count} riskli yan dosya indirme dışı bırakıldı: {names}{suffix}");
    }

    private ClientEngine EnsureInitialized() =>
        _engine ?? throw new InvalidOperationException("Torrent akış motoru başlatılmadı.");

    private SessionState GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new InvalidOperationException("Torrent oturumu bulunamadı.");

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    private void Raise(TorrentSessionSnapshot snapshot) => SessionChanged?.Invoke(this, snapshot);

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask.ConfigureAwait(false); } catch { }
        }
        foreach (var id in _sessions.Keys.ToArray())
        {
            try { await RemoveAsync(id, deleteFiles: false).ConfigureAwait(false); } catch { }
        }
        if (_engine is not null)
        {
            try { await _engine.StopAllAsync().ConfigureAwait(false); } catch { }
            _engine.Dispose();
        }
        _registryGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed class SessionState(string id, string source, TorrentManager manager)
    {
        public string Id { get; } = id;
        public string Source { get; } = source;
        public TorrentManager Manager { get; } = manager;
        public SemaphoreSlim StreamGate { get; } = new(1, 1);
        public IHttpStream? HttpStream { get; set; }
    }
}
