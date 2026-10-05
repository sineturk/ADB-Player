using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Resolves AkiraBox share links using the documented public file-status API
/// first, then an optional user API key/token fallback.
///
/// Public file status:
///   GET /api/files?url={shareUrl}
///
/// Authenticated file listing:
///   GET /api/files/list?api_key=...&page=...&per_page=...
///
/// AkiraBox's public API verifies availability and metadata but does not expose
/// a separate documented "resolve signed media URL" endpoint. Therefore the
/// share URL itself is probed for byte-range media/redirects. If that public
/// probe is not enough, a user-supplied API key/token can be used to validate
/// ownership and retry the same share link with authenticated headers.
/// </summary>
internal sealed class AkiraBoxPublicLinkResolver : IDisposable
{
    private const string DefaultApiBase = "https://akirabox.com/api";
    private const string WebsiteOrigin = "https://akirabox.com";
    private const string ApiBaseSecretKey = "akirabox:api-base-url";
    private const string ApiCredentialSecretKey = "akirabox:api-key";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";
    private const int PageSize = 100;
    private const int MaximumListPages = 20;

    private readonly HttpClient _client;
    private readonly ISecretStore? _secrets;
    private readonly IAppLogger _logger;
    private bool _disposed;

    public AkiraBoxPublicLinkResolver(
        IAppLogger logger,
        ISecretStore? secrets = null)
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
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public static bool IsSupported(Uri uri)
    {
        if (!IsAkiraBoxHost(uri.Host))
            return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
               segments[1].Equals("file", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(segments[0]);
    }

    public async Task<RemoteBrowseResult> ResolveAsync(
        Uri originalUri,
        CancellationToken cancellationToken)
    {
        if (!IsSupported(originalUri))
            throw new InvalidOperationException("AkiraBox dosya bağlantısı geçerli değil.");

        var apiBase = await GetApiBaseAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await GetPublicStatusAsync(
                apiBase,
                originalUri,
                cancellationToken)
            .ConfigureAwait(false);

        var shareUri = Uri.TryCreate(metadata.Url, UriKind.Absolute, out var apiUri) &&
                       IsSupported(apiUri)
            ? apiUri
            : originalUri;

        var publicHeaders = BuildPlaybackHeaders(null);
        var publicProbe = await ProbeShareAsync(
                shareUri,
                publicHeaders,
                cancellationToken)
            .ConfigureAwait(false);

        if (publicProbe is { IsPlayable: true })
        {
            _logger.Info(
                $"AkiraBox public link byte-range medya olarak çözüldü: {metadata.Name} · HTTP {publicProbe.StatusCode}");
            return BuildDirectResult(
                metadata,
                publicProbe.FinalUri ?? shareUri,
                publicHeaders,
                "AkiraBox public bağlantısı oynatmaya hazır.");
        }

        var credential = await GetApiCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential))
        {
            var publicDetail = publicProbe?.Description ?? "public byte-range akışı alınamadı";
            throw new InvalidOperationException(
                $"AkiraBox dosyası mevcut ancak public bağlantı doğrudan medya akışı vermedi ({publicDetail}). " +
                "Ayarlar > AkiraBox API bölümüne kendi API Key / Token bilginizi ekleyip yeniden deneyin.");
        }

        var authenticatedUri = await FindOwnedFileLinkAsync(
                apiBase,
                credential,
                shareUri,
                cancellationToken)
            .ConfigureAwait(false)
            ?? shareUri;

        var authenticatedHeaders = BuildPlaybackHeaders(credential);
        var authenticatedProbe = await ProbeShareAsync(
                authenticatedUri,
                authenticatedHeaders,
                cancellationToken)
            .ConfigureAwait(false);

        if (authenticatedProbe is { IsPlayable: true })
        {
            _logger.Info(
                $"AkiraBox authenticated fallback çözüldü: {metadata.Name} · HTTP {authenticatedProbe.StatusCode}");
            return BuildDirectResult(
                metadata,
                authenticatedProbe.FinalUri ?? authenticatedUri,
                authenticatedHeaders,
                "AkiraBox API kimliği ile akış hazır.");
        }

        throw new InvalidOperationException(
            "AkiraBox bağlantısı API ile doğrulandı ancak oynatılabilir byte-range medya adresi alınamadı. " +
            "AkiraBox bu paylaşım için ayrı bir signed download URL gerektiriyor olabilir; mevcut belgelenmiş API bu URL'yi doğrudan döndürmüyor.");
    }

    private async Task<AkiraBoxMetadata> GetPublicStatusAsync(
        Uri apiBase,
        Uri shareUri,
        CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(new Uri(apiBase, "./files"));
        builder.Query = "url=" + Uri.EscapeDataString(shareUri.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        AddCommonHeaders(request);

        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"AkiraBox public status API geçersiz JSON döndürdü (HTTP {(int)response.StatusCode}).",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            var status = GetInt32(root, "status") ?? (int)response.StatusCode;
            if (!response.IsSuccessStatusCode || status != 200)
            {
                var message = GetString(root, "message")
                    ?? response.ReasonPhrase
                    ?? "bilinmeyen hata";
                throw new InvalidOperationException(
                    $"AkiraBox public status API HTTP {status}: {message}");
            }

            var name = GetString(root, "name") ?? FileNameFromShare(shareUri);
            var mime = GetString(root, "mime") ?? string.Empty;
            var type = GetString(root, "type") ?? "file";
            var url = GetString(root, "url") ?? shareUri.ToString();
            var sizeText = GetString(root, "size");

            if (!type.Equals("file", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"AkiraBox içerik türü desteklenmiyor: {type}");

            return new AkiraBoxMetadata(
                name,
                mime,
                url,
                sizeText);
        }
    }

    private async Task<ProbeResult?> ProbeShareAsync(
        Uri shareUri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(7));

            using var request = new HttpRequestMessage(HttpMethod.Get, shareUri);
            request.Headers.Range = new RangeHeaderValue(0, 1);
            foreach (var pair in headers)
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);

            using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    probeCts.Token)
                .ConfigureAwait(false);

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var disposition = response.Content.Headers.ContentDisposition;
            var finalUri = response.RequestMessage?.RequestUri ?? shareUri;
            var isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                         mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

            var hasByteRange =
                response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange is { Unit: var unit } &&
                string.Equals(unit, "bytes", StringComparison.OrdinalIgnoreCase);

            var looksBinary200 =
                response.StatusCode == HttpStatusCode.OK &&
                !isHtml &&
                (disposition is not null ||
                 mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase));

            var playable = hasByteRange || looksBinary200;
            return new ProbeResult(
                playable,
                (int)response.StatusCode,
                finalUri,
                playable
                    ? hasByteRange ? "HTTP 206 byte-range" : $"HTTP 200 {mediaType}"
                    : $"HTTP {(int)response.StatusCode} {mediaType}".Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warning("AkiraBox public-link ön doğrulaması zaman aşımına uğradı.");
            return new ProbeResult(false, 0, null, "ön doğrulama zaman aşımı");
        }
        catch (HttpRequestException exception)
        {
            _logger.Warning($"AkiraBox public-link ön doğrulaması başarısız: {exception.Message}");
            return new ProbeResult(false, 0, null, "ağ hatası");
        }
    }

    private async Task<Uri?> FindOwnedFileLinkAsync(
        Uri apiBase,
        string credential,
        Uri shareUri,
        CancellationToken cancellationToken)
    {
        var targetCode = ExtractFileCode(shareUri);
        if (string.IsNullOrWhiteSpace(targetCode))
            return null;

        for (var page = 1; page <= MaximumListPages; page++)
        {
            var result = await GetOwnedFilesPageAsync(
                    apiBase,
                    credential,
                    page,
                    useApiTokenParameter: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.AuthenticationRejected && page == 1)
            {
                result = await GetOwnedFilesPageAsync(
                        apiBase,
                        credential,
                        page,
                        useApiTokenParameter: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (result.AuthenticationRejected)
            {
                throw new InvalidOperationException(
                    "AkiraBox API Key / Token reddedildi. Ayarlar > AkiraBox API alanını kontrol edin.");
            }

            foreach (var file in result.Files)
            {
                if (!file.FileCode.Equals(targetCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Uri.TryCreate(file.Link, UriKind.Absolute, out var matchedUri))
                    return matchedUri;
            }

            if (result.Files.Count < PageSize)
                break;
        }

        _logger.Warning(
            "AkiraBox API kimliği geçerli ancak paylaşım kullanıcının ilk 2000 dosyası içinde bulunamadı; authenticated share probe doğrudan denenecek.");
        return null;
    }

    private async Task<OwnedFilesPage> GetOwnedFilesPageAsync(
        Uri apiBase,
        string credential,
        int page,
        bool useApiTokenParameter,
        CancellationToken cancellationToken)
    {
        var parameterName = useApiTokenParameter ? "api_token" : "api_key";
        var builder = new UriBuilder(new Uri(apiBase, "./files/list"));
        builder.Query =
            $"{parameterName}={Uri.EscapeDataString(credential)}" +
            $"&page={page.ToString(CultureInfo.InvariantCulture)}" +
            $"&per_page={PageSize.ToString(CultureInfo.InvariantCulture)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        AddCommonHeaders(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"AkiraBox authenticated list API geçersiz JSON döndürdü (HTTP {(int)response.StatusCode}).",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            var status = GetInt32(root, "status") ?? (int)response.StatusCode;
            if (status is 401 or 403)
                return new OwnedFilesPage(true, []);

            if (!response.IsSuccessStatusCode || status != 200)
            {
                var message = GetString(root, "message")
                    ?? GetString(root, "msg")
                    ?? response.ReasonPhrase
                    ?? "bilinmeyen hata";
                throw new InvalidOperationException(
                    $"AkiraBox authenticated list API HTTP {status}: {message}");
            }

            if (!TryGetProperty(root, "result", out var result) ||
                result.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(result, "files", out var filesElement) ||
                filesElement.ValueKind != JsonValueKind.Array)
            {
                return new OwnedFilesPage(false, []);
            }

            var files = new List<OwnedFile>();
            foreach (var file in filesElement.EnumerateArray())
            {
                var code = GetString(file, "file_code");
                var link = GetString(file, "link");
                if (string.IsNullOrWhiteSpace(code) ||
                    string.IsNullOrWhiteSpace(link))
                    continue;

                files.Add(new OwnedFile(code, link));
            }

            return new OwnedFilesPage(false, files);
        }
    }

    private async Task<Uri> GetApiBaseAsync(CancellationToken cancellationToken)
    {
        var configured = _secrets is null
            ? null
            : await _secrets
                .GetAsync(ApiBaseSecretKey, cancellationToken)
                .ConfigureAwait(false);

        var value = string.IsNullOrWhiteSpace(configured)
            ? DefaultApiBase
            : configured.Trim();

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "AkiraBox API adresi geçerli bir HTTPS adresi olmalıdır.");
        }

        return EnsureTrailingSlash(uri);
    }

    private async Task<string?> GetApiCredentialAsync(CancellationToken cancellationToken)
    {
        if (_secrets is null)
            return null;

        var credential = await _secrets
            .GetAsync(ApiCredentialSecretKey, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(credential)
            ? null
            : credential.Trim();
    }

    private static RemoteBrowseResult BuildDirectResult(
        AkiraBoxMetadata metadata,
        Uri source,
        IReadOnlyDictionary<string, string> headers,
        string statusMessage)
    {
        return new RemoteBrowseResult(
            RemoteSourceProvider.AkiraBox,
            metadata.Name,
            null,
            [],
            new RemoteOpenRequest(
                source.ToString(),
                metadata.Name,
                headers,
                "AkiraBox",
                metadata.Url),
            StatusMessage: statusMessage);
    }

    private static IReadOnlyDictionary<string, string> BuildPlaybackHeaders(
        string? credential)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = WebsiteOrigin + "/"
        };

        if (!string.IsNullOrWhiteSpace(credential))
            headers["Authorization"] = "Bearer " + credential.Trim();

        return headers;
    }

    private static void AddCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Referrer = new Uri(WebsiteOrigin + "/");
    }

    private static bool IsAkiraBoxHost(string host) =>
        host.Equals("akirabox.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.akirabox.com", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractFileCode(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
               segments[1].Equals("file", StringComparison.OrdinalIgnoreCase)
            ? segments[0]
            : null;
    }

    private static string FileNameFromShare(Uri uri)
    {
        var code = ExtractFileCode(uri);
        return string.IsNullOrWhiteSpace(code)
            ? "AkiraBox dosyası"
            : $"AkiraBox {code}";
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/");

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
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

    private static int? GetInt32(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number))
            return number;

        return int.TryParse(
            value.ToString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }

    private sealed record AkiraBoxMetadata(
        string Name,
        string Mime,
        string Url,
        string? SizeText);

    private sealed record ProbeResult(
        bool IsPlayable,
        int StatusCode,
        Uri? FinalUri,
        string Description);

    private sealed record OwnedFile(
        string FileCode,
        string Link);

    private sealed record OwnedFilesPage(
        bool AuthenticationRejected,
        IReadOnlyList<OwnedFile> Files);
}
