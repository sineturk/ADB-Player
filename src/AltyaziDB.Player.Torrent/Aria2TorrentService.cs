using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Torrent;

public sealed class Aria2TorrentService : ITorrentService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".vob"
    };

    private readonly IAppLogger _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollers = new(StringComparer.Ordinal);
    private Process? _process;
    private Uri? _rpcUri;
    private string _secret = string.Empty;
    private TorrentEngineConfiguration? _configuration;
    private long _rpcId;
    private bool _disposed;

    public Aria2TorrentService(IAppLogger logger)
    {
        _logger = logger;
        ExecutablePath = FindExecutable();
        IsEngineAvailable = !string.IsNullOrWhiteSpace(ExecutablePath);
        EngineDiagnostic = IsEngineAvailable
            ? $"aria2c bulundu: {ExecutablePath}"
            : "aria2c.exe bulunamadı. native\\torrent\\aria2c.exe konumuna yerleştirin.";
        if (IsEngineAvailable) _logger.Info(EngineDiagnostic);
        else _logger.Warning(EngineDiagnostic);
    }

    public event EventHandler<TorrentSessionSnapshot>? SessionChanged;

    public bool IsEngineAvailable { get; }
    public string EngineDiagnostic { get; }
    public string? ExecutablePath { get; }

    public async Task InitializeAsync(TorrentEngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsEngineAvailable || string.IsNullOrWhiteSpace(ExecutablePath))
            throw new InvalidOperationException(EngineDiagnostic);
        if (_process is { HasExited: false }) return;

        Directory.CreateDirectory(configuration.CacheDirectory);
        _configuration = configuration;
        _secret = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var port = GetFreePort();
        _rpcUri = new Uri($"http://127.0.0.1:{port}/jsonrpc");
        var arguments = BuildArguments(configuration, port, _secret);
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("aria2c işlemi başlatılamadı.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _logger.Warning($"aria2c işlemi kapandı. Kod: {_process?.ExitCode}");
        _ = DrainProcessStreamAsync(_process.StandardOutput, "aria2");
        _ = DrainProcessStreamAsync(_process.StandardError, "aria2-error");

        Exception? lastError = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited) throw new InvalidOperationException($"aria2c başlatılamadı. Çıkış kodu: {_process.ExitCode}");
            try
            {
                _ = await RpcAsync("aria2.getVersion", [], cancellationToken).ConfigureAwait(false);
                _logger.Info($"aria2 JSON-RPC hazır: {_rpcUri}");
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("aria2 JSON-RPC zamanında hazır olmadı.", lastError);
    }

    public async Task<TorrentSessionSnapshot> PrepareMagnetAsync(string magnetUri, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!Uri.TryCreate(magnetUri.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Geçerli bir magnet bağlantısı girin.");

        var options = BaseTorrentOptions();
        options["bt-save-metadata"] = "true";
        options["follow-torrent"] = "mem";
        options["pause"] = "false";
        options["pause-metadata"] = "true";
        var metadataGid = await RpcStringAsync("aria2.addUri", [new[] { magnetUri.Trim() }, options], cancellationToken).ConfigureAwait(false);
        var sessionId = Guid.NewGuid().ToString("N");
        var preparing = new TorrentSessionSnapshot(sessionId, MagnetName(uri), magnetUri.Trim(), TorrentSessionState.Preparing, []);
        _sessions[sessionId] = new SessionState(sessionId, metadataGid, magnetUri.Trim(), preparing.Name, true);
        Raise(preparing);

        var contentGid = await WaitForMetadataAsync(metadataGid, cancellationToken).ConfigureAwait(false);
        try { await RpcAsync("aria2.pause", [contentGid], cancellationToken).ConfigureAwait(false); }
        catch { }
        var state = _sessions[sessionId] with { Gid = contentGid };
        _sessions[sessionId] = state;
        var snapshot = await ReadSnapshotAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            TorrentContentSafety.ValidatePlayableVideo(snapshot.Files);
        }
        catch
        {
            try { await RemoveAsync(sessionId, deleteFiles: true, cancellationToken).ConfigureAwait(false); } catch { }
            throw;
        }
        snapshot = snapshot with { State = TorrentSessionState.Ready };
        Raise(snapshot);
        StartPolling(state);
        return snapshot;
    }

    public async Task<TorrentSessionSnapshot> PrepareTorrentFileAsync(string torrentFilePath, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!File.Exists(torrentFilePath)) throw new FileNotFoundException("Torrent dosyası bulunamadı.", torrentFilePath);
        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(torrentFilePath, cancellationToken).ConfigureAwait(false));
        var options = BaseTorrentOptions();
        options["pause"] = "true";
        var gid = await RpcStringAsync("aria2.addTorrent", [base64, Array.Empty<string>(), options], cancellationToken).ConfigureAwait(false);
        var sessionId = Guid.NewGuid().ToString("N");
        var state = new SessionState(sessionId, gid, torrentFilePath, Path.GetFileNameWithoutExtension(torrentFilePath), false);
        _sessions[sessionId] = state;
        var snapshot = await WaitForTorrentFilesAsync(state, cancellationToken).ConfigureAwait(false);
        if (snapshot.Files.All(file => !file.IsVideo))
            throw new InvalidOperationException("Torrent içinde desteklenen bir video dosyası bulunamadı.");
        snapshot = snapshot with { State = TorrentSessionState.Ready };
        Raise(snapshot);
        StartPolling(state);
        return snapshot;
    }

    public async Task<TorrentPlaybackSource> StartSelectedFileAsync(string sessionId, int fileIndex, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var state = GetSession(sessionId);
        var before = await ReadSnapshotAsync(state, cancellationToken).ConfigureAwait(false);
        var selected = before.Files.FirstOrDefault(file => file.Index == fileIndex && file.IsVideo)
                       ?? throw new InvalidOperationException("Oynatılabilir torrent dosyası seçilmedi.");

        await RpcAsync("aria2.changeOption", [state.Gid, new Dictionary<string, string>
        {
            ["select-file"] = selected.Index.ToString(CultureInfo.InvariantCulture),
            ["bt-prioritize-piece"] = "head=32M,tail=8M",
            ["file-allocation"] = "none"
        }], cancellationToken).ConfigureAwait(false);
        try { await RpcAsync("aria2.unpause", [state.Gid], cancellationToken).ConfigureAwait(false); }
        catch (TorrentRpcException exception) when (exception.Message.Contains("not paused", StringComparison.OrdinalIgnoreCase)) { }

        Raise(before with { State = TorrentSessionState.Downloading });
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await ReadSnapshotAsync(state, cancellationToken).ConfigureAwait(false);
            Raise(snapshot);
            var current = snapshot.Files.FirstOrDefault(file => file.Index == selected.Index);
            if (current is not null && File.Exists(current.Path))
            {
                var length = 0L;
                try { length = new FileInfo(current.Path).Length; } catch { }
                if (current.CompletedLength >= 512 * 1024 || length >= 512 * 1024 || snapshot.State == TorrentSessionState.Complete)
                    return new TorrentPlaybackSource(sessionId, current.Index, current.Path, current.Name);
            }
            if (snapshot.State == TorrentSessionState.Error)
                throw new InvalidOperationException(snapshot.ErrorMessage ?? "Torrent indirme hatası.");
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Torrent videosunun ilk parçaları zamanında hazırlanamadı.");
    }

    public async Task PauseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var state = GetSession(sessionId);
        await RpcAsync("aria2.pause", [state.Gid], cancellationToken).ConfigureAwait(false);
        Raise((await ReadSnapshotAsync(state, cancellationToken).ConfigureAwait(false)) with { State = TorrentSessionState.Paused });
    }

    public async Task ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var state = GetSession(sessionId);
        await RpcAsync("aria2.unpause", [state.Gid], cancellationToken).ConfigureAwait(false);
        Raise((await ReadSnapshotAsync(state, cancellationToken).ConfigureAwait(false)) with { State = TorrentSessionState.Downloading });
    }

    public async Task RemoveAsync(string sessionId, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(sessionId, out var state)) return;
        if (_pollers.TryRemove(sessionId, out var poller))
        {
            poller.Cancel();
            poller.Dispose();
        }
        TorrentSessionSnapshot? snapshot = null;
        try { snapshot = await ReadSnapshotAsync(state, cancellationToken).ConfigureAwait(false); } catch { }
        try { await RpcAsync("aria2.forceRemove", [state.Gid], cancellationToken).ConfigureAwait(false); } catch { }
        try { await RpcAsync("aria2.removeDownloadResult", [state.Gid], cancellationToken).ConfigureAwait(false); } catch { }
        if (deleteFiles && snapshot is not null)
        {
            foreach (var file in snapshot.Files)
            {
                try { if (File.Exists(file.Path)) File.Delete(file.Path); } catch { }
                try { if (File.Exists(file.Path + ".aria2")) File.Delete(file.Path + ".aria2"); } catch { }
            }
        }
        Raise((snapshot ?? new TorrentSessionSnapshot(sessionId, state.Name, state.Source, TorrentSessionState.Removed, [])) with
        {
            State = TorrentSessionState.Removed
        });
    }

    private async Task<string> WaitForMetadataAsync(string metadataGid, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TellStatusAsync(metadataGid, cancellationToken).ConfigureAwait(false);
            var error = ReadString(result, "errorMessage");
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
            if (result.TryGetProperty("followedBy", out var followed) && followed.ValueKind == JsonValueKind.Array)
            {
                var gid = followed.EnumerateArray().Select(item => item.GetString()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                if (!string.IsNullOrWhiteSpace(gid)) return gid;
            }
            if (HasFiles(result) && result.TryGetProperty("bittorrent", out _)) return metadataGid;
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Torrent metadata bilgisi zamanında alınamadı.");
    }

    private async Task<TorrentSessionSnapshot> WaitForTorrentFilesAsync(SessionState state, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await ReadSnapshotAsync(state, cancellationToken).ConfigureAwait(false);
            if (snapshot.Files.Count > 0) return snapshot;
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Torrent dosya listesi okunamadı.");
    }

    private void StartPolling(SessionState state)
    {
        if (_pollers.ContainsKey(state.SessionId)) return;
        var cancellation = new CancellationTokenSource();
        if (!_pollers.TryAdd(state.SessionId, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested && !_disposed)
            {
                try
                {
                    if (_sessions.TryGetValue(state.SessionId, out var current))
                    {
                        var snapshot = await ReadSnapshotAsync(current, cancellation.Token).ConfigureAwait(false);
                        Raise(snapshot);
                        if (snapshot.State is TorrentSessionState.Complete or TorrentSessionState.Error or TorrentSessionState.Removed) break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception exception) { _logger.Warning($"Torrent durum güncellemesi alınamadı: {exception.Message}"); }
                try { await Task.Delay(1000, cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
    }

    private async Task<TorrentSessionSnapshot> ReadSnapshotAsync(SessionState state, CancellationToken cancellationToken)
    {
        var result = await TellStatusAsync(state.Gid, cancellationToken).ConfigureAwait(false);
        var status = ReadString(result, "status");
        var torrentState = status switch
        {
            "active" => TorrentSessionState.Downloading,
            "waiting" => TorrentSessionState.Metadata,
            "paused" => TorrentSessionState.Paused,
            "complete" => TorrentSessionState.Complete,
            "error" => TorrentSessionState.Error,
            "removed" => TorrentSessionState.Removed,
            _ => TorrentSessionState.Preparing
        };
        var files = new List<TorrentFileItem>();
        if (result.TryGetProperty("files", out var rawFiles) && rawFiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in rawFiles.EnumerateArray())
            {
                var urisSelected = file.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.String
                    ? selected.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                    : selected.ValueKind == JsonValueKind.True;
                var path = ReadString(file, "path");
                var index = ParseInt32(ReadString(file, "index"));
                var name = Path.GetFileName(path);
                files.Add(new TorrentFileItem(
                    index,
                    string.IsNullOrWhiteSpace(name) ? $"Dosya {index}" : name,
                    path,
                    ParseInt64(ReadString(file, "length")),
                    ParseInt64(ReadString(file, "completedLength")),
                    urisSelected,
                    VideoExtensions.Contains(Path.GetExtension(path))));
            }
        }
        var nameFromTorrent = state.Name;
        if (result.TryGetProperty("bittorrent", out var bittorrent) && bittorrent.TryGetProperty("info", out var info))
            nameFromTorrent = ReadString(info, "name", nameFromTorrent);
        return new TorrentSessionSnapshot(
            state.SessionId,
            nameFromTorrent,
            state.Source,
            torrentState,
            files,
            ParseInt64(ReadString(result, "completedLength")),
            ParseInt64(ReadString(result, "totalLength")),
            ParseInt64(ReadString(result, "downloadSpeed")),
            ParseInt64(ReadString(result, "uploadSpeed")),
            ParseInt32(ReadString(result, "numSeeders")),
            string.IsNullOrWhiteSpace(ReadString(result, "errorMessage")) ? null : ReadString(result, "errorMessage"));
    }

    private Task<JsonElement> TellStatusAsync(string gid, CancellationToken cancellationToken) =>
        RpcAsync("aria2.tellStatus", [gid, new[]
        {
            "gid", "status", "totalLength", "completedLength", "downloadSpeed", "uploadSpeed", "numSeeders",
            "errorMessage", "followedBy", "files", "bittorrent"
        }], cancellationToken);

    private async Task<string> RpcStringAsync(string method, object?[] parameters, CancellationToken cancellationToken)
    {
        var result = await RpcAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(result.GetString()))
            throw new InvalidOperationException($"aria2 {method} geçerli bir kimlik döndürmedi.");
        return result.GetString()!;
    }

    private async Task<JsonElement> RpcAsync(string method, object?[] parameters, CancellationToken cancellationToken)
    {
        if (_rpcUri is null) throw new InvalidOperationException("aria2 RPC başlatılmadı.");
        var authParameters = new List<object?> { $"token:{_secret}" };
        authParameters.AddRange(parameters);
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _rpcId).ToString(CultureInfo.InvariantCulture),
            ["method"] = method,
            ["params"] = authParameters
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_rpcUri, content, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"aria2 RPC HTTP {(int)response.StatusCode}: {json}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value) ? value : -1;
            throw new TorrentRpcException(code, ReadString(error, "message", "aria2 RPC hatası"));
        }
        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private Dictionary<string, string> BaseTorrentOptions()
    {
        var configuration = _configuration ?? throw new InvalidOperationException("Torrent yapılandırması bulunamadı.");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dir"] = configuration.CacheDirectory,
            ["file-allocation"] = "none",
            ["seed-time"] = "0",
            ["bt-enable-lpd"] = "true",
            ["enable-peer-exchange"] = "true"
        };
    }

    private static string BuildArguments(TorrentEngineConfiguration configuration, int port, string secret)
    {
        var arguments = new List<string>
        {
            "--enable-rpc=true",
            "--rpc-listen-all=false",
            $"--rpc-listen-port={port}",
            $"--rpc-secret={secret}",
            $"--dir=\"{configuration.CacheDirectory}\"",
            "--file-allocation=none",
            "--seed-time=0",
            "--bt-enable-lpd=true",
            "--enable-dht=true",
            "--enable-dht6=false",
            "--enable-peer-exchange=true",
            "--follow-torrent=mem",
            "--pause-metadata=true",
            "--rpc-save-upload-metadata=true",
            "--summary-interval=0",
            "--console-log-level=warn",
            "--download-result=hide",
            "--check-certificate=true"
        };
        if (configuration.DownloadLimitKiB > 0) arguments.Add($"--max-overall-download-limit={configuration.DownloadLimitKiB}K");
        if (configuration.UploadLimitKiB > 0) arguments.Add($"--max-overall-upload-limit={configuration.UploadLimitKiB}K");
        return string.Join(' ', arguments);
    }

    private static string? FindExecutable()
    {
        var root = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(root, "aria2c.exe"),
            Path.Combine(root, "native", "torrent", "aria2c.exe"),
            Path.Combine(Environment.CurrentDirectory, "native", "torrent", "aria2c.exe")
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "aria2c.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task DrainProcessStreamAsync(StreamReader reader, string prefix)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            Debug.WriteLine($"[{prefix}] {line}");
        }
    }

    private static bool HasFiles(JsonElement result) =>
        result.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0;

    private static string MagnetName(Uri uri)
    {
        var query = WebUtility.UrlDecode(uri.Query.TrimStart('?'));
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("dn", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair[1]))
                return pair[1];
        }
        return "Magnet torrent";
    }

    private SessionState GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var state) ? state : throw new KeyNotFoundException("Torrent oturumu bulunamadı.");

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (_process is not { HasExited: false } || _rpcUri is null)
            throw new InvalidOperationException("Torrent motoru başlatılmadı.");
    }

    private void Raise(TorrentSessionSnapshot snapshot) => SessionChanged?.Invoke(this, snapshot);
    private static string ReadString(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static long ParseInt64(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static int ParseInt32(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var poller in _pollers.Values) poller.Cancel();
        foreach (var poller in _pollers.Values) poller.Dispose();
        _pollers.Clear();
        if (_rpcUri is not null && _process is { HasExited: false })
        {
            try { await RpcAsync("aria2.shutdown", [], CancellationToken.None).ConfigureAwait(false); }
            catch { }
            try { if (!_process.WaitForExit(2000)) _process.Kill(true); } catch { }
        }
        _process?.Dispose();
        _http.Dispose();
    }

    private sealed record SessionState(string SessionId, string Gid, string Source, string Name, bool IsMagnet);

    private sealed class TorrentRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
