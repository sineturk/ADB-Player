using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Reads public Gofile share folders through an isolated guest session. The guest
/// token is kept in memory and is never written to settings or logs.
/// </summary>
internal sealed class GofileLinkResolver
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const string WebsiteTokenSalt = "9844d94d963d30";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 AltyaziDB-Player/0.8";
    private readonly HttpClient _client;
    private readonly IAppLogger _logger;
    private readonly CookieContainer? _cookies;
    private string? _guestToken;

    public GofileLinkResolver(HttpClient client, IAppLogger logger, CookieContainer? cookies)
    {
        _client = client;
        _logger = logger;
        _cookies = cookies;
    }

    public static bool IsSupported(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host.Equals("gofile.io", StringComparison.Ordinal) ||
               host.EndsWith(".gofile.io", StringComparison.Ordinal);
    }

    public async Task<RemoteBrowseResult> ResolveAsync(string source, CancellationToken cancellationToken)
    {
        if (source.StartsWith("gofile://", StringComparison.OrdinalIgnoreCase))
        {
            var state = ParseState(source);
            return await BrowseAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !IsSupported(uri))
            throw new InvalidOperationException("Geçerli bir Gofile bağlantısı girin.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("d", StringComparison.OrdinalIgnoreCase))
        {
            var id = segments[1];
            if (!IsSafeContentId(id)) throw new InvalidOperationException("Gofile klasör kimliği geçerli değil.");
            return await BrowseAsync(new[] { id }, cancellationToken).ConfigureAwait(false);
        }

        if (uri.AbsolutePath.Contains("/download/", StringComparison.OrdinalIgnoreCase))
        {
            var headers = await BuildDownloadHeadersAsync(uri, cancellationToken).ConfigureAwait(false);
            var playableUri = await ValidateByteRangeAsync(uri, headers, cancellationToken).ConfigureAwait(false);
            var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (string.IsNullOrWhiteSpace(name)) name = "Gofile videosu";
            return new RemoteBrowseResult(
                RemoteSourceProvider.Gofile,
                name,
                null,
                [],
                new RemoteOpenRequest(playableUri.ToString(), name, headers, "Gofile", source),
                StatusMessage: "Gofile doğrudan dosya bağlantısı hazır.");
        }

        throw new InvalidOperationException("Gofile için /d/{kimlik} paylaşım veya doğrudan indirme bağlantısı kullanın.");
    }

    private async Task<RemoteBrowseResult> BrowseAsync(
        IReadOnlyList<string> state,
        CancellationToken cancellationToken)
    {
        if (state.Count == 0 || state.Any(id => !IsSafeContentId(id)))
            throw new InvalidOperationException("Gofile klasör yolu geçerli değil.");

        var token = await EnsureGuestTokenAsync(cancellationToken).ConfigureAwait(false);
        var contentId = state[^1];
        var endpoint = new Uri(
            $"https://api.gofile.io/contents/{Uri.EscapeDataString(contentId)}" +
            "?cache=true&contentFilter=&page=1&pageSize=1000&sortField=name&sortDirection=1");
        using var request = CreateApiRequest(HttpMethod.Get, endpoint, token);
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadLimitedStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Gofile public klasör API'si HTTP {(int)response.StatusCode} yanıtı verdi. API beta olduğu için geçici olarak değişmiş olabilir.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!TryGetData(root, out var data))
            throw new InvalidOperationException("Gofile klasör yanıtı geçerli değil veya paylaşım artık açık değil.");

        var title = GetString(data, "name") ?? "Gofile paylaşımı";
        var currentPath = BuildState(state);
        var downloadHeaders = BuildDownloadHeaders(token);
        var items = new List<RemoteSourceItem>();
        if (data.TryGetProperty("children", out var children))
        {
            foreach (var child in EnumerateChildren(children))
            {
                var id = GetString(child, "id");
                var name = GetString(child, "name") ?? "Öğe";
                var type = GetString(child, "type") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id) || !IsSafeContentId(id)) continue;
                var isFolder = type.Equals("folder", StringComparison.OrdinalIgnoreCase);
                var link = isFolder ? null : GetString(child, "link");
                items.Add(new RemoteSourceItem(
                    id,
                    name,
                    isFolder ? RemoteSourceItemKind.Folder : DetectKind(name, GetString(child, "mimetype")),
                    link,
                    isFolder ? BuildState(state.Concat(new[] { id }).ToArray()) : null,
                    GetLong(child, "size"),
                    ParseTimestamp(child),
                    isFolder ? null : downloadHeaders,
                    "Gofile"));
            }
        }

        items.Sort((left, right) =>
        {
            var folderOrder = right.IsFolder.CompareTo(left.IsFolder);
            return folderOrder != 0 ? folderOrder : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });
        var parent = state.Count > 1 ? BuildState(state.Take(state.Count - 1).ToArray()) : null;
        return new RemoteBrowseResult(
            RemoteSourceProvider.Gofile,
            title,
            currentPath,
            items,
            ParentPath: parent,
            StatusMessage: $"{items.Count} Gofile öğesi bulundu.");
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildDownloadHeadersAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var token = await EnsureGuestTokenAsync(cancellationToken).ConfigureAwait(false);
        return BuildDownloadHeaders(token, uri);
    }

    private async Task<string> EnsureGuestTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_guestToken)) return _guestToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.gofile.io/accounts");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("X-Website-Token", GenerateWebsiteToken(string.Empty));
        request.Headers.TryAddWithoutValidation("X-BL", "en-US");
        request.Headers.Referrer = new Uri("https://gofile.io/");
        request.Headers.TryAddWithoutValidation("Origin", "https://gofile.io");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadLimitedStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "Gofile geçici public oturumu oluşturulamadı. Sağlayıcının beta API'si kimlik doğrulama akışını değiştirmiş olabilir.");
        using var document = JsonDocument.Parse(body);
        if (!TryGetData(document.RootElement, out var data) ||
            !data.TryGetProperty("token", out var tokenNode) ||
            tokenNode.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenNode.GetString()))
            throw new InvalidOperationException("Gofile geçici oturum belirteci alınamadı.");
        _guestToken = tokenNode.GetString();
        try
        {
            _cookies?.Add(new Uri("https://gofile.io/"), new Cookie("accountToken", _guestToken, "/", ".gofile.io"));
        }
        catch (CookieException exception)
        {
            _logger.Warning($"Gofile oturum çerezi eklenemedi: {exception.GetType().Name}");
        }
        return _guestToken!;
    }

    private static HttpRequestMessage CreateApiRequest(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("X-Website-Token", GenerateWebsiteToken(token));
        request.Headers.TryAddWithoutValidation("X-BL", "en-US");
        request.Headers.TryAddWithoutValidation("Cookie", "accountToken=" + token);
        request.Headers.Referrer = new Uri("https://gofile.io/");
        request.Headers.TryAddWithoutValidation("Origin", "https://gofile.io");
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static IReadOnlyDictionary<string, string> BuildDownloadHeaders(string token, Uri? uri = null) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Authorization"] = "Bearer " + token,
            ["X-Website-Token"] = GenerateWebsiteToken(token),
            ["X-BL"] = "en-US",
            ["Referer"] = "https://gofile.io/",
            ["Origin"] = "https://gofile.io",
            ["Cookie"] = "accountToken=" + token
        };

    private static string GenerateWebsiteToken(string token)
    {
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 14400;
        var input = $"{UserAgent}::en-US::{token}::{window}::{WebsiteTokenSalt}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private async Task<Uri> ValidateByteRangeAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Gofile video yerine bir yetkilendirme sayfası döndürdü. Public klasör bağlantısını yeniden açıp dosyayı oradan seçin.");
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is null)
            throw new InvalidOperationException(
                $"Gofile dosyası ileri sarılabilir byte-range akışı vermedi (HTTP {(int)response.StatusCode}). Bağlantı süresi dolmuş olabilir.");
        return response.RequestMessage?.RequestUri ?? uri;
    }

    private static bool TryGetData(JsonElement root, out JsonElement data)
    {
        data = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            !status.GetString()!.Equals("ok", StringComparison.OrdinalIgnoreCase)) return false;
        return root.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<JsonElement> EnumerateChildren(JsonElement children)
    {
        if (children.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in children.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
        else if (children.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in children.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object) yield return property.Value;
        }
    }

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
        var value = GetLong(element, "createTime") ?? GetLong(element, "modTime");
        if (value is null) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(value.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool IsSafeContentId(string value) =>
        value.Length is >= 4 and <= 128 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static string BuildState(IReadOnlyList<string> ids) => "gofile:///" + string.Join('/', ids);

    private static IReadOnlyList<string> ParseState(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("gofile", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gofile klasör yolu geçerli değil.");
        return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidOperationException("Gofile yanıtı güvenli boyut sınırını aşıyor.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("Gofile yanıtı güvenli boyut sınırını aşıyor.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
