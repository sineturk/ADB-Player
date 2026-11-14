using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Cloud.Generated;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.Cloud;

public sealed class RcloneCloudService : IRcloneCloudService
{
    private const string ConfigSecretKey = "rclone:config";
    // Virtual browsing namespace: never forwarded as an actual Drive folder.
    private const string SharedDrivesVirtualRoot = "__adb_shared_drives_v1__";
    // The provider OAuth redirect URL is registered against rclone's fixed port.
    private const int LocalOAuthPort = 53682;
    private static readonly TimeSpan StandardTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(10);
    // rclone emits this private, short-lived loopback URL while it is running.
    // Never display it as plain text or log it (the state query is a secret).
    private static readonly Regex LocalOAuthUrlPattern = new(
        @"http://(?:127\.0\.0\.1|localhost):[0-9]{2,5}/auth\?state=[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);


    private readonly IAppLogger _logger;
    private readonly ISecretStore _secrets;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly Dictionary<string, ServeSession> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _rcloneExecutable;
    private readonly string _runtimeDirectory;
    private readonly string _runtimeConfigFile;
    private bool _runtimePrepared;
    private bool _disposed;

    public RcloneCloudService(IAppLogger logger, ISecretStore secrets, AppPaths paths)
    {
        _logger = logger;
        _secrets = secrets;
        _paths = paths;
        _rcloneExecutable = LocateRcloneExecutable();
        _runtimeDirectory = Path.Combine(paths.RcloneRuntimeDirectory, Guid.NewGuid().ToString("N"));
        _runtimeConfigFile = Path.Combine(_runtimeDirectory, "rclone.conf");
    }

    public async Task<RcloneRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_rcloneExecutable))
        {
            return new RcloneRuntimeStatus(
                false,
                string.Empty,
                "rclone.exe bulunamadı. Proje kökünde .\\tool\\vendor-rclone.ps1 çalıştırın.");
        }

        try
        {
            var result = await ExecuteTextAsync(["version"], StandardTimeout, cancellationToken).ConfigureAwait(false);
            var firstLine = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "rclone";
            return new RcloneRuntimeStatus(true, firstLine.Trim(), $"Hazır · {firstLine.Trim()}");
        }
        catch (Exception exception)
        {
            _logger.Error("rclone sürümü alınamadı.", exception);
            return new RcloneRuntimeStatus(false, string.Empty, $"rclone başlatılamadı: {exception.Message}");
        }
    }

    public async Task<string> ConnectAsync(
        RcloneCloudProvider provider,
        string displayName,
        CancellationToken cancellationToken = default,
        Action<Uri>? onAuthorizationLink = null)
    {
        ThrowIfUnavailable();
        await _configGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
            // Google's shared Drive client is retired in 2026: require BYOC.
            // Dropbox, OneDrive and pCloud prefer user-owned apps, but may use
            // provider defaults if the user leaves both fields blank.
            // Existing saved remotes keep their previously authorized tokens.
            if (provider == RcloneCloudProvider.GoogleDrive)
                await RequireUserOwnedClientAsync(provider, cancellationToken).ConfigureAwait(false);
            // Read-only bind probe: Windows/Hyper-V/WSL can reserve 53682 even
            // when no process appears in netstat. Do not alter OS port ranges
            // or terminate any other process. Close the listener immediately.
            VerifyLocalOAuthPortAvailable();
            var remoteName = CreateRemoteName(provider);
            var arguments = new List<string>
            {
                "config", "create", remoteName, BackendName(provider),
                "config_is_local", "true",
                // App owns browser launch; rclone writes its local /auth?state
                // link to stderr and waits for provider confirmation.
                "config_auth_no_browser", "true",
                "--auto-confirm",
                "--config", _runtimeConfigFile
            };

            if (provider == RcloneCloudProvider.GoogleDrive)
            {
                arguments.InsertRange(4, ["scope", "drive.readonly"]);
            }
            else if (provider == RcloneCloudProvider.PCloud)
            {
                arguments.InsertRange(4, ["hostname", "api.pcloud.com"]);
            }
            else if (provider == RcloneCloudProvider.PCloudEurope)
            {
                arguments.InsertRange(4, ["hostname", "eapi.pcloud.com"]);
            }

            var environment = await BuildProviderEnvironmentAsync(provider, cancellationToken).ConfigureAwait(false);
            // An optional provider-default connection must really use rclone's
            // defaults, never a publisher's packaged fallback OAuth credentials.
            if (provider != RcloneCloudProvider.GoogleDrive &&
                !await HasUserOwnedClientAsync(provider, cancellationToken).ConfigureAwait(false))
                environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _logger.Info($"rclone bulut hesabı yetkilendirmesi başlatılıyor: {ProviderLabel(provider)} / {displayName}");
            var result = await ExecuteTextAsync(
                    arguments, AuthorizationTimeout, cancellationToken, environment,
                    onAuthorizationLink).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(SafeProcessError(result, "Bulut hesabı bağlanamadı."));

            // rclone config create can succeed even if a provider has not
            // issued a usable token. Verify the remote before reporting success
            // or adding it to the user's saved accounts. No existing remote is
            // touched by this validation.
            var verify = await ExecuteTextAsync(
                ["lsjson", $"{remoteName}:", "--max-depth", "1",
                 "--config", _runtimeConfigFile, "--no-mimetype"],
                StandardTimeout,
                cancellationToken,
                environment).ConfigureAwait(false);
            if (verify.ExitCode != 0)
            {
                try
                {
                    await ExecuteTextAsync(
                        ["config", "delete", remoteName, "--config", _runtimeConfigFile],
                        StandardTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Runtime config is not persisted on failed connect.
                }
                throw new InvalidOperationException(SafeProcessError(verify,
                    "OAuth tamamlanmadı veya bulut erişimi doğrulanamadı."));
            }

            await PersistRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info($"rclone bulut hesabı bağlandı: {ProviderLabel(provider)} / {remoteName}");
            return remoteName;
        }
        finally
        {
            _configGate.Release();
        }
    }

    public async Task DisconnectAsync(string remoteName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
        ThrowIfUnavailable();
        await _configGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
            await StopServerAsync(remoteName).ConfigureAwait(false);

            // Bazı sağlayıcılar token iptalini desteklemez. Hata verirse uzak kaydı silmeye devam edilir.
            try
            {
                await ExecuteTextAsync(
                    ["config", "disconnect", remoteName, "--config", _runtimeConfigFile],
                    StandardTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Info($"rclone token iptali desteklenmedi veya başarısız oldu: {exception.Message}");
            }

            var delete = await ExecuteTextAsync(
                ["config", "delete", remoteName, "--config", _runtimeConfigFile],
                StandardTimeout,
                cancellationToken).ConfigureAwait(false);
            if (delete.ExitCode != 0)
            {
                throw new InvalidOperationException(SafeProcessError(delete, "Bulut hesabı kaldırılamadı."));
            }

            await PersistRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configGate.Release();
        }
    }

    public async Task<RemoteBrowseResult> BrowseAsync(
        SavedCloudAccount account,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.RemoteName);
        ThrowIfUnavailable();

        await _configGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
            var normalizedPath = NormalizeRemotePath(path);
            var environment = await BuildProviderEnvironmentAsync(account.Provider, cancellationToken, account.RemoteName)
                .ConfigureAwait(false);
            if (account.Provider == RcloneCloudProvider.GoogleDrive &&
                string.Equals(normalizedPath, SharedDrivesVirtualRoot, StringComparison.Ordinal))
            {
                var drivesResult = await ExecuteTextAsync(
                    ["backend", "drives", $"{account.RemoteName}:", "--config", _runtimeConfigFile],
                    StandardTimeout, cancellationToken, environment).ConfigureAwait(false);
                if (drivesResult.ExitCode != 0)
                    throw new InvalidOperationException(SafeProcessError(drivesResult,
                        "Paylaşılan Google Drive listesi alınamadı. Hesap erişimini kontrol edin."));
                var sharedDrives = JsonSerializer.Deserialize<List<RcloneSharedDriveEntry>>(
                    drivesResult.StandardOutput,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                var sharedItems = sharedDrives
                    .Where(drive => IsSafeSharedDriveId(drive.Id))
                    .OrderBy(drive => drive.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(drive =>
                    {
                        var virtualPath = $"{SharedDrivesVirtualRoot}/{drive.Id}";
                        var virtualUri = BuildRcloneUri(account.RemoteName, virtualPath);
                        return new RemoteSourceItem(
                            virtualUri,
                            string.IsNullOrWhiteSpace(drive.Name) ? drive.Id : drive.Name,
                            RemoteSourceItemKind.Folder,
                            null,
                            virtualUri,
                            null,
                            null,
                            Provider: $"rclone:{account.RemoteName}");
                    })
                    .ToList();
                await PersistRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
                return new RemoteBrowseResult(
                    RemoteSourceProvider.Rclone,
                    "Paylaşılan Drive'lar",
                    BuildRcloneUri(account.RemoteName, normalizedPath),
                    sharedItems,
                    ParentPath: BuildRcloneUri(account.RemoteName, string.Empty),
                    StatusMessage: $"{sharedItems.Count} paylaşılan Drive bulundu.");
            }

            var isSharedDrive = TryParseSharedDrivePath(normalizedPath, out var sharedDriveId, out var sharedPath);
            if (isSharedDrive && account.Provider != RcloneCloudProvider.GoogleDrive)
                throw new InvalidOperationException("Bu hesap Google Shared Drives erişimini desteklemiyor.");
            var remoteSpec = isSharedDrive
                ? BuildSharedDriveRemoteSpec(account.RemoteName, sharedDriveId, sharedPath)
                : BuildRemoteSpec(account.RemoteName, normalizedPath);
            var result = await ExecuteTextAsync(
                ["lsjson", remoteSpec, "--config", _runtimeConfigFile, "--no-mimetype"],
                StandardTimeout, cancellationToken, environment).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(SafeProcessError(result, "Bulut klasörü listelenemedi."));
            }

            var rows = JsonSerializer.Deserialize<List<RcloneListItem>>(
                           result.StandardOutput,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? [];

            var items = rows
                .Select(row => ToRemoteItem(account, normalizedPath, row))
                .OrderByDescending(item => item.IsFolder)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (account.Provider == RcloneCloudProvider.GoogleDrive &&
                string.IsNullOrEmpty(normalizedPath))
            {
                var sharedUri = BuildRcloneUri(account.RemoteName, SharedDrivesVirtualRoot);
                items.Insert(0, new RemoteSourceItem(
                    sharedUri,
                    "Paylaşılan Drive'lar (Shared Drives)",
                    RemoteSourceItemKind.Folder,
                    null,
                    sharedUri,
                    null,
                    null,
                    Provider: $"rclone:{account.RemoteName}"));
            }

            await PersistRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
            account.LastUsedUtc = DateTimeOffset.UtcNow;
            var currentPath = BuildRcloneUri(account.RemoteName, normalizedPath);
            var parent = GetParentPath(normalizedPath);
            return new RemoteBrowseResult(
                RemoteSourceProvider.Rclone,
                account.ToString(),
                currentPath,
                items,
                ParentPath: parent is null ? null : BuildRcloneUri(account.RemoteName, parent),
                StatusMessage: $"{items.Count} bulut öğesi bulundu.");
        }
        finally
        {
            _configGate.Release();
        }
    }

    public async Task<RemoteOpenRequest> CreateOpenRequestAsync(
        SavedCloudAccount account,
        RemoteSourceItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is not (RemoteSourceItemKind.Video or RemoteSourceItemKind.Audio))
            throw new InvalidOperationException("Seçilen bulut öğesi oynatılabilir medya değil.");

        await _configGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (requestedRemote, virtualPath) = ParseRcloneUri(item.OpenUrl ?? item.Id);
            if (!requestedRemote.Equals(account.RemoteName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Seçilen dosya bu bulut hesabına ait değil.");
            var isSharedDrive = TryParseSharedDrivePath(virtualPath, out var sharedDriveId, out var mediaPath);
            if (isSharedDrive && account.Provider != RcloneCloudProvider.GoogleDrive)
                throw new InvalidOperationException("Google Shared Drive yalnız Google hesabıyla açılabilir.");
            var path = isSharedDrive ? mediaPath : virtualPath;
            var session = await GetOrStartServerAsync(account, isSharedDrive ? sharedDriveId : null, cancellationToken)
                .ConfigureAwait(false);
            var escapedPath = EscapeHttpPath(path);
            var source = $"http://127.0.0.1:{session.Port}/{escapedPath}";
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.UserName}:{session.Password}"));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Basic {basic}"
            };

            // Fail early with a sanitized HTTP status instead of the generic
            // "Video açılamadı" when the Rclone/Drive Range bridge fails.
            // Stream headers only; never download an entire multi-GB video here.
            await ProbeLocalMediaAsync(source, headers["Authorization"], cancellationToken)
                .ConfigureAwait(false);

            // Harici ses libmpv'ye ikinci bir kaynak olarak eklenir. Yetkiyi bu
            // localhost URL'sine gommek, ana videonun HTTP basliklarini degistirmeden
            // sesin de Range istekleriyle aninda acilmasini saglar.
            if (item.Kind == RemoteSourceItemKind.Audio)
            {
                var user = Uri.EscapeDataString(session.UserName);
                var password = Uri.EscapeDataString(session.Password);
                source = $"http://{user}:{password}@127.0.0.1:{session.Port}/{escapedPath}";
                headers = null;
            }

            return new RemoteOpenRequest(source, item.Name, headers, $"rclone:{account.RemoteName}", item.OpenUrl);
        }
        finally
        {
            _configGate.Release();
        }
    }

    public async Task DownloadToFileAsync(
        SavedCloudAccount account,
        RemoteSourceItem item,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ThrowIfUnavailable();

        await _configGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
            var (requestedRemote, virtualPath) = ParseRcloneUri(item.OpenUrl ?? item.Id);
            if (!requestedRemote.Equals(account.RemoteName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("İndirilecek dosya bu bulut hesabına ait değil.");
            var isSharedDrive = TryParseSharedDrivePath(virtualPath, out var sharedDriveId, out var sharedPath);
            if (isSharedDrive && account.Provider != RcloneCloudProvider.GoogleDrive)
                throw new InvalidOperationException("Google Shared Drive yalnız Google hesabıyla açılabilir.");
            var remoteSpec = isSharedDrive
                ? BuildSharedDriveRemoteSpec(account.RemoteName, sharedDriveId, sharedPath)
                : BuildRemoteSpec(account.RemoteName, virtualPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var result = await ExecuteTextAsync(
                [
                    "copyto", remoteSpec, destinationPath,
                    "--config", _runtimeConfigFile,
                    "--cache-dir", _paths.RcloneCacheDirectory,
                    "--no-traverse"
                ],
                TimeSpan.FromMinutes(30),
                cancellationToken,
                await BuildProviderEnvironmentAsync(account.Provider, cancellationToken, account.RemoteName).ConfigureAwait(false))
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(SafeProcessError(result, "Bulut dosyası indirilemedi."));
            }

            await PersistRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configGate.Release();
        }
    }

    private async Task<ServeSession> GetOrStartServerAsync(
        SavedCloudAccount account, string? sharedDriveId, CancellationToken cancellationToken)
    {
        var remoteName = account.RemoteName;
        var serverKey = sharedDriveId is null ? remoteName : $"{remoteName}|team={sharedDriveId}";
        lock (_servers)
        {
            if (_servers.TryGetValue(serverKey, out var current) && !current.Process.HasExited)
                return current;
        }

        await EnsureRuntimeConfigAsync(cancellationToken).ConfigureAwait(false);
        var port = ReserveTcpPort();
        var user = $"adb-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var logFile = Path.Combine(_paths.RcloneLogDirectory, $"serve-{SafeFileName(remoteName)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

        var startInfo = CreateStartInfo(
            [
                "serve", "http", sharedDriveId is null
                    ? $"{remoteName}:"
                    : BuildSharedDriveRemoteSpec(remoteName, sharedDriveId, string.Empty),
                "--config", _runtimeConfigFile,
                "--addr", $"127.0.0.1:{port}",
                "--user", user,
                "--pass", password,
                "--read-only",
                "--cache-dir", _paths.RcloneCacheDirectory,
                "--buffer-size", "16M",
                "--vfs-read-chunk-size", "32M",
                "--vfs-read-chunk-size-limit", "512M",
                "--dir-cache-time", "5m",
                "--poll-interval", "1m",
                "--log-file", logFile,
                "--log-level", "INFO"
            ],
            redirectOutput: false,
            environment: await BuildProviderEnvironmentAsync(account.Provider, cancellationToken, account.RemoteName)
                .ConfigureAwait(false));
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("rclone HTTP sunucusu başlatılamadı.");
        var session = new ServeSession(serverKey, port, user, password, process);

        try
        {
            await WaitForPortAsync(process, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryTerminate(process);
            process.Dispose();
            throw;
        }

        lock (_servers)
        {
            if (_servers.TryGetValue(serverKey, out var old))
            {
                TryTerminate(old.Process);
                old.Process.Dispose();
            }
            _servers[serverKey] = session;
        }

        _logger.Info($"rclone yerel medya sunucusu başlatıldı: {remoteName} / 127.0.0.1:{port}");
        return session;
    }

    private async Task StopServerAsync(string remoteName)
    {
        List<ServeSession> sessions;
        lock (_servers)
        {
            var keys = _servers.Keys
                .Where(key => key.Equals(remoteName, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(remoteName + "|team=", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            sessions = keys.Select(key => _servers[key]).ToList();
            foreach (var key in keys) _servers.Remove(key);
        }

        foreach (var session in sessions)
        {
            TryTerminate(session.Process);
            try { await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* Best-effort shutdown. */ }
            session.Process.Dispose();
        }
    }

    private async Task EnsureRuntimeConfigAsync(CancellationToken cancellationToken)
    {
        if (_runtimePrepared) return;
        Directory.CreateDirectory(_paths.RcloneRuntimeDirectory);
        CleanupStaleRuntimeDirectories();
        Directory.CreateDirectory(_runtimeDirectory);
        var protectedConfig = await _secrets.GetAsync(ConfigSecretKey, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(_runtimeConfigFile, protectedConfig ?? string.Empty, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        try { File.SetAttributes(_runtimeConfigFile, FileAttributes.Hidden); } catch { /* Opsiyonel. */ }
        _runtimePrepared = true;
    }

    private async Task PersistRuntimeConfigAsync(CancellationToken cancellationToken)
    {
        if (!_runtimePrepared || !File.Exists(_runtimeConfigFile)) return;
        var config = await File.ReadAllTextAsync(_runtimeConfigFile, cancellationToken).ConfigureAwait(false);
        await _secrets.SetAsync(ConfigSecretKey, config, cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyLocalOAuthPortAvailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, LocalOAuthPort);
        try
        {
            listener.Start();
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException("oauth_local_port_occupied", exception);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
        {
            throw new InvalidOperationException("oauth_local_port_reserved", exception);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<ProcessResult> ExecuteTextAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<Uri>? onAuthorizationLink = null)
    {
        ThrowIfUnavailable();
        using var process = new Process { StartInfo = CreateStartInfo(arguments, redirectOutput: true, environment) };
        if (!process.Start()) throw new InvalidOperationException("rclone işlemi başlatılamadı.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = onAuthorizationLink is null
            ? process.StandardError.ReadToEndAsync(cancellationToken)
            : ReadOAuthStderrAsync(process.StandardError, onAuthorizationLink, cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            throw new TimeoutException($"rclone işlemi {timeout.TotalMinutes:0.#} dakika içinde tamamlanmadı.");
        }
        catch
        {
            TryTerminate(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output, error);
    }

    /// <summary>
    /// Inspect rclone stderr *while* config create is waiting for OAuth.
    /// Reading only after WaitForExit would make the manual-browser fallback
    /// unusable. Do not write the authorization state to logs or settings.
    /// </summary>
    private static async Task<string> ReadOAuthStderrAsync(
        StreamReader stderr,
        Action<Uri> onAuthorizationLink,
        CancellationToken cancellationToken)
    {
        const int maxChars = 32_768;
        var diagnostic = new StringBuilder();
        var notified = false;
        while (await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!notified)
            {
                var url = TryExtractLocalAuthorizationUri(line);
                if (url is not null)
                {
                    notified = true;
                    try { onAuthorizationLink(url); }
                    catch { /* Browser launch/UI callback failure must not abort rclone */ }
                }
            }

            // Bound the collected stderr. It is used only by SafeProcessError
            // on unsuccessful operations, not surfaced as raw user diagnostics.
            if (diagnostic.Length < maxChars)
                diagnostic.AppendLine(line[..Math.Min(line.Length, maxChars - diagnostic.Length)]);
        }
        return diagnostic.ToString();
    }

    private static Uri? TryExtractLocalAuthorizationUri(string line)
    {
        var match = LocalOAuthUrlPattern.Match(line);
        if (!match.Success
            || !Uri.TryCreate(match.Value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttp
            || !candidate.IsLoopback
            || candidate.Port is <= 0 or > 65535
            || !string.Equals(candidate.AbsolutePath, "/auth", StringComparison.Ordinal)
            || !candidate.Query.StartsWith("?state=", StringComparison.Ordinal))
            return null;

        return candidate;
    }

    private ProcessStartInfo CreateStartInfo(
        IReadOnlyList<string> arguments,
        bool redirectOutput,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _rcloneExecutable!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            WorkingDirectory = _paths.RcloneDirectory
        };
        // .NET Process.Start rejects StandardOutputEncoding/StandardErrorEncoding
        // when the corresponding stream is not redirected. "rclone serve http"
        // must leave both streams attached, while CLI lsjson/oauth uses capture.
        if (redirectOutput)
        {
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value)) startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        return startInfo;
    }

    private async Task<bool> HasUserOwnedClientAsync(
        RcloneCloudProvider provider, CancellationToken cancellationToken)
    {
        var prefix = provider switch
        {
            RcloneCloudProvider.GoogleDrive => "google",
            RcloneCloudProvider.Dropbox => "dropbox",
            RcloneCloudProvider.OneDrive => "onedrive",
            RcloneCloudProvider.PCloud or RcloneCloudProvider.PCloudEurope => "pcloud",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        var id = await _secrets.GetAsync($"cloud:{prefix}-client-id", cancellationToken).ConfigureAwait(false);
        var secret = await _secrets.GetAsync($"cloud:{prefix}-client-secret", cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret);
    }

    private async Task RequireUserOwnedClientAsync(
        RcloneCloudProvider provider, CancellationToken cancellationToken)
    {
        if (!await HasUserOwnedClientAsync(provider, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Yeni Google Drive bağlantısı için kendi OAuth Client ID ve Client Secret bilgileriniz gereklidir. " +
                "Ayarlar > Bulut bağlantı bilgileri bölümüne girin veya bağlantı penceresinde doldurun.");
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildProviderEnvironmentAsync(
        RcloneCloudProvider provider,
        CancellationToken cancellationToken,
        string? remoteName = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(remoteName))
        {
            // A provider-default account is pinned to its original shared OAuth
            // client. Do not silently replace its identity if Settings changes.
            var shared = await _secrets.GetAsync(
                $"cloud:remote:{remoteName}:shared-client", cancellationToken).ConfigureAwait(false);
            if (string.Equals(shared, "true", StringComparison.OrdinalIgnoreCase))
                return values;
        }
        switch (provider)
        {
            case RcloneCloudProvider.GoogleDrive:
                values["RCLONE_DRIVE_CLIENT_ID"] = await GetCredentialAsync(
                    "cloud:google-client-id",
                    GeneratedAppCredentials.GoogleClientId,
                    cancellationToken).ConfigureAwait(false);
                values["RCLONE_DRIVE_CLIENT_SECRET"] = await GetCredentialAsync(
                    "cloud:google-client-secret",
                    GeneratedAppCredentials.GoogleClientSecret,
                    cancellationToken).ConfigureAwait(false);
                values["RCLONE_DRIVE_SCOPE"] = "drive.readonly";
                break;

            case RcloneCloudProvider.Dropbox:
                values["RCLONE_DROPBOX_CLIENT_ID"] = await GetCredentialAsync(
                    "cloud:dropbox-client-id",
                    GeneratedAppCredentials.DropboxClientId,
                    cancellationToken).ConfigureAwait(false);
                values["RCLONE_DROPBOX_CLIENT_SECRET"] = await GetCredentialAsync(
                    "cloud:dropbox-client-secret",
                    GeneratedAppCredentials.DropboxClientSecret,
                    cancellationToken).ConfigureAwait(false);
                break;

            case RcloneCloudProvider.OneDrive:
                values["RCLONE_ONEDRIVE_CLIENT_ID"] = await GetCredentialAsync(
                    "cloud:onedrive-client-id",
                    GeneratedAppCredentials.OneDriveClientId,
                    cancellationToken).ConfigureAwait(false);
                values["RCLONE_ONEDRIVE_CLIENT_SECRET"] = await GetCredentialAsync(
                    "cloud:onedrive-client-secret",
                    GeneratedAppCredentials.OneDriveClientSecret,
                    cancellationToken).ConfigureAwait(false);
                break;

            case RcloneCloudProvider.PCloud:
            case RcloneCloudProvider.PCloudEurope:
                values["RCLONE_PCLOUD_CLIENT_ID"] = await GetCredentialAsync(
                    "cloud:pcloud-client-id",
                    GeneratedAppCredentials.PCloudClientId,
                    cancellationToken).ConfigureAwait(false);
                values["RCLONE_PCLOUD_CLIENT_SECRET"] = await GetCredentialAsync(
                    "cloud:pcloud-client-secret",
                    GeneratedAppCredentials.PCloudClientSecret,
                    cancellationToken).ConfigureAwait(false);
                break;
        }

        // Per-remote OAuth client credentials supersede provider-wide defaults.
        // This avoids replacing the OAuth identity for an already-linked pCloud
        // or Dropbox account when a new one uses a different custom app.
        if (!string.IsNullOrWhiteSpace(remoteName))
        {
            var remoteId = await _secrets.GetAsync(
                $"cloud:remote:{remoteName}:client-id", cancellationToken).ConfigureAwait(false);
            var remoteSecret = await _secrets.GetAsync(
                $"cloud:remote:{remoteName}:client-secret", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(remoteId) && !string.IsNullOrWhiteSpace(remoteSecret))
            {
                var prefix = provider switch
                {
                    RcloneCloudProvider.GoogleDrive => "RCLONE_DRIVE",
                    RcloneCloudProvider.Dropbox => "RCLONE_DROPBOX",
                    RcloneCloudProvider.OneDrive => "RCLONE_ONEDRIVE",
                    RcloneCloudProvider.PCloud or RcloneCloudProvider.PCloudEurope => "RCLONE_PCLOUD",
                    _ => string.Empty
                };
                if (prefix.Length > 0)
                {
                    values[$"{prefix}_CLIENT_ID"] = remoteId;
                    values[$"{prefix}_CLIENT_SECRET"] = remoteSecret;
                }
            }
        }

        return values;
    }

    private async Task<string> GetCredentialAsync(
        string secretKey,
        string packagedFallback,
        CancellationToken cancellationToken)
    {
        var stored = await _secrets.GetAsync(secretKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stored) ? packagedFallback : stored;
    }

    private static RemoteSourceItem ToRemoteItem(SavedCloudAccount account, string parentPath, RcloneListItem row)
    {
        var name = string.IsNullOrWhiteSpace(row.Name) ? row.Path : row.Name;
        var relative = CombineRemotePath(parentPath, string.IsNullOrWhiteSpace(row.Path) ? name : row.Path);
        var kind = row.IsDir ? RemoteSourceItemKind.Folder : ClassifyFile(name);
        var uri = BuildRcloneUri(account.RemoteName, relative);
        return new RemoteSourceItem(
            uri,
            name,
            kind,
            row.IsDir ? null : uri,
            row.IsDir ? uri : null,
            row.IsDir || row.Size < 0 ? null : row.Size,
            ParseTimestamp(row.ModTime),
            Provider: $"rclone:{account.RemoteName}");
    }

    private static RemoteSourceItemKind ClassifyFile(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".mkv" or ".mp4" or ".avi" or ".webm" or ".mov" or ".m4v" or ".ts" or ".m2ts" or ".mts" or ".mpeg" or ".mpg" or ".wmv" or ".flv" or ".ogv" or ".vob" or ".3gp")
            return RemoteSourceItemKind.Video;
        if (extension is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub")
            return RemoteSourceItemKind.Subtitle;
        if (extension is ".aac" or ".ac3" or ".eac3" or ".dts" or ".dtshd" or ".truehd" or ".flac" or ".mka" or ".m4a" or ".mp3" or ".ogg" or ".opus" or ".wav")
            return RemoteSourceItemKind.Audio;
        if (extension is ".zip" or ".rar" or ".7z")
            return RemoteSourceItemKind.Archive;
        return RemoteSourceItemKind.Other;
    }

    private string? LocateRcloneExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", "rclone", "rclone.exe"),
            Path.Combine(AppContext.BaseDirectory, "rclone.exe"),
            Path.Combine(Environment.CurrentDirectory, "native", "rclone", "rclone.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "rclone.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* Geçersiz PATH girdisi. */ }
        }
        return null;
    }

    private void CleanupStaleRuntimeDirectories()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.RcloneRuntimeDirectory))
            {
                if (directory.Equals(_runtimeDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddHours(-12)) continue;
                    Directory.Delete(directory, recursive: true);
                }
                catch { /* Başka çalışan örnek kullanıyor olabilir. */ }
            }
        }
        catch { /* Temizlik uygulamayı engellemez. */ }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_rcloneExecutable))
            throw new FileNotFoundException("rclone.exe bulunamadı. .\\tool\\vendor-rclone.ps1 çalıştırın.");
    }

    private static string CreateRemoteName(RcloneCloudProvider provider)
    {
        var prefix = provider switch
        {
            RcloneCloudProvider.GoogleDrive => "gdrive",
            RcloneCloudProvider.Dropbox => "dropbox",
            RcloneCloudProvider.OneDrive => "onedrive",
            RcloneCloudProvider.PCloud => "pcloud-us",
            RcloneCloudProvider.PCloudEurope => "pcloud-eu",
            _ => "cloud"
        };
        return $"adb-{prefix}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
    }

    private static string BackendName(RcloneCloudProvider provider) => provider switch
    {
        RcloneCloudProvider.GoogleDrive => "drive",
        RcloneCloudProvider.Dropbox => "dropbox",
        RcloneCloudProvider.OneDrive => "onedrive",
        RcloneCloudProvider.PCloud => "pcloud",
        RcloneCloudProvider.PCloudEurope => "pcloud",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    private static string ProviderLabel(RcloneCloudProvider provider) => provider switch
    {
        RcloneCloudProvider.GoogleDrive => "Google Drive",
        RcloneCloudProvider.Dropbox => "Dropbox",
        RcloneCloudProvider.OneDrive => "OneDrive",
        RcloneCloudProvider.PCloud => "pCloud ABD",
        RcloneCloudProvider.PCloudEurope => "pCloud Avrupa",
        _ => provider.ToString()
    };

    private static string BuildRemoteSpec(string remoteName, string path) =>
        string.IsNullOrWhiteSpace(path) ? $"{remoteName}:" : $"{remoteName}:{path}";

    private static bool IsSafeSharedDriveId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        Regex.IsMatch(id, @"^[a-zA-Z0-9_-]{5,128}$", RegexOptions.CultureInvariant);

    private static bool TryParseSharedDrivePath(string path, out string sharedDriveId, out string relativePath)
    {
        sharedDriveId = string.Empty;
        relativePath = string.Empty;
        var prefix = SharedDrivesVirtualRoot + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        var slash = suffix.IndexOf('/');
        sharedDriveId = slash < 0 ? suffix : suffix[..slash];
        if (!IsSafeSharedDriveId(sharedDriveId))
            throw new InvalidOperationException("Geçersiz Google Shared Drive kimliği.");
        relativePath = slash < 0 ? string.Empty : suffix[(slash + 1)..];
        return true;
    }

    private static string BuildSharedDriveRemoteSpec(string remoteName, string sharedDriveId, string path)
    {
        if (!IsSafeSharedDriveId(sharedDriveId))
            throw new InvalidOperationException("Geçersiz Google Shared Drive kimliği.");
        // rclone backend config override: use the same user's OAuth token,
        // choose the Shared Drive root without persisting another remote.
        var prefix = $"{remoteName},team_drive={sharedDriveId},root_folder_id=:";
        return string.IsNullOrWhiteSpace(path) ? prefix : prefix + path;
    }

    private async Task ProbeLocalMediaAsync(
        string source, string basicAuthorization, CancellationToken cancellationToken)
    {
        // A cold Drive read may require upstream metadata and initial range fetches.
        // This is a bounded, one-byte probe, not a multi-GB pre-download.
        var probeTimer = Stopwatch.StartNew();
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        request.Headers.TryAddWithoutValidation("Authorization", basicAuthorization);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _logger.Info($"Bulut ilk Range yanıtı: HTTP {(int)response.StatusCode}; süre {probeTimer.ElapsedMilliseconds} ms.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Google Drive/rclone akışı HTTP {(int)response.StatusCode} döndürdü. " +
                    "Dosya yetkisini ve rclone günlüklerini kontrol edin.");
            if (response.StatusCode == HttpStatusCode.OK &&
                response.Content.Headers.ContentLength is > 1024 * 1024)
                throw new InvalidOperationException(
                    "Bulut sunucusu Range isteğine 206 yerine büyük bir 200 yanıtı verdi. " +
                    "Büyük dosyalar için kısmi içerik desteği gerekli.");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warning($"Bulut ilk Range isteği yanıt vermedi: süre {probeTimer.ElapsedMilliseconds} ms (45s sınır).");
            throw new TimeoutException(
                "Bulut akışı ilk Range isteğinde zaman aşımına uğradı (45 saniye). " +
                "Google Drive OAuth istemcisini, ağ erişimini ve rclone günlüklerini kontrol edin.", exception);
        }
    }

    private static string NormalizeRemotePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (value.StartsWith("rclone://", StringComparison.OrdinalIgnoreCase))
            return ParseRcloneUri(value).Path;
        return value.Replace('\\', '/').Trim('/');
    }

    private static string CombineRemotePath(string parent, string child)
    {
        parent = parent.Replace('\\', '/').Trim('/');
        child = child.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(parent)) return child;
        if (child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)) return child;
        return $"{parent}/{child}";
    }

    private static string BuildRcloneUri(string remoteName, string path)
    {
        var escapedRemote = Uri.EscapeDataString(remoteName);
        var escapedPath = EscapeHttpPath(path);
        return string.IsNullOrWhiteSpace(escapedPath)
            ? $"rclone://{escapedRemote}/"
            : $"rclone://{escapedRemote}/{escapedPath}";
    }

    private static (string RemoteName, string Path) ParseRcloneUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("rclone", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Geçersiz rclone dosya yolu.");
        var remote = Uri.UnescapeDataString(uri.Host);
        var path = string.Join('/', uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString));
        return (remote, path);
    }

    private static string EscapeHttpPath(string path) => string.Join('/', path
        .Replace('\\', '/')
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.EscapeDataString));

    private static string? GetParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task WaitForPortAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"rclone medya sunucusu erken kapandı (kod {process.ExitCode}).");
            try
            {
                using var client = new TcpClient();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                // Await the real connect operation, not a WaitAsync wrapper which
                // abandons the socket task and raises unobserved SocketException 995.
                await client.ConnectAsync(IPAddress.Loopback, port, attemptCts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is SocketException or TimeoutException or OperationCanceledException))
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TimeoutException("rclone yerel medya sunucusu zamanında hazır olmadı.");
    }

    private static string SafeProcessError(ProcessResult result, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        text = text.Trim();
        return text.Length <= 2000 ? text : text[..2000];
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* İşlem zaten kapanmış olabilir. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        List<ServeSession> sessions;
        lock (_servers)
        {
            sessions = _servers.Values.ToList();
            _servers.Clear();
        }
        foreach (var session in sessions)
        {
            TryTerminate(session.Process);
            session.Process.Dispose();
        }

        try { await PersistRuntimeConfigAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { _logger.Error("rclone yapılandırması kapanışta saklanamadı.", exception); }
        try { if (Directory.Exists(_runtimeDirectory)) Directory.Delete(_runtimeDirectory, recursive: true); }
        catch { /* En iyi çaba. */ }
        _configGate.Dispose();
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record ServeSession(string RemoteName, int Port, string UserName, string Password, Process Process);

    private sealed class RcloneSharedDriveEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private sealed class RcloneListItem
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public string? ModTime { get; set; }
        public bool IsDir { get; set; }
    }
}
