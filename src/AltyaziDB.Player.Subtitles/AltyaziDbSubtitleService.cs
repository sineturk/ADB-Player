using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Core.Parsing;

namespace AltyaziDB.Player.Subtitles;

public sealed class AltyaziDbSubtitleService : ISubtitleService
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(2)];
    private readonly HttpClient _client;
    private readonly IAppLogger _logger;

    public AltyaziDbSubtitleService(IAppLogger logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _client.Timeout = TimeSpan.FromSeconds(20);
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AltyaziDB-Player-Windows", "0.5.0"));
    }

    public async Task<AltyaziDbAccountStatus> GetAccountStatusAsync(SubtitleSearchOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var uri = BuildUri(options.ApiBaseUrl, "/me", new Dictionary<string, string?>());
        using var document = await GetJsonAsync(uri, options.ApiKey, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccess(root);
        var user = GetObject(root, "user");
        var limits = GetObject(root, "rate_limits");
        return new AltyaziDbAccountStatus(
            GetString(user, "username") ?? "Bilinmiyor",
            GetString(user, "role") ?? "Bilinmiyor",
            ParseRateWindow(GetObject(limits, "minute")),
            ParseRateWindow(GetObject(limits, "hour")),
            ParseRateWindow(GetObject(limits, "day")));
    }

    public async Task<SubtitleSearchResponse> SearchAsync(MediaIdentity identity, SubtitleSearchOptions options, CancellationToken cancellationToken = default)
    {
        if (!identity.CanSearch)
            return new SubtitleSearchResponse([], null, SubtitleSearchPagination.Empty, []);

        ValidateOptions(options);
        var query = new Dictionary<string, string?>
        {
            ["imdb_id"] = identity.ImdbId,
            ["tmdb_id"] = identity.TmdbId,
            ["title"] = string.IsNullOrWhiteSpace(identity.ImdbId) && string.IsNullOrWhiteSpace(identity.TmdbId) ? identity.Title : null,
            ["year"] = identity.Year?.ToString(CultureInfo.InvariantCulture),
            ["content_type"] = identity.ContentType,
            ["version_group"] = options.VersionGroup,
            ["season"] = identity.Season?.ToString(CultureInfo.InvariantCulture),
            ["episode"] = identity.Episode?.ToString(CultureInfo.InvariantCulture),
            ["lang"] = options.Language,
            ["page"] = Math.Max(1, options.Page).ToString(CultureInfo.InvariantCulture),
            ["limit"] = Math.Clamp(options.Limit, 1, 100).ToString(CultureInfo.InvariantCulture),
            ["sort"] = options.Sort is "downloads" ? "downloads" : "date"
        };

        var uri = BuildUri(options.ApiBaseUrl, "/search", query);
        using var document = await GetJsonAsync(uri, options.ApiKey, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccess(root);

        var results = new List<SubtitleSearchItem>();
        if (TryGetProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var json in data.EnumerateArray())
            {
                if (json.ValueKind != JsonValueKind.Object) continue;
                var parsed = ParseSubtitle(json, identity, options.DurationSeconds);
                if (parsed.Id > 0 && Uri.TryCreate(parsed.DownloadUrl, UriKind.Absolute, out _))
                    results.Add(parsed);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.VersionGroup))
        {
            results.RemoveAll(item =>
                !item.VersionGroups.Contains(options.VersionGroup, StringComparer.OrdinalIgnoreCase) &&
                !item.Versions.Any(version => version.Group.Equals(options.VersionGroup, StringComparison.OrdinalIgnoreCase)));
        }

        results.Sort((left, right) =>
        {
            var score = right.CompatibilityScore.CompareTo(left.CompatibilityScore);
            return score != 0 ? score : right.Downloads.CompareTo(left.Downloads);
        });

        var pagination = ParsePagination(root);
        var filters = GetObject(root, "filters");
        var versionGroups = GetStringList(filters, "version_groups");
        var candidate = ParseCandidate(GetObject(root, "movie"));
        var searchMethod = GetString(GetObject(root, "movie"), "search_method");
        return new SubtitleSearchResponse(results, candidate, pagination, versionGroups, searchMethod);
    }

    public async Task<byte[]> DownloadAsync(SubtitleSearchItem item, SubtitleSearchOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var selectedUrl = !string.IsNullOrWhiteSpace(item.DownloadUrl) ? item.DownloadUrl : item.StreamUrl;
        if (!Uri.TryCreate(selectedUrl, UriKind.Absolute, out var primary) || primary.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Altyazı yalnızca HTTPS üzerinden indirilebilir.");

        Exception? lastError = null;
        for (var round = 0; round <= RetryDelays.Length; round++)
        {
            foreach (var uri in UriCandidates(primary))
            {
                try
                {
                    using var request = CreateRequest(uri, options.ApiKey, "text/plain, text/vtt, application/x-subrip, */*");
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new ApiRequestException(await ReadApiErrorAsync(response, cancellationToken).ConfigureAwait(false), response.StatusCode);
                    return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRetryable(exception, cancellationToken))
                {
                    lastError = exception;
                }
            }
            if (round < RetryDelays.Length) await Task.Delay(RetryDelays[round], cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException(FriendlyNetworkMessage(lastError), lastError);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri primary, string apiKey, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var round = 0; round <= RetryDelays.Length; round++)
        {
            foreach (var uri in UriCandidates(primary))
            {
                try
                {
                    using var request = CreateRequest(uri, apiKey, "application/json");
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new ApiRequestException(await ReadApiErrorAsync(response, cancellationToken).ConfigureAwait(false), response.StatusCode);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRetryable(exception, cancellationToken))
                {
                    lastError = exception;
                    _logger.Warning($"AltyazıDB isteği yeniden denenecek: {exception.Message}");
                }
            }
            if (round < RetryDelays.Length) await Task.Delay(RetryDelays[round], cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException(FriendlyNetworkMessage(lastError), lastError);
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string apiKey, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        return request;
    }

    private static SubtitleSearchItem ParseSubtitle(JsonElement json, MediaIdentity identity, double videoDuration)
    {
        var releases = GetStringList(json, "releases");
        var versionGroups = GetStringList(json, "version_groups");
        var versions = GetVersions(json);
        var score = CompatibilityScore(json, identity, videoDuration, releases, versionGroups, versions);
        var streamUrl = GetString(json, "stream_url") ?? string.Empty;
        var downloadUrl = GetString(json, "download_url") ?? streamUrl;
        return new SubtitleSearchItem(
            GetLong(json, "id") ?? 0,
            GetString(json, "language") ?? "tr",
            GetInt(json, "season"),
            GetString(json, "episode"),
            GetString(json, "fps"),
            GetString(json, "duration") ?? GetString(json, "sure_bilgisi"),
            GetString(json, "translator") ?? "Bilinmiyor",
            GetString(json, "uploader") ?? "Bilinmiyor",
            streamUrl,
            downloadUrl,
            GetString(json, "archive_url") ?? string.Empty,
            GetString(json, "content_type"),
            GetInt(json, "downloads") ?? 0,
            GetInt(json, "is_package") == 1,
            GetInt(json, "hearing_impaired") == 1,
            GetInt(json, "forced") == 1,
            GetInt(json, "ai_ceviri") == 1,
            GetInt(json, "foreign_parts") == 1,
            releases,
            versionGroups,
            versions,
            GetString(json, "translator_note"),
            DateTimeOffset.TryParse(GetString(json, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null,
            score);
    }

    private static IReadOnlyList<SubtitleReleaseVersion> GetVersions(JsonElement json)
    {
        var result = new List<SubtitleReleaseVersion>();
        if (!TryGetProperty(json, "versions", out var values) || values.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var file = GetString(item, "file") ?? GetString(item, "dosya");
            if (string.IsNullOrWhiteSpace(file)) continue;
            result.Add(new SubtitleReleaseVersion(GetString(item, "group") ?? GetString(item, "grup") ?? "Genel", file));
        }
        return result;
    }

    private static double CompatibilityScore(JsonElement json, MediaIdentity identity, double videoDuration, IReadOnlyList<string> releases, IReadOnlyList<string> groups, IReadOnlyList<SubtitleReleaseVersion> versions)
    {
        var score = 0d;
        var rawEpisode = GetString(json, "episode")?.ToUpperInvariant();
        if (identity.Episode is not null)
        {
            if (rawEpisode == identity.Episode.Value.ToString(CultureInfo.InvariantCulture)) score += 35;
            if (rawEpisode == "PAKET") score += 12;
        }
        if (identity.Season is not null && GetInt(json, "season") == identity.Season) score += 12;
        var apiDuration = ParseDurationSeconds(GetString(json, "duration") ?? GetString(json, "sure_bilgisi"));
        if (apiDuration is not null && videoDuration > 0)
        {
            var delta = Math.Abs(apiDuration.Value - videoDuration);
            score += delta <= 1 ? 42 : delta <= 3 ? 34 : delta <= 10 ? 18 : delta <= 30 ? 5 : 0;
        }
        var releaseTokens = ReleaseParser.ReleaseTokens(identity.ReleaseName);
        var candidateTokens = releases
            .Concat(groups)
            .Concat(versions.SelectMany(value => new[] { value.Group, value.File }))
            .SelectMany(ReleaseParser.ReleaseTokens)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (releaseTokens.Count > 0 && candidateTokens.Count > 0)
            score += releaseTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count() * 7;
        if (GetInt(json, "forced") == 1) score -= 4;
        if (GetInt(json, "ai_ceviri") == 1) score -= 1;
        score += Math.Clamp(GetInt(json, "downloads") ?? 0, 0, 5000) / 1000d;
        return score;
    }

    private static SubtitleMediaCandidate? ParseCandidate(JsonElement movie)
    {
        if (movie.ValueKind != JsonValueKind.Object) return null;
        var title = GetString(movie, "title") ?? GetString(movie, "movie_title") ?? GetString(movie, "name");
        if (string.IsNullOrWhiteSpace(title)) return null;
        return new SubtitleMediaCandidate(
            title,
            GetString(movie, "content_type") ?? GetString(movie, "type") ?? "movie",
            GetInt(movie, "year"),
            GetString(movie, "imdb_id"),
            GetString(movie, "tmdb_id"),
            GetString(movie, "poster") ?? GetString(movie, "poster_url"),
            GetString(movie, "overview"));
    }

    private static SubtitleSearchPagination ParsePagination(JsonElement root)
    {
        var pagination = GetObject(root, "pagination");
        return new SubtitleSearchPagination(
            GetInt(pagination, "current_page") ?? 1,
            GetInt(pagination, "total_pages") ?? 1,
            GetInt(pagination, "total_records") ?? 0,
            GetInt(pagination, "limit") ?? 20);
    }

    private static ApiRateWindow ParseRateWindow(JsonElement value) =>
        new(GetInt(value, "limit") ?? 0, GetInt(value, "remaining") ?? 0);

    private static void EnsureSuccess(JsonElement root)
    {
        if (TryGetProperty(root, "success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException(GetString(root, "error") ?? "AltyazıDB API isteği başarısız oldu.");
    }

    private static void ValidateOptions(SubtitleSearchOptions options)
    {
        if (!Uri.TryCreate(options.ApiBaseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("AltyazıDB API adresi HTTPS olmalıdır.");
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("AltyazıDB API anahtarını girin.");
    }

    private static Uri BuildUri(string baseUrl, string endpoint, IReadOnlyDictionary<string, string?> query)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        var values = query.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        var suffix = string.Join("&", values);
        return new Uri(root + endpoint + (suffix.Length == 0 ? string.Empty : "?" + suffix));
    }

    private static IEnumerable<Uri> UriCandidates(Uri primary)
    {
        yield return primary;
        if (primary.Host.Equals("altyazidb.com", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(primary) { Host = "www.altyazidb.com" };
            yield return builder.Uri;
        }
        else if (primary.Host.Equals("www.altyazidb.com", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(primary) { Host = "altyazidb.com" };
            yield return builder.Uri;
        }
    }

    private static async Task<string> ReadApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            message = GetString(document.RootElement, "error") ?? GetString(document.RootElement, "message");
        }
        catch (JsonException) { }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => message ?? "API anahtarı geçersiz, pasif veya eksik (HTTP 401).",
            HttpStatusCode.Forbidden => message ?? "Bu API işlemi için yetkiniz yok (HTTP 403).",
            HttpStatusCode.TooManyRequests => message ?? "AltyazıDB hız sınırına ulaşıldı (HTTP 429). Bir süre sonra yeniden deneyin.",
            _ => message ?? $"AltyazıDB HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
        };
    }

    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException) return false;
        if (exception is ApiRequestException api)
            return api.StatusCode == HttpStatusCode.RequestTimeout || (int)api.StatusCode >= 500;
        return exception is HttpRequestException or IOException or JsonException;
    }

    private static string FriendlyNetworkMessage(Exception? exception) => exception switch
    {
        null => "AltyazıDB sunucusuna ulaşılamadı.",
        ApiRequestException api => api.Message,
        HttpRequestException => "AltyazıDB sunucusuna bağlanılamadı. İnternet, DNS ve güvenlik duvarı ayarlarını kontrol edin.",
        JsonException => "AltyazıDB geçersiz bir JSON yanıtı döndürdü.",
        _ => exception.Message
    };

    private static double? ParseDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        var parts = normalized.Split(':');
        if (parts.Length == 4) normalized = string.Join(':', parts.Take(3));
        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var time)) return time.TotalSeconds;
        return null;
    }

    private static JsonElement GetObject(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static IReadOnlyList<string> GetStringList(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
        if (!TryGetProperty(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    public void Dispose() => _client.Dispose();

    private sealed class ApiRequestException(string message, HttpStatusCode statusCode) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
