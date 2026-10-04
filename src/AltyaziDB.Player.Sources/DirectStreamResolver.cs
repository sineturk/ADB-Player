using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Validates user supplied direct media, HLS and DASH URLs before libmpv opens
/// them. This prevents HTML error/ad pages from being treated as media files.
/// </summary>
internal sealed class DirectStreamResolver
{
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const string UserAgent = "AltyaziDB-Player-Windows/0.9";
    private readonly HttpClient _client;

    public DirectStreamResolver(HttpClient client)
    {
        _client = client;
    }

    public async Task<RemoteBrowseResult> ResolveAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.ParseAdd(
            "application/vnd.apple.mpegurl,application/x-mpegURL,application/dash+xml,video/*,audio/*,application/octet-stream;q=0.8,*/*;q=0.2");
        var pathHint = uri.AbsolutePath.ToLowerInvariant();
        if (!pathHint.Contains(".m3u8", StringComparison.Ordinal) &&
            !pathHint.Contains(".mpd", StringComparison.Ordinal))
            request.Headers.Range = new RangeHeaderValue(0, 65535);
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Doğrudan akış HTTP {(int)response.StatusCode} yanıtı verdi.");

        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var path = finalUri.AbsolutePath.ToLowerInvariant();
        var manifestHint = IsHlsMediaType(mediaType) || IsDashMediaType(mediaType) ||
                           path.Contains(".m3u8", StringComparison.Ordinal) ||
                           path.Contains(".mpd", StringComparison.Ordinal);
        if (manifestHint || IsTextual(mediaType))
        {
            var body = await ReadLimitedStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var kind = DetectManifest(body);
            if (kind == ManifestKind.None)
            {
                if (IsHtml(body, mediaType))
                    throw new InvalidOperationException(
                        "Girilen adres video yerine bir web sayfası döndürdü. Doğrudan HLS (.m3u8), DASH (.mpd) veya medya dosyası adresini kullanın.");
                throw new InvalidOperationException("Bağlantı geçerli bir HLS veya DASH manifesti döndürmedi.");
            }

            var name = FileName(finalUri, kind == ManifestKind.Hls ? "HLS yayını" : "DASH yayını");
            return DirectResult(
                finalUri,
                name,
                kind == ManifestKind.Hls ? "HLS akışı doğrulandı." : "DASH akışı doğrulandı.");
        }

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Girilen adres medya yerine bir web yanıtı döndürdü.");

        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            contentRange is null ||
            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            contentRange.From != 0)
            throw new InvalidOperationException(
                "Dosya sunucusu ileri sarmayı sağlayan byte-range akışını desteklemiyor.");

        return DirectResult(finalUri, FileName(finalUri, "İnternet videosu"), "İleri sarılabilir medya akışı doğrulandı.");
    }

    private static RemoteBrowseResult DirectResult(Uri uri, string name, string status)
    {
        IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent
        };
        return new RemoteBrowseResult(
            RemoteSourceProvider.Direct,
            name,
            null,
            [],
            new RemoteOpenRequest(uri.ToString(), name, headers, "Doğrudan akış", uri.ToString()),
            StatusMessage: status);
    }

    private static ManifestKind DetectManifest(string body)
    {
        var trimmed = body.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return ManifestKind.Hls;
        if (!trimmed.StartsWith("<", StringComparison.Ordinal)) return ManifestKind.None;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(trimmed),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                return reader.LocalName.Equals("MPD", StringComparison.OrdinalIgnoreCase)
                    ? ManifestKind.Dash
                    : ManifestKind.None;
            }
        }
        catch (XmlException)
        {
            return ManifestKind.None;
        }
        return ManifestKind.None;
    }

    private static bool IsHlsMediaType(string value) =>
        value.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase);

    private static bool IsDashMediaType(string value) =>
        value.Contains("dash+xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextual(string value) =>
        value.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsHtml(string body, string mediaType)
    {
        if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)) return true;
        var trimmed = body.TrimStart();
        return trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(Uri uri, string fallback)
    {
        var value = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static async Task<string> ReadLimitedStringAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumManifestBytes)
            throw new InvalidOperationException("Akış manifesti güvenli boyut sınırını aşıyor.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumManifestBytes)
                throw new InvalidOperationException("Akış manifesti güvenli boyut sınırını aşıyor.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private enum ManifestKind
    {
        None,
        Hls,
        Dash
    }
}
