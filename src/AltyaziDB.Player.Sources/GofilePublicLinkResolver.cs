using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Resolves Gofile public shares through the official REST API.
///
/// Folder/file metadata reads (`GET /contents/{id}`) are Premium-only in the
/// current Gofile API. ADB Player therefore uses a user-supplied Gofile account
/// token stored in the existing DPAPI-backed secret store. No guest-account
/// creation, website-token scraping or rotating-salt emulation is used.
///
/// Already-resolved `file-*.gofile.io/download/web/...` URLs are still accepted
/// directly; the configured account token is attached when available.
/// </summary>
internal sealed class GofilePublicLinkResolver : IDisposable
{
    private const string ApiBase = "https://api.gofile.io";
    private const string WebsiteOrigin = "https://gofile.io";
    private const string ConfiguredAccountTokenSecretKey = "gofile:api-token";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";
    private const int PageSize = 1000;

    private readonly HttpClient _client;
    private readonly IAppLogger _logger;
    private readonly ISecretStore? _secrets;
    private bool _disposed;

    public GofilePublicLinkResolver(IAppLogger logger, ISecretStore? secrets = null)
    {
        _logger = logger;
        _secrets = secrets;
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            MaxAutomaticRedirections = 8
        })
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
    }

    public static bool IsSupported(Uri uri) =>
        (uri.Host.Equals("gofile.io", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("www.gofile.io", StringComparison.OrdinalIgnoreCase)) &&
        TryExtractContentId(uri) is not null;

    public static bool IsDirectDownloadSupported(Uri uri) =>
        uri.Host.EndsWith(".gofile.io", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Contains("/download/web/", StringComparison.OrdinalIgnoreCase);

    public async Task<RemoteBrowseResult> ResolveDirectDownloadAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (!IsDirectDownloadSupported(uri))
            throw new InvalidOperationException("Gofile doğrudan dosya bağlantısı geçerli değil.");

        var accountToken = await GetConfiguredAccountTokenAsync(
                required: false,
                cancellationToken)
            .ConfigureAwait(false);
        var headers = string.IsNullOrWhiteSpace(accountToken)
            ? BuildAnonymousDownloadHeaders()
            : BuildDownloadHeaders(accountToken);

        // A short probe is useful, but it must never keep the player closed for
        // a signed direct URL. If the probe is slow/unreachable, hand the URL to
        // libmpv and let its normal network/cache path decide.
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(5));
            if (await ProbeDirectDownloadAsync(uri, headers, probeCts.Token).ConfigureAwait(false))
            {
                _logger.Info(
                    string.IsNullOrWhiteSpace(accountToken)
                        ? "Gofile direct-store URL byte-range doğrulandı."
                        : "Gofile direct-store URL kullanıcı account tokenı ile byte-range doğrulandı.");
            }
            else
            {
                _logger.Warning("Gofile direct-store ön doğrulaması 206 döndürmedi; URL yine de libmpv'ye aktarılacak.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warning("Gofile direct-store ön doğrulaması zaman aşımına uğradı; doğrudan libmpv handoff kullanılacak.");
        }
        catch (HttpRequestException exception)
        {
            _logger.Warning($"Gofile direct-store ön doğrulaması başarısız: {exception.Message}. Doğrudan libmpv handoff kullanılacak.");
        }

        return BuildDirectResult(uri, headers);
    }

    public async Task<RemoteBrowseResult> ResolveAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var state = ParseState(source);
        var token = await GetConfiguredAccountTokenAsync(
                required: true,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Gofile API tokenı bulunamadı.");

        return await BrowseAsync(state, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteBrowseResult> BrowseAsync(
        GofileState state,
        string token,
        CancellationToken cancellationToken)
    {
        var contentId = state.ContentId;
        var pages = new List<JsonElement>();
        string? displayName = null;
        string? contentType = null;
        string? directLink = null;
        var totalPages = 1;

        for (var page = 1; page <= totalPages; page++)
        {
            using var document = await GetContentPageAsync(
                    contentId,
                    token,
                    page,
                    cancellationToken)
                .ConfigureAwait(false);

            var root = document.RootElement;
            var status = GetString(root, "status") ?? string.Empty;
            if (!status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                ThrowStatus(status, contentId);

            if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Gofile API içerik bilgisi döndürmedi.");

            if (GetBoolean(data, "canAccess") == false && HasProperty(data, "canAccess"))
                throw new InvalidOperationException("Gofile paylaşımına erişilemiyor.");

            var passwordProtected = GetBoolean(data, "password");
            var passwordStatus = GetString(data, "passwordStatus");
            if (passwordProtected &&
                !string.Equals(passwordStatus, "passwordOk", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Bu Gofile paylaşımı parola korumalı. V1.0.1'de parola korumalı Gofile paylaşımları henüz desteklenmiyor.");
            }

            if (GetBoolean(data, "public") == false && HasProperty(data, "public"))
                throw new InvalidOperationException("Bu Gofile içeriği herkese açık değil.");

            displayName ??= GetString(data, "name") ?? "Gofile paylaşımı";
            contentType ??= GetString(data, "type");
            directLink ??= GetString(data, "link");
            pages.Add(data.Clone());

            if (TryGetProperty(root, "metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object)
            {
                totalPages = Math.Max(1, GetInt32(metadata, "totalPages") ?? totalPages);
            }
        }

        if (string.Equals(contentType, "file", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directLink))
                throw new InvalidOperationException("Gofile dosya indirme adresi bulunamadı.");

            var headers = BuildDownloadHeaders(token);
            return new RemoteBrowseResult(
                RemoteSourceProvider.Gofile,
                displayName ?? "Gofile dosyası",
                null,
                [],
                new RemoteOpenRequest(
                    directLink,
                    displayName ?? "Gofile dosyası",
                    headers,
                    "Gofile",
                    $"https://gofile.io/d/{contentId}"),
                StatusMessage: "Gofile dosyası oynatmaya hazır.");
        }

        if (!string.Equals(contentType, "folder", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Gofile içerik türü desteklenmiyor: {contentType ?? "bilinmiyor"}");

        var items = new List<RemoteSourceItem>();
        foreach (var page in pages)
        {
            foreach (var child in EnumerateChildren(page))
            {
                if (child.ValueKind != JsonValueKind.Object)
                    continue;
                if (GetBoolean(child, "canAccess") == false && HasProperty(child, "canAccess"))
                    continue;

                var id = GetString(child, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = GetString(child, "name") ?? id;
                var type = GetString(child, "type") ?? string.Empty;
                var isFolder = type.Equals("folder", StringComparison.OrdinalIgnoreCase);
                var link = GetString(child, "link");
                var size = GetInt64(child, "size");
                var modified = ParseUnixDate(
                    GetInt64(child, "modTime") ??
                    GetInt64(child, "createTime"));

                if (isFolder)
                {
                    items.Add(new RemoteSourceItem(
                        id,
                        name,
                        RemoteSourceItemKind.Folder,
                        null,
                        BuildChildPath(state, id),
                        null,
                        modified,
                        null,
                        "Gofile"));
                    continue;
                }

                if (!type.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(link))
                    continue;

                items.Add(new RemoteSourceItem(
                    id,
                    name,
                    DetectKind(name),
                    link,
                    null,
                    size,
                    modified,
                    BuildDownloadHeaders(token),
                    "Gofile"));
            }
        }

        items.Sort(ItemComparison);
        _logger.Info($"Gofile Premium API paylaşımı çözüldü: {contentId} · {items.Count} öğe.");
        return new RemoteBrowseResult(
            RemoteSourceProvider.Gofile,
            displayName ?? "Gofile paylaşımı",
            state.CurrentPath,
            items,
            ParentPath: state.ParentPath,
            StatusMessage: $"{items.Count} Gofile öğesi bulundu.");
    }

    private async Task<JsonDocument> GetContentPageAsync(
        string contentId,
        string token,
        int page,
        CancellationToken cancellationToken)
    {
        var query =
            $"page={page.ToString(CultureInfo.InvariantCulture)}" +
            $"&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}" +
            "&sortField=name&sortDirection=1";

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{ApiBase}/contents/{Uri.EscapeDataString(contentId)}?{query}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("Gofile istek sınırına ulaşıldı. Bir süre sonra yeniden deneyin.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Gofile API geçersiz yanıt döndürdü (HTTP {(int)response.StatusCode}).",
                exception);
        }

        var status = GetString(document.RootElement, "status");
        if (!response.IsSuccessStatusCode ||
            !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status, "error-notPremium", StringComparison.OrdinalIgnoreCase))
            {
                document.Dispose();
                throw new InvalidOperationException(
                    "Gofile klasör/dosya listeleme API'si Premium hesaba özeldir. Ayarlar'daki Gofile API Token alanına Premium hesabınıza ait account tokenı girin.");
            }

            if (string.Equals(status, "error-token", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "error-wrongToken", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "error-notAuthenticated", StringComparison.OrdinalIgnoreCase))
            {
                document.Dispose();
                throw new InvalidOperationException(
                    "Gofile API tokenı geçersiz veya reddedildi. Ayarlar > Gofile API Token alanını kontrol edin.");
            }

            document.Dispose();
            throw new InvalidOperationException(
                $"Gofile API HTTP {(int)response.StatusCode}: {status ?? response.ReasonPhrase}");
        }

        return document;
    }

    private async Task<string?> GetConfiguredAccountTokenAsync(
        bool required,
        CancellationToken cancellationToken)
    {
        var token = _secrets is null
            ? null
            : await _secrets
                .GetAsync(ConfiguredAccountTokenSecretKey, cancellationToken)
                .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(token))
            return token.Trim();

        if (required)
        {
            throw new InvalidOperationException(
                "Gofile paylaşım klasörlerini açmak için Premium Gofile account tokenı gerekiyor. Ayarlar > Gofile API Token alanına kendi tokenınızı ekleyin.");
        }

        return null;
    }

    private async Task<bool> ProbeDirectDownloadAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 1);
        foreach (var pair in headers)
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);

        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
            return false;

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null ||
            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
            return false;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        return !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
               !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static RemoteBrowseResult BuildDirectResult(
        Uri uri,
        IReadOnlyDictionary<string, string> headers)
    {
        var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name))
            name = "Gofile dosyası";

        return new RemoteBrowseResult(
            RemoteSourceProvider.Gofile,
            name,
            null,
            [],
            new RemoteOpenRequest(
                uri.ToString(),
                name,
                headers,
                "Gofile",
                uri.ToString()),
            StatusMessage: "Gofile doğrudan dosya akışı hazır.");
    }

    private static IReadOnlyDictionary<string, string> BuildAnonymousDownloadHeaders() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = WebsiteOrigin + "/"
        };

    private static IReadOnlyDictionary<string, string> BuildDownloadHeaders(string token) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = WebsiteOrigin + "/",
            ["Authorization"] = $"Bearer {token}",
            ["Cookie"] = $"accountToken={token}"
        };

    private static GofileState ParseState(string source)
    {
        if (source.StartsWith("gofile://", StringComparison.OrdinalIgnoreCase))
        {
            var custom = new Uri(source);
            var trail = new List<string>();
            if (!string.IsNullOrWhiteSpace(custom.Host))
                trail.Add(custom.Host);
            trail.AddRange(
                custom.AbsolutePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString));

            if (trail.Count == 0)
                throw new InvalidOperationException("Gofile klasör kimliği bulunamadı.");

            var currentPath = BuildCustomPath(trail);
            var parentPath = trail.Count > 1
                ? BuildCustomPath(trail.Take(trail.Count - 1))
                : null;
            return new GofileState(trail[^1], trail, currentPath, parentPath);
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Gofile bağlantısı geçerli değil.");

        var contentId = TryExtractContentId(uri)
            ?? throw new InvalidOperationException("Gofile içerik kodu bağlantıdan çıkarılamadı.");
        var rootTrail = new[] { contentId };
        return new GofileState(
            contentId,
            rootTrail,
            BuildCustomPath(rootTrail),
            null);
    }

    private static string BuildChildPath(GofileState state, string childId) =>
        BuildCustomPath(state.Trail.Concat(new[] { childId }));

    private static string BuildCustomPath(IEnumerable<string> trail)
    {
        var values = trail.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (values.Length == 0)
            throw new InvalidOperationException("Gofile klasör yolu oluşturulamadı.");
        if (values.Length == 1)
            return $"gofile://{values[0]}";
        return $"gofile://{values[0]}/{string.Join("/", values.Skip(1).Select(Uri.EscapeDataString))}";
    }

    private static string? TryExtractContentId(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            segments[0].Equals("d", StringComparison.OrdinalIgnoreCase))
        {
            return segments[1];
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateChildren(JsonElement data)
    {
        if (!TryGetProperty(data, "children", out var children))
            yield break;

        if (children.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in children.EnumerateObject())
                yield return property.Value;
        }
        else if (children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                yield return child;
        }
    }

    private static void ThrowStatus(string status, string contentId)
    {
        throw status switch
        {
            "error-notFound" => new InvalidOperationException("Gofile paylaşımı bulunamadı."),
            "error-passwordRequired" => new InvalidOperationException("Gofile paylaşımı parola korumalı."),
            "error-passwordWrong" => new InvalidOperationException("Gofile paylaşım parolası yanlış."),
            "error-rateLimit" => new InvalidOperationException("Gofile istek sınırına ulaşıldı. Bir süre sonra yeniden deneyin."),
            "error-notPremium" => new InvalidOperationException("Gofile içerik listeleme API'si Premium hesaba özeldir."),
            "error-token" or "error-wrongToken" or "error-notAuthenticated" =>
                new InvalidOperationException("Gofile API tokenı geçersiz veya reddedildi."),
            _ => new InvalidOperationException($"Gofile API hatası ({contentId}): {status}")
        };
    }

    private static RemoteSourceItemKind DetectKind(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.ToLowerInvariant() switch
        {
            ".mkv" or ".mp4" or ".avi" or ".webm" or ".mov" or ".m4v" or ".ts" or
            ".m2ts" or ".mts" or ".mpeg" or ".mpg" or ".wmv" or ".flv" or ".ogv" or
            ".vob" or ".3gp" => RemoteSourceItemKind.Video,
            ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub" => RemoteSourceItemKind.Subtitle,
            ".aac" or ".ac3" or ".eac3" or ".dts" or ".dtshd" or ".thd" or ".truehd" or
            ".flac" or ".mka" or ".m4a" or ".mp3" or ".ogg" or ".opus" or ".wav" =>
                RemoteSourceItemKind.Audio,
            ".zip" or ".rar" or ".7z" => RemoteSourceItemKind.Archive,
            _ => RemoteSourceItemKind.Other
        };
    }

    private static DateTimeOffset? ParseUnixDate(long? value)
    {
        if (value is null or <= 0)
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int ItemComparison(RemoteSourceItem left, RemoteSourceItem right)
    {
        if (left.IsFolder != right.IsFolder)
            return left.IsFolder ? -1 : 1;
        return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
    }

    private static bool HasProperty(JsonElement element, string name) =>
        TryGetProperty(element, name, out _);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed) && parsed);
    }

    private static int? GetInt32(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static long? GetInt64(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed))
            return parsed;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            ? parsed
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
    }

    private sealed record GofileState(
        string ContentId,
        IReadOnlyList<string> Trail,
        string CurrentPath,
        string? ParentPath);
}
