using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Infrastructure;

public sealed class AltyaziDbPlayerMetadataService : IMediaMetadataService, IDisposable
{
    private static readonly Uri DefaultBaseUri = new("https://altyazidb.com/api/player/v1/");
    private readonly IPlayerApiAuthProvider _authProvider;
    private readonly IAppLogger _logger;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public AltyaziDbPlayerMetadataService(
        IPlayerApiAuthProvider authProvider,
        IAppLogger logger,
        Uri? baseUri = null,
        HttpMessageHandler? handler = null)
    {
        _authProvider = authProvider;
        _logger = logger;
        _baseUri = NormalizeHttpsBaseUri(baseUri ?? DefaultBaseUri);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(18);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ADB-Player/1.0");
    }

    public async Task<MediaMetadataResult> ResolveAsync(
        MediaIdentity identity,
        string? clientId = null,
        CancellationToken cancellationToken = default,
        bool includeDetail = false)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!identity.CanSearch)
            return MediaMetadataResult.Fail("Medya kimliği çözümlenemedi.", clientId);

        var auth = await _authProvider.GetPlayerApiAuthAsync(cancellationToken).ConfigureAwait(false);
        if (auth is null || !auth.IsValid)
            return MediaMetadataResult.Fail("ADB Cloud oturumu metadata servisi için doğrulanamadı.", clientId);

        try
        {
            var requestBody = BuildRequest(identity, clientId, includeDetail);
            using var response = await SendAsync(
                HttpMethod.Post,
                "metadata/resolve",
                requestBody,
                auth,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return MediaMetadataResult.Fail(
                    await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false),
                    clientId);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseResult(document.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _logger.Warning($"ADB metadata çözümleme başarısız: {exception.Message}");
            return MediaMetadataResult.Fail("ADB metadata servisine ulaşılamadı.", clientId);
        }
    }

    public async Task<IReadOnlyList<MediaMetadataResult>> ResolveBatchAsync(
        IReadOnlyList<MediaIdentity> identities,
        CancellationToken cancellationToken = default)
    {
        if (identities.Count == 0) return Array.Empty<MediaMetadataResult>();

        var selected = identities.Take(20).ToArray();
        var result = new MediaMetadataResult[selected.Length];
        var valid = new List<(int Index, MetadataRequestDto Request)>();

        for (var index = 0; index < selected.Length; index++)
        {
            var clientId = $"item-{index + 1}";
            if (!selected[index].CanSearch)
            {
                result[index] = MediaMetadataResult.Fail("Medya kimliği çözümlenemedi.", clientId);
                continue;
            }

            valid.Add((index, BuildRequest(selected[index], clientId, includeDetail: false)));
        }

        if (valid.Count == 0) return result;

        var auth = await _authProvider.GetPlayerApiAuthAsync(cancellationToken).ConfigureAwait(false);
        if (auth is null || !auth.IsValid)
        {
            foreach (var item in valid)
            {
                result[item.Index] = MediaMetadataResult.Fail(
                    "ADB Cloud oturumu metadata servisi için doğrulanamadı.",
                    item.Request.ClientId);
            }
            return result;
        }

        try
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                "metadata/batch",
                new BatchRequestDto(valid.Select(item => item.Request).ToArray()),
                auth,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                foreach (var item in valid)
                    result[item.Index] = MediaMetadataResult.Fail(error, item.Request.ClientId);
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!TryGet(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                foreach (var item in valid)
                    result[item.Index] = MediaMetadataResult.Fail("ADB metadata batch yanıtı okunamadı.", item.Request.ClientId);
                return result;
            }

            var byClientId = data.EnumerateArray()
                .Select(ParseResult)
                .Where(item => !string.IsNullOrWhiteSpace(item.ClientId))
                .ToDictionary(item => item.ClientId!, StringComparer.OrdinalIgnoreCase);

            foreach (var item in valid)
            {
                result[item.Index] = byClientId.TryGetValue(item.Request.ClientId ?? string.Empty, out var resolved)
                    ? resolved
                    : MediaMetadataResult.Fail("ADB metadata batch yanıtında öğe bulunamadı.", item.Request.ClientId);
            }

            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _logger.Warning($"ADB metadata toplu çözümleme başarısız: {exception.Message}");
            foreach (var item in valid)
            {
                result[item.Index] = MediaMetadataResult.Fail(
                    "ADB metadata servisine ulaşılamadı.",
                    item.Request.ClientId);
            }
            return result;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        object body,
        PlayerApiAuthContext auth,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation("X-ADB-Device-Key", auth.DeviceKey);
        request.Headers.TryAddWithoutValidation(
            "X-ADB-Player-Version",
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0.0");
        request.Content = JsonContent.Create(body, options: _json);

        try
        {
            return await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static MetadataRequestDto BuildRequest(
        MediaIdentity identity,
        string? clientId,
        bool includeDetail) =>
        new(
            clientId,
            identity.Title,
            identity.Year,
            identity.ContentType,
            identity.Season,
            identity.Episode,
            identity.ImdbId,
            int.TryParse(identity.TmdbId, out var tmdbId) && tmdbId > 0 ? tmdbId : null,
            includeDetail);

    private static MediaMetadataResult ParseResult(JsonElement root)
    {
        var success = GetBool(root, "success") ?? false;
        var matched = GetBool(root, "matched") ?? false;
        var clientId = GetString(root, "client_id") ?? GetString(root, "clientId");
        var error = GetString(root, "error");

        MediaMetadataMatch? match = null;
        if (TryGet(root, "match", out var matchElement) && matchElement.ValueKind == JsonValueKind.Object)
        {
            match = new MediaMetadataMatch(
                GetString(matchElement, "source") ?? "none",
                GetString(matchElement, "method") ?? "none",
                GetDouble(matchElement, "confidence") ?? 0);
        }

        ResolvedMediaMetadata? media = null;
        if (TryGet(root, "media", out var mediaElement) && mediaElement.ValueKind == JsonValueKind.Object)
        {
            var title = GetString(mediaElement, "title");
            if (!string.IsNullOrWhiteSpace(title))
            {
                media = new ResolvedMediaMetadata(
                    GetLong(mediaElement, "adb_id") ?? GetLong(mediaElement, "adbId"),
                    GetString(mediaElement, "type") ?? "unknown",
                    title,
                    GetString(mediaElement, "original_title") ?? GetString(mediaElement, "originalTitle"),
                    GetInt(mediaElement, "year"),
                    GetString(mediaElement, "imdb_id") ?? GetString(mediaElement, "imdbId"),
                    GetString(mediaElement, "tmdb_id") ?? GetString(mediaElement, "tmdbId"),
                    GetString(mediaElement, "poster") ?? GetString(mediaElement, "poster_url"),
                    GetString(mediaElement, "backdrop") ?? GetString(mediaElement, "backdrop_url"),
                    GetString(mediaElement, "overview"));
            }
        }

        var subtitles = new MediaSubtitleAvailability(false, 0, 0, 0);
        if (TryGet(root, "subtitles", out var subtitleElement) && subtitleElement.ValueKind == JsonValueKind.Object)
        {
            subtitles = new MediaSubtitleAvailability(
                GetBool(subtitleElement, "available") ?? false,
                GetInt(subtitleElement, "total_count") ?? GetInt(subtitleElement, "totalCount") ?? 0,
                GetInt(subtitleElement, "tr_count")
                    ?? GetInt(subtitleElement, "turkish_count")
                    ?? GetInt(subtitleElement, "turkishCount")
                    ?? 0,
                GetInt(subtitleElement, "en_count")
                    ?? GetInt(subtitleElement, "english_count")
                    ?? GetInt(subtitleElement, "englishCount")
                    ?? 0);
        }

        var cacheHit = false;
        if (TryGet(root, "cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            cacheHit = GetBool(cache, "hit") ?? false;

        return new MediaMetadataResult(
            success,
            matched,
            clientId,
            match,
            media,
            subtitles,
            cacheHit,
            error);
    }

    private static Uri NormalizeHttpsBaseUri(Uri baseUri)
    {
        if (!baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ADB Player metadata endpoint HTTPS olmalıdır.");

        var value = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri.AbsoluteUri
            : baseUri.AbsoluteUri + "/";
        return new Uri(value, UriKind.Absolute);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var error = GetString(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                return error switch
                {
                    "player_auth_required" or "invalid_player_session" =>
                        "ADB Cloud oturumu geçersiz veya süresi dolmuş.",
                    "player_auth_unavailable" =>
                        "ADB Player kimlik doğrulama servisi şu anda kullanılamıyor.",
                    "rate_limit_exceeded" =>
                        "ADB metadata servisinin kısa süreli hız sınırına ulaşıldı.",
                    _ => error
                };
            }
        }
        catch (JsonException)
        {
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "ADB Cloud oturumu metadata servisi için doğrulanamadı.",
            HttpStatusCode.TooManyRequests => "ADB metadata servisinin hız sınırına ulaşıldı.",
            _ => $"ADB metadata servisi HTTP {(int)response.StatusCode} döndürdü."
        };
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
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
        if (!TryGet(element, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(value.ToString(), out var result) ? result : null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), out number) ? number : null;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return double.TryParse(
            value.ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    public void Dispose() => _http.Dispose();

    private sealed record MetadataRequestDto(
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("year")] int? Year,
        [property: JsonPropertyName("media_type")] string MediaType,
        [property: JsonPropertyName("season")] int? Season,
        [property: JsonPropertyName("episode")] int? Episode,
        [property: JsonPropertyName("imdb_id")] string? ImdbId,
        [property: JsonPropertyName("tmdb_id")] int? TmdbId,
        [property: JsonPropertyName("include_detail")] bool IncludeDetail);

    private sealed record BatchRequestDto(
        [property: JsonPropertyName("items")] IReadOnlyList<MetadataRequestDto> Items);
}
