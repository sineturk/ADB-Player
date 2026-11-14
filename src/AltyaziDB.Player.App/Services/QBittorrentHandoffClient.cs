using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AltyaziDB.Player.Torrent;

namespace AltyaziDB.Player.App.Services;

/// <summary>
/// Safe qBittorrent WebUI handoff. The torrent's ORIGINAL metadata is submitted
/// to qBittorrent; no tracker credentials, announce URLs or passwords are logged.
/// This client never alters torrent download limits or deletes torrent data.
/// </summary>
internal sealed record QBittorrentHandoffProgress(string Stage, double Fraction);

internal sealed class QBittorrentHandoffClient
{
    private const int MaximumTorrentFileBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(18);

    public static Uri ValidateBaseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) ||
            !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("qBittorrent adresini kontrol edin.");

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("qBittorrent adresine kullanıcı bilgisi veya anahtar eklemeyin.");

        // Do not send WebUI passwords or private torrent announce URLs over public HTTP.
        // Reverse-proxy users can supply HTTPS; for HTTP use loopback on this device.
        if (uri.Scheme == Uri.UriSchemeHttp &&
            !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)))
            throw new InvalidOperationException("Uzak qBittorrent bağlantısı HTTPS kullanmalıdır.");

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            builder.Path += "/";
        return builder.Uri;
    }

    public async Task<string> TestAsync(
        string address, string username, string password, CancellationToken cancellationToken = default)
    {
        var root = ValidateBaseAddress(address);
        using var client = CreateClient(root);
        await AuthenticateAsync(client, root, username, password, cancellationToken).ConfigureAwait(false);
        using var reply = await client.GetAsync(new Uri(root, "api/v2/app/version"),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(reply, cancellationToken).ConfigureAwait(false);
        var version = (await reply.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (version.Length == 0 || version.Length > 80)
            throw new InvalidOperationException("qBittorrent yanıtı doğrulanamadı.");
        return version;
    }

    public async Task AddAsync(
        string address, string username, string password, string source,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateBaseAddress(address);
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Önce torrent veya magnet açın.");

        using var form = new MultipartFormDataContent();
        if (source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            if (source.Length > 65536 || !Uri.TryCreate(source, UriKind.Absolute, out var magnet) ||
                magnet.Scheme != "magnet")
                throw new InvalidOperationException("Magnet bağlantısı geçersiz.");
            form.Add(new StringContent(source, Encoding.UTF8), "urls");
        }
        else
        {
            if (!source.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                throw new InvalidOperationException("Orijinal .torrent dosyası bulunamadı.");
            var file = new FileInfo(source);
            if (file.Length == 0 || file.Length > MaximumTorrentFileBytes)
                throw new InvalidOperationException("Torrent dosyası boyutu desteklenmiyor.");
            var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
            var attachment = new ByteArrayContent(bytes);
            attachment.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(attachment, "torrents", "adb-player.torrent");
        }
        // Preserve qBittorrent's own destination, category and share-limit policies.
        // DO NOT set skip_checking, ratioLimit or seedingTimeLimit here.
        using var client = CreateClient(root);
        await AuthenticateAsync(client, root, username, password, cancellationToken).ConfigureAwait(false);
        using var response = await client.PostAsync(new Uri(root, "api/v2/torrents/add"),
            form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (!result.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("qBittorrent torrent aktarımını kabul etmedi.");
    }


    /// <summary>
    /// Preflight checks the qBittorrent identity and duplicate hash WITHOUT
    /// changing Player's session or qBittorrent's existing jobs.
    /// </summary>
    public async Task CheckSmartHandoffAsync(
        string address, string username, string password,
        SmartTorrentHandoffPlan plan, CancellationToken cancellationToken = default)
    {
        var root = ValidateSameMachineWebUi(address);
        using var client = CreateClient(root);
        await AuthenticateAsync(client, root, username, password, cancellationToken).ConfigureAwait(false);
        if (await JobExistsAsync(client, root, plan.TorrentHash, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Torrent qBittorrent'te zaten var. Var olan torrentin konumunu kontrol edin.");
    }

    /// <summary>
    /// Must only be called AFTER the Player torrent manager has been removed
    /// with deleteFiles=false. Never calls skip_checking=true or deletes data.
    /// </summary>
    public async Task AddWithRecheckAsync(
        string address, string username, string password,
        SmartTorrentHandoffPlan plan, CancellationToken cancellationToken = default,
        IProgress<QBittorrentHandoffProgress>? progress = null)
    {
        var root = ValidateSameMachineWebUi(address);
        using var client = CreateClient(root);
        await AuthenticateAsync(client, root, username, password, cancellationToken).ConfigureAwait(false);
        if (await JobExistsAsync(client, root, plan.TorrentHash, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Torrent qBittorrent'te zaten var. Var olan torrentin konumunu kontrol edin.");

        // Player is no longer writing. Preserve the original magnet tracker
        // addresses or private .torrent metadata; qBittorrent remains stopped
        // until the imported job's path mapping has been checked.
        using var form = new MultipartFormDataContent();
        if (plan.IsMagnet)
        {
            form.Add(new StringContent(plan.TorrentFile, Encoding.UTF8), "urls");
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(plan.TorrentFile, cancellationToken).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaximumTorrentFileBytes)
                throw new InvalidOperationException("Torrent dosyası boyutu desteklenmiyor.");
            var attachment = new ByteArrayContent(bytes);
            attachment.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(attachment, "torrents", "adb-player.torrent");
        }
        form.Add(new StringContent(plan.SaveDirectory, Encoding.UTF8), "savepath");
        form.Add(new StringContent("true"), "paused");
        form.Add(new StringContent("true"), "stopped");
        form.Add(new StringContent(plan.ContentLayout), "contentLayout");
        form.Add(new StringContent(plan.ContentLayout == "Subfolder" ? "true" : "false"), "root_folder");
        form.Add(new StringContent("false"), "autoTMM");
        form.Add(new StringContent("false"), "skip_checking");

        using (var post = await client.PostAsync(new Uri(root, "api/v2/torrents/add"),
                   form, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(post, cancellationToken).ConfigureAwait(false);
            var body = (await post.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!body.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("qBittorrent torrent aktarımını kabul etmedi.");
        }

        // v4.x uses pause/resume; v5 uses stop/start.
        var version = await ReadVersionAsync(client, root, cancellationToken).ConfigureAwait(false);
        var modern = version.StartsWith("5.", StringComparison.Ordinal);
        var stopEndpoint = modern ? "api/v2/torrents/stop" : "api/v2/torrents/pause";
        var startEndpoint = modern ? "api/v2/torrents/start" : "api/v2/torrents/resume";

        JsonElement? job = null;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            job = await ReadJobAsync(client, root, plan.TorrentHash, cancellationToken).ConfigureAwait(false);
            if (job is not null) break;
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
        if (job is null)
            throw new InvalidOperationException("Torrent eklendi ancak qBittorrent listesinde henüz görünmüyor.");

        var fileNames = await ReadJobFilesAsync(client, root, plan.TorrentHash, cancellationToken)
            .ConfigureAwait(false);
        if (plan.IsMagnet && fileNames.Count == 0)
        {
            // A stopped magnet cannot always retrieve its metadata. Now that
            // Player has relinquished ownership, allow qBittorrent to fetch
            // metadata, then stop the job before comparing any file paths.
            // We never start the ADB Player downloader again.
            try
            {
                await ChangeJobAsync(client, root, startEndpoint, plan.TorrentHash, cancellationToken)
                    .ConfigureAwait(false);
                for (var attempt = 0; attempt < 120 && fileNames.Count == 0; attempt++)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    fileNames = await ReadJobFilesAsync(client, root, plan.TorrentHash, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await ChangeJobAsync(client, root, stopEndpoint, plan.TorrentHash,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        else
        {
            await ChangeJobAsync(client, root, stopEndpoint, plan.TorrentHash, cancellationToken)
                .ConfigureAwait(false);
        }

        if (fileNames.Count == 0)
            throw new InvalidOperationException(
                "qBittorrent magnet meta verisini henüz alamadı. Torrent durduruldu; qBittorrent'i kontrol edin.");

        job = await ReadJobAsync(client, root, plan.TorrentHash, cancellationToken).ConfigureAwait(false);
        var reportedSavePath = job is not null &&
            job.Value.TryGetProperty("save_path", out var savePathProperty)
            ? savePathProperty.GetString() : null;
        if (!SameLocalPath(reportedSavePath, plan.SaveDirectory) &&
            !SameLocalPath(reportedSavePath, plan.ContentDirectory))
            throw new InvalidOperationException(
                "qBittorrent indirme klasörü eşleşmiyor. Torrent durduruldu; klasörü kontrol edin.");

        // qBittorrent may report file names relative to either the save folder
        // or the torrent's content folder (and may include the root name).
        // Accept only a consistent mapping of *all* files to the actual
        // MonoTorrent on-disk paths; never invent renames or move data.
        if (!MatchExpectedFiles(plan, fileNames))
            throw new InvalidOperationException(
                "qBittorrent dosya yolu eşleşmiyor. Torrent durduruldu; dosyalar korundu.");

        progress?.Report(new QBittorrentHandoffProgress("Checking", 0));
        await ChangeJobAsync(client, root, "api/v2/torrents/recheck",
            plan.TorrentHash, cancellationToken).ConfigureAwait(false);
        await EnsureRecheckFinishedAndStartedAsync(
            client, root, plan.TorrentHash, startEndpoint, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Recheck is asynchronous in qBittorrent. Calling start once immediately
    /// after recheck often leaves the new job paused. Watch the same infohash,
    /// start only when it is no longer checking, and verify it left paused state.
    /// Never use force-start (would override user queue/seeding policy).
    /// </summary>
    private static async Task EnsureRecheckFinishedAndStartedAsync(
        HttpClient client, Uri root, string hash, string startEndpoint,
        IProgress<QBittorrentHandoffProgress>? progress, CancellationToken token)
    {
        var sawChecking = false;
        var lastStartAttempt = -20;
        var startCount = 0;
        for (var attempt = 0; attempt < 360; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var job = await ReadJobAsync(client, root, hash, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("qBittorrent torrent kaydı bulunamadı.");
            var state = job.TryGetProperty("state", out var stateValue)
                ? stateValue.GetString() ?? "unknown" : "unknown";
            var fraction = job.TryGetProperty("progress", out var rawProgress) &&
                           rawProgress.ValueKind == JsonValueKind.Number
                ? Math.Clamp(rawProgress.GetDouble(), 0, 1) : 0;

            if (state.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("missingFiles", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("qBittorrent dosyalara erişemiyor. Dosyalar korundu.");

            var checking = state.StartsWith("checking", StringComparison.OrdinalIgnoreCase) ||
                           state.Equals("queuedForChecking", StringComparison.OrdinalIgnoreCase) ||
                           state.Equals("moving", StringComparison.OrdinalIgnoreCase) ||
                           state.Equals("allocating", StringComparison.OrdinalIgnoreCase);
            if (checking)
            {
                sawChecking = true;
                progress?.Report(new QBittorrentHandoffProgress("Checking", fraction));
            }
            else
            {
                var paused = state.StartsWith("paused", StringComparison.OrdinalIgnoreCase) ||
                             state.StartsWith("stopped", StringComparison.OrdinalIgnoreCase);
                if (!paused)
                {
                    // Queued, stalled, downloading and uploading are valid
                    // started states. StalledUP means seeding with no peers.
                    var started = state.StartsWith("queued", StringComparison.OrdinalIgnoreCase) ||
                                  state.StartsWith("stalled", StringComparison.OrdinalIgnoreCase) ||
                                  state.StartsWith("forced", StringComparison.OrdinalIgnoreCase) ||
                                  state.Equals("uploading", StringComparison.OrdinalIgnoreCase) ||
                                  state.Equals("downloading", StringComparison.OrdinalIgnoreCase);
                    if (started && (sawChecking || attempt >= 7 || startCount > 0))
                    {
                        progress?.Report(new QBittorrentHandoffProgress(
                            fraction >= 0.999999 ? "Seeding" : "Downloading", fraction));
                        return;
                    }
                }

                // A rapid scan may complete before we observe checking.
                // Give the recheck queue a few seconds before first start.
                if (paused && (sawChecking || attempt >= 7) &&
                    startCount < 5 && attempt - lastStartAttempt >= 6)
                {
                    progress?.Report(new QBittorrentHandoffProgress("Starting", fraction));
                    await ChangeJobAsync(client, root, startEndpoint, hash, token)
                        .ConfigureAwait(false);
                    startCount++;
                    lastStartAttempt = attempt;
                    sawChecking = false; // if another check begins, wait again
                }
            }
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            "qBittorrent kontrolü henüz tamamlanmadı veya torrent durduruldu. qBittorrent'i kontrol edin.");
    }

    private static async Task<IReadOnlyList<string>> ReadJobFilesAsync(
        HttpClient client, Uri root, string hash, CancellationToken token)
    {
        using var response = await client.GetAsync(
            new Uri(root, "api/v2/torrents/files?hash=" + Uri.EscapeDataString(hash)),
            token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var names = new List<string>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
                return Array.Empty<string>();
            names.Add(name.GetString()!);
        }
        return names;
    }

    private static bool MatchExpectedFiles(
        SmartTorrentHandoffPlan plan, IReadOnlyList<string> names)
    {
        if (names.Count != plan.ExpectedFiles.Count) return false;
        var expected = new HashSet<string>(
            plan.ExpectedFiles, StringComparer.OrdinalIgnoreCase);
        var roots = new[] { plan.SaveDirectory, plan.ContentDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names)
            {
                var relative = raw.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative)) break;
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (!expected.Contains(candidate) || !matches.Add(candidate))
                    break;
            }
            if (matches.SetEquals(expected)) return true;
        }
        return false;
    }

    private static Uri ValidateSameMachineWebUi(string address)
    {
        var root = ValidateBaseAddress(address);
        if (!root.IsLoopback && !root.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Akıllı aktarım yalnızca aynı bilgisayardaki qBittorrent ile kullanılabilir.");
        return root;
    }

    private static bool SameLocalPath(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return false;
        try
        {
            return Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(second).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static async Task<bool> JobExistsAsync(
        HttpClient client, Uri root, string hash, CancellationToken token) =>
        await ReadJobAsync(client, root, hash, token).ConfigureAwait(false) is not null;

    private static async Task<JsonElement?> ReadJobAsync(
        HttpClient client, Uri root, string hash, CancellationToken token)
    {
        using var response = await client.GetAsync(
            new Uri(root, "api/v2/torrents/info?hashes=" + Uri.EscapeDataString(hash)),
            token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var element in json.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("hash", out var itemHash) &&
                hash.Equals(itemHash.GetString(), StringComparison.OrdinalIgnoreCase))
                return element.Clone();
        }
        return null;
    }

    private static async Task ChangeJobAsync(
        HttpClient client, Uri root, string endpoint, string hash, CancellationToken token)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hash });
        using var response = await client.PostAsync(new Uri(root, endpoint), body, token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadVersionAsync(
        HttpClient client, Uri root, CancellationToken token)
    {
        using var response = await client.GetAsync(new Uri(root, "api/v2/app/version"), token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
    }

    private static HttpClient CreateClient(Uri root)
    {
        // No auto redirects: never forward WebUI credentials to another host.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            UseProxy = false
        };
        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.Referrer = root;
        return client;
    }

    private static async Task AuthenticateAsync(
        HttpClient client, Uri root, string username, string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("qBittorrent kullanıcı adı ve parolasını girin.");

        using var payload = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username.Trim(),
            ["password"] = password
        });
        using var response = await client.PostAsync(new Uri(root, "api/v2/auth/login"),
            payload, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (!body.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("qBittorrent giriş bilgileri doğrulanamadı.");
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("qBittorrent sunucusu beklenmeyen yönlendirme döndürdü.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("qBittorrent erişimi reddetti. WebUI ayarlarını kontrol edin.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"qBittorrent bağlantısı başarısız (HTTP {(int)response.StatusCode}).");
        return Task.CompletedTask;
    }
}
