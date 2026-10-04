using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Resolves public PixelDrain files and lists only through the documented API.
/// No page scraping, browser session or API key is required for public reads.
/// </summary>
internal sealed class PixelDrainLinkResolver
{
    private const int MaximumJsonBytes = 4 * 1024 * 1024;
    private const string UserAgent = "AltyaziDB-Player-Windows/0.9";
    private readonly HttpClient _client;

    public PixelDrainLinkResolver(HttpClient client)
    {
        _client = client;
    }

    public static bool IsSupported(Uri uri) =>
        uri.Host.Equals("pixeldrain.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".pixeldrain.com", StringComparison.OrdinalIgnoreCase);

    public async Task<RemoteBrowseResult> ResolveAsync(string source, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !IsSupported(uri))
            throw new InvalidOperationException("Geçerli bir PixelDrain bağlantısı girin.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (TryReadFileId(segments, out var fileId))
            return await ResolveFileAsync(fileId, source, cancellationToken).ConfigureAwait(false);
        if (TryReadListId(segments, out var listId))
            return await ResolveListAsync(listId, cancellationToken).ConfigureAwait(false);

        throw new InvalidOperationException(
            "PixelDrain için /u/{dosya-kimliği}, /l/{liste-kimliği} veya resmî /api/file adresini kullanın.");
    }

    private async Task<RemoteBrowseResult> ResolveFileAsync(
        string fileId,
        string persistentSource,
        CancellationToken cancellationToken)
    {
        ValidateId(fileId);
        var infoUri = new Uri($"https://pixeldrain.com/api/file/{Uri.EscapeDataString(fileId)}/info");
        using var document = await GetJsonAsync(infoUri, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccess(root, "PixelDrain dosya bilgisi alınamadı.");

        var availability = GetString(root, "availability");
        if (!string.IsNullOrWhiteSpace(availability))
            throw new InvalidOperationException(
                GetString(root, "availability_message") ?? PixelDrainErrorMessage(availability));
        if (root.TryGetProperty("can_download", out var canDownload) &&
            canDownload.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("Bu PixelDrain dosyası public indirmeye açık değil.");

        var name = GetString(root, "name") ?? $"PixelDrain {fileId}";
        var directUri = new Uri($"https://pixeldrain.com/api/file/{Uri.EscapeDataString(fileId)}");
        var headers = BuildHeaders(fileId);
        var playableUri = await ValidateByteRangeAsync(directUri, headers, cancellationToken).ConfigureAwait(false);
        return new RemoteBrowseResult(
            RemoteSourceProvider.PixelDrain,
            name,
            null,
            [],
            new RemoteOpenRequest(playableUri.ToString(), name, headers, "PixelDrain", persistentSource),
            StatusMessage: "PixelDrain akışı ileri sarılabilir olarak doğrulandı.");
    }

    private async Task<RemoteBrowseResult> ResolveListAsync(string listId, CancellationToken cancellationToken)
    {
        ValidateId(listId);
        var endpoint = new Uri($"https://pixeldrain.com/api/list/{Uri.EscapeDataString(listId)}");
        using var document = await GetJsonAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccess(root, "PixelDrain listesi açılamadı.");

        var items = new List<RemoteSourceItem>();
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;
                var id = GetString(file, "id");
                if (string.IsNullOrWhiteSpace(id) || !IsSafeId(id)) continue;
                var name = GetString(file, "name") ?? $"PixelDrain {id}";
                var availability = GetString(file, "availability");
                var canDownload = !file.TryGetProperty("can_download", out var canDownloadNode) ||
                                  canDownloadNode.ValueKind != JsonValueKind.False;
                var openUrl = string.IsNullOrWhiteSpace(availability) && canDownload
                    ? $"https://pixeldrain.com/u/{Uri.EscapeDataString(id)}"
                    : null;
                items.Add(new RemoteSourceItem(
                    id,
                    name,
                    DetectKind(name, GetString(file, "mime_type")),
                    openUrl,
                    null,
                    GetLong(file, "size"),
                    ParseTimestamp(file),
                    openUrl is null ? null : BuildHeaders(id),
                    "PixelDrain"));
            }
        }

        return new RemoteBrowseResult(
            RemoteSourceProvider.PixelDrain,
            GetString(root, "title") ?? "PixelDrain listesi",
            BuildListState(listId),
            items,
            StatusMessage: $"{items.Count} PixelDrain öğesi bulundu.");
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadLimitedStringAsync(response.Content, MaximumJsonBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateHttpException(response.StatusCode, body);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("PixelDrain geçerli bir API yanıtı döndürmedi.", exception);
        }
    }

    private async Task<Uri> ValidateByteRangeAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.Range = new RangeHeaderValue(0, 1);
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadLimitedStringAsync(response.Content, 128 * 1024, cancellationToken).ConfigureAwait(false);
            throw CreateHttpException(response.StatusCode, body);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var contentRange = response.Content.Headers.ContentRange;
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            response.StatusCode != HttpStatusCode.PartialContent ||
            contentRange is null ||
            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            contentRange.From != 0)
            throw new InvalidOperationException(
                "PixelDrain dosyası ileri sarılabilir medya akışı vermedi. Dosya limite girmiş veya erişime kapanmış olabilir.");
        return response.RequestMessage?.RequestUri ?? uri;
    }

    private static IReadOnlyDictionary<string, string> BuildHeaders(string fileId) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = $"https://pixeldrain.com/u/{fileId}"
        };

    private static Exception CreateHttpException(HttpStatusCode statusCode, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var value = GetString(root, "value");
            var message = GetString(root, "message");
            return new InvalidOperationException(
                !string.IsNullOrWhiteSpace(value) ? PixelDrainErrorMessage(value, message) :
                !string.IsNullOrWhiteSpace(message) ? message :
                $"PixelDrain HTTP {(int)statusCode} yanıtı verdi.");
        }
        catch (JsonException)
        {
            return new InvalidOperationException($"PixelDrain HTTP {(int)statusCode} yanıtı verdi.");
        }
    }

    private static string PixelDrainErrorMessage(string value, string? fallback = null) => value switch
    {
        "not_found" => "PixelDrain dosyası veya listesi bulunamadı.",
        "file_rate_limited_captcha_required" => "PixelDrain bu dosya için tarayıcıda CAPTCHA tamamlanmasını istiyor.",
        "virus_detected_captcha_required" => "PixelDrain bu dosyayı güvenlik nedeniyle yalnız CAPTCHA sonrasında açıyor.",
        "hotlink_detected" => "PixelDrain bu dosyada haricî oynatmayı sınırlandırdı.",
        "ip_download_limited_captcha_required" => "PixelDrain günlük indirme sınırına ulaşıldığını bildirdi.",
        "server_overload_captcha_required" => "PixelDrain sunucusu yoğun; dosya şu anda yalnız tarayıcı doğrulamasıyla açılabiliyor.",
        "max_concurrent_downloads" => "PixelDrain eşzamanlı indirme sınırına ulaşıldığını bildirdi.",
        "transfer_limit_exceeded" => "PixelDrain ücretsiz aktarım sınırı aşıldı.",
        "download_limit_exceeded" => "PixelDrain ücretsiz indirme sınırı aşıldı.",
        "embed_not_allowed" => "PixelDrain dosya sahibinin haricî oynatmaya izin vermediğini bildirdi.",
        "unavailable_for_legal_reasons" => "PixelDrain dosyası yasal nedenle kullanılamıyor.",
        _ => fallback ?? $"PixelDrain isteği başarısız oldu ({value})."
    };

    private static void EnsureSuccess(JsonElement root, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException(PixelDrainErrorMessage(
                GetString(root, "value") ?? "unknown",
                GetString(root, "message") ?? fallback));
    }

    private static bool TryReadFileId(IReadOnlyList<string> segments, out string id)
    {
        id = string.Empty;
        if (segments.Count >= 2 && segments[0].Equals("u", StringComparison.OrdinalIgnoreCase))
            id = segments[1];
        else if (segments.Count >= 3 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                 segments[1].Equals("file", StringComparison.OrdinalIgnoreCase))
            id = segments[2];
        return !string.IsNullOrWhiteSpace(id);
    }

    private static bool TryReadListId(IReadOnlyList<string> segments, out string id)
    {
        id = string.Empty;
        if (segments.Count >= 2 && segments[0].Equals("l", StringComparison.OrdinalIgnoreCase))
            id = segments[1];
        else if (segments.Count >= 3 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                 segments[1].Equals("list", StringComparison.OrdinalIgnoreCase))
            id = segments[2];
        return !string.IsNullOrWhiteSpace(id);
    }

    private static void ValidateId(string value)
    {
        if (!IsSafeId(value)) throw new InvalidOperationException("PixelDrain kimliği geçerli değil.");
    }

    private static bool IsSafeId(string value) =>
        value.Length is >= 3 and <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static RemoteSourceItemKind DetectKind(string name, string? mime)
    {
        var extension = Path.GetExtension(name);
        if (mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
            new[] { ".mkv", ".mp4", ".webm", ".avi", ".mov", ".m4v", ".ts", ".m2ts" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase)) return RemoteSourceItemKind.Video;
        if (mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
            new[] { ".aac", ".ac3", ".eac3", ".dts", ".flac", ".mka", ".m4a", ".mp3", ".ogg", ".opus", ".wav" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase)) return RemoteSourceItemKind.Audio;
        if (new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase)) return RemoteSourceItemKind.Subtitle;
        if (new[] { ".zip", ".rar", ".7z" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase)) return RemoteSourceItemKind.Archive;
        return RemoteSourceItemKind.Other;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var node)) return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value)) return value;
        return node.ValueKind == JsonValueKind.String &&
               long.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            ? value
            : null;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element)
    {
        var value = GetString(element, "date_upload");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string BuildListState(string listId) => $"https://pixeldrain.com/l/{Uri.EscapeDataString(listId)}";

    private static async Task<string> ReadLimitedStringAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
            throw new InvalidOperationException("PixelDrain yanıtı güvenli boyut sınırını aşıyor.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidOperationException("PixelDrain yanıtı güvenli boyut sınırını aşıyor.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
