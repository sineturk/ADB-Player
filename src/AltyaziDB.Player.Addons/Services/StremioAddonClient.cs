using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Addons.Models;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.Addons.Services;

internal sealed record AddonResourceRule(
    bool IsSupported,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> IdPrefixes)
{
    public bool SupportsRequest(string type, string id)
    {
        if (!IsSupported)
        {
            return false;
        }

        var normalizedType = type.Trim().ToLowerInvariant() switch
        {
            "tv" => "series",
            _ => type.Trim().ToLowerInvariant()
        };
        if (Types.Count > 0 && !Types.Contains(normalizedType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return IdPrefixes.Count == 0 ||
               IdPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record AddonManifestDocument(
    AddonRegistration Registration,
    IReadOnlyList<AddonCatalogDescriptor> Catalogs,
    AddonResourceRule StreamResource,
    AddonResourceRule MetadataResource);

internal sealed partial class StremioAddonClient : IDisposable
{
    private const int MaximumJsonBytes = 4 * 1024 * 1024;
    private const int MaximumTorrentBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IAppLogger _logger;

    public StremioAddonClient(IAppLogger logger)
    {
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AltyaziDB-Player/0.10.4 AddonClient");
    }

    public async Task<AddonManifestDocument> ReadManifestAsync(
        string manifestUrl,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        var manifestUri = NormalizeManifestUri(manifestUrl, allowLocalHttp);
        using var document = await GetJsonAsync(manifestUri, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var id = GetString(root, "id");
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("Eklenti manifestinde id ve name alanları bulunmalıdır.");
        }

        var version = GetString(root, "version") ?? "0.0.0";
        var description = GetString(root, "description");
        var streamResource = ParseResourceRule(root, "stream");
        var metadataResource = ParseResourceRule(root, "meta");
        var catalogs = ParseCatalogs(root, id, name);
        if (catalogs.Count == 0 && !streamResource.IsSupported && !metadataResource.IsSupported)
        {
            throw new InvalidDataException(
                "Eklenti desteklenen bir catalog, meta veya stream kaynağı yayınlamıyor.");
        }

        var registration = new AddonRegistration(
            id.Trim(),
            name.Trim(),
            manifestUri.AbsoluteUri,
            version.Trim(),
            true,
            DateTimeOffset.UtcNow,
            description?.Trim(),
            catalogs.Count > 0,
            streamResource.IsSupported,
            metadataResource.IsSupported);

        return new AddonManifestDocument(
            registration,
            catalogs,
            streamResource,
            metadataResource);
    }

    public async Task<IReadOnlyList<CatalogItem>> GetCatalogAsync(
        AddonRegistration registration,
        AddonCatalogDescriptor catalog,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        var manifestUri = new Uri(registration.ManifestUrl, UriKind.Absolute);
        var baseUri = new Uri(manifestUri, ".");
        var endpoint = new Uri(
            baseUri,
            $"catalog/{Uri.EscapeDataString(catalog.Type)}/{Uri.EscapeDataString(catalog.Id)}.json");

        using var document = await GetJsonAsync(endpoint, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CatalogItem>();
        }

        var items = new List<CatalogItem>();
        var position = 0;
        foreach (var meta in metas.EnumerateArray())
        {
            if (meta.ValueKind != JsonValueKind.Object || position++ >= 80)
            {
                continue;
            }

            var externalId = GetString(meta, "id");
            var title = GetString(meta, "name") ?? GetString(meta, "title");
            var sourceType = GetString(meta, "type") ?? catalog.Type;
            if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var genres = GetStringArray(meta, "genres");
            var contentType = ClassifyContent(sourceType, catalog, genres);
            var year = GetInteger(meta, "year") ?? ParseYear(GetString(meta, "releaseInfo"));
            var released = ParseDate(GetString(meta, "released"));
            var key = BuildItemKey(contentType, externalId, title, year);

            items.Add(new CatalogItem(
                key,
                externalId.Trim(),
                NormalizeSourceType(sourceType, contentType),
                contentType,
                title.Trim(),
                year,
                NormalizeHttpUrl(GetString(meta, "poster"), allowLocalHttp),
                NormalizeHttpUrl(GetString(meta, "background"), allowLocalHttp),
                GetString(meta, "description")?.Trim(),
                GetString(meta, "imdbRating")?.Trim(),
                released,
                genres,
                registration.Id,
                registration.Name,
                catalog.Id));
        }

        return items;
    }

    public async Task<IReadOnlyList<CatalogEpisode>> GetEpisodesAsync(
        AddonRegistration registration,
        CatalogItem item,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        var manifestUri = new Uri(registration.ManifestUrl, UriKind.Absolute);
        var baseUri = new Uri(manifestUri, ".");
        var endpoint = new Uri(
            baseUri,
            $"meta/{Uri.EscapeDataString(item.SourceType)}/{Uri.EscapeDataString(item.ExternalId)}.json");

        using var document = await GetJsonAsync(endpoint, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CatalogEpisode>();
        }

        var results = new List<CatalogEpisode>();
        foreach (var video in videos.EnumerateArray().Take(500))
        {
            if (video.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(video, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var season = GetInteger(video, "season");
            var episode = GetInteger(video, "episode");
            var title = GetString(video, "title")
                        ?? GetString(video, "name")
                        ?? (season is not null && episode is not null ? $"S{season:00}E{episode:00}" : id);
            var released = ParseDate(GetString(video, "released"));
            var thumbnail = NormalizeHttpUrl(GetString(video, "thumbnail"), allowLocalHttp);
            var description = GetString(video, "overview") ?? GetString(video, "description");

            results.Add(new CatalogEpisode(
                id.Trim(),
                title.Trim(),
                season,
                episode,
                released,
                thumbnail,
                description?.Trim()));
        }

        return results
            .GroupBy(video => video.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(video => video.ReleasedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(video => video.Season ?? 0)
            .ThenByDescending(video => video.Episode ?? 0)
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogStream>> GetStreamsAsync(
        AddonRegistration registration,
        CatalogItem item,
        CatalogEpisode? episode,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        var manifestUri = new Uri(registration.ManifestUrl, UriKind.Absolute);
        var baseUri = new Uri(manifestUri, ".");
        var streamId = episode?.ExternalId ?? item.ExternalId;
        var endpoint = new Uri(
            baseUri,
            $"stream/{Uri.EscapeDataString(item.SourceType)}/{Uri.EscapeDataString(streamId)}.json");

        using var document = await GetJsonAsync(endpoint, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CatalogStream>();
        }

        var results = new List<CatalogStream>();
        var position = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object || position++ >= 100)
            {
                continue;
            }

            var name = GetString(stream, "name") ?? registration.Name;
            var title = GetString(stream, "title") ?? name;
            var url = GetString(stream, "url");
            var infoHash = NormalizeInfoHash(GetString(stream, "infoHash"));
            var fileIndex = GetInteger(stream, "fileIdx");
            var fileName = TryGetNestedString(stream, "behaviorHints", "filename");

            var kind = DetermineStreamKind(url, infoHash);
            if (kind is null)
            {
                continue;
            }

            url = NormalizeStreamUrl(url, allowLocalHttp);
            if (url is null && infoHash is null)
            {
                continue;
            }

            var combined = $"{name} {title} {fileName}";
            var quality = ParseQuality(combined);
            var seeders = ParseSeeders(combined);
            var size = ParseSize(combined);
            var key = infoHash is not null
                ? $"hash:{infoHash.ToLowerInvariant()}:{fileIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}"
                : $"url:{url}";

            results.Add(new CatalogStream(
                key,
                registration.Id,
                registration.Name,
                kind.Value,
                name.Trim(),
                title.Trim(),
                url,
                infoHash,
                fileIndex,
                fileName,
                quality,
                seeders,
                size));
        }

        return results;
    }

    public async Task<string> DownloadTorrentAsync(
        CatalogStream stream,
        string targetDirectory,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        if (stream.Kind != CatalogStreamKind.Torrent || string.IsNullOrWhiteSpace(stream.Url))
        {
            throw new InvalidOperationException("Akış indirilebilir bir .torrent bağlantısı içermiyor.");
        }

        var uri = new Uri(stream.Url, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Torrent dosyası yalnız HTTP veya HTTPS üzerinden indirilebilir.");
        }

        Directory.CreateDirectory(targetDirectory);
        using var response = await GetResponseAsync(uri, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumTorrentBytes)
        {
            throw new InvalidDataException("Torrent dosyası izin verilen boyutu aşıyor.");
        }

        var safeName = MakeSafeFileName(stream.FileName ?? stream.Name) + ".torrent";
        var path = Path.Combine(targetDirectory, $"addon-{Guid.NewGuid():N}-{safeName}");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, true);
        await CopyWithLimitAsync(input, output, MaximumTorrentBytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private async Task<HttpResponseMessage> GetResponseAsync(
        Uri uri,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        var original = uri;
        var current = uri;
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            ValidateRemoteUri(current, allowLocalHttp);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new HttpRequestException("Eklenti yönlendirmesi hedef adres içermiyor.");
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                ValidateRemoteUri(next, allowLocalHttp);
                if (!next.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase) &&
                    IsLocalNetworkHost(next) &&
                    !IsLocalNetworkHost(original))
                {
                    throw new HttpRequestException(
                        "Uzak eklenti yerel ağ adresine yönlendirme yaptı; güvenlik nedeniyle istek durduruldu.");
                }

                current = next;
                continue;
            }

            return response;
        }

        throw new HttpRequestException("Eklenti çok fazla yönlendirme döndürdü.");
    }

    private async Task<JsonDocument> GetJsonAsync(
        Uri uri,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        ValidateRemoteUri(uri, allowLocalHttp);
        using var response = await GetResponseAsync(uri, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumJsonBytes)
        {
            throw new InvalidDataException("Eklenti yanıtı izin verilen boyutu aşıyor.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new MemoryStream();
        await CopyWithLimitAsync(stream, bounded, MaximumJsonBytes, cancellationToken).ConfigureAwait(false);
        bounded.Position = 0;
        return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static Uri NormalizeManifestUri(string value, bool allowLocalHttp)
    {
        value = value?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("Geçerli bir eklenti adresi girin.");
        }

        ValidateRemoteUri(uri, allowLocalHttp);
        if (!uri.AbsolutePath.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Path = uri.AbsolutePath.TrimEnd('/') + "/manifest.json",
                Fragment = string.Empty
            };
            uri = builder.Uri;
        }

        return uri;
    }

    private static void ValidateRemoteUri(Uri uri, bool allowLocalHttp)
    {
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new InvalidDataException("Kullanıcı adı veya parola eklenti adresinde bulunamaz.");
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            allowLocalHttp &&
            IsLocalNetworkHost(uri))
        {
            return;
        }

        throw new InvalidDataException(
            "Uzak eklentiler HTTPS kullanmalıdır. Yerel ağ veya localhost HTTP erişimi ayrıca onaylanmalıdır.");
    }

    private static bool IsLocalNetworkHost(Uri uri)
    {
        if (uri.IsLoopback ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Contains('.'))
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    private static IReadOnlyList<AddonCatalogDescriptor> ParseCatalogs(
        JsonElement root,
        string addonId,
        string addonName)
    {
        if (!root.TryGetProperty("catalogs", out var catalogs) || catalogs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AddonCatalogDescriptor>();
        }

        var results = new List<AddonCatalogDescriptor>();
        foreach (var catalog in catalogs.EnumerateArray())
        {
            if (catalog.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetString(catalog, "type")?.Trim().ToLowerInvariant();
            var id = GetString(catalog, "id")?.Trim();
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id) || !IsSupportedType(type))
            {
                continue;
            }

            var name = GetString(catalog, "name")?.Trim() ?? id;
            var genres = GetStringArray(catalog, "genres");
            var supportsSkip = false;
            if (catalog.TryGetProperty("extra", out var extras) && extras.ValueKind == JsonValueKind.Array)
            {
                supportsSkip = extras.EnumerateArray().Any(extra =>
                    extra.ValueKind == JsonValueKind.Object &&
                    string.Equals(GetString(extra, "name"), "skip", StringComparison.OrdinalIgnoreCase));
            }

            results.Add(new AddonCatalogDescriptor(
                addonId,
                addonName,
                type,
                id,
                name,
                genres,
                supportsSkip));
        }

        return results;
    }

    private static AddonResourceRule ParseResourceRule(JsonElement root, string resourceName)
    {
        if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            return new AddonResourceRule(false, Array.Empty<string>(), Array.Empty<string>());
        }

        var isSupported = false;
        var types = new List<string>();
        var idPrefixes = new List<string>();
        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind == JsonValueKind.String &&
                string.Equals(resource.GetString(), resourceName, StringComparison.OrdinalIgnoreCase))
            {
                isSupported = true;
                continue;
            }

            if (resource.ValueKind != JsonValueKind.Object ||
                !string.Equals(GetString(resource, "name"), resourceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            isSupported = true;
            types.AddRange(GetStringArray(resource, "types"));
            idPrefixes.AddRange(GetStringArray(resource, "idPrefixes"));
        }

        if (!isSupported)
        {
            return new AddonResourceRule(false, Array.Empty<string>(), Array.Empty<string>());
        }

        if (types.Count == 0)
        {
            types.AddRange(GetStringArray(root, "types"));
        }

        if (idPrefixes.Count == 0)
        {
            idPrefixes.AddRange(GetStringArray(root, "idPrefixes"));
        }

        var normalizedTypes = types
            .Select(type => type.Trim().ToLowerInvariant() switch
            {
                "tv" => "series",
                var value => value
            })
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedPrefixes = idPrefixes
            .Select(prefix => prefix.Trim())
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AddonResourceRule(true, normalizedTypes, normalizedPrefixes);
    }

    private static CatalogContentType ClassifyContent(
        string sourceType,
        AddonCatalogDescriptor catalog,
        IReadOnlyList<string> genres)
    {
        var combined = string.Join(' ', new[] { sourceType, catalog.Id, catalog.Name }
            .Concat(catalog.Genres)
            .Concat(genres));
        if (AnimeTokenRegex().IsMatch(combined))
        {
            return CatalogContentType.Anime;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "movie" => CatalogContentType.Movie,
            "series" or "tv" => CatalogContentType.Series,
            "anime" => CatalogContentType.Anime,
            _ => CatalogContentType.Series
        };
    }

    private static string NormalizeSourceType(string sourceType, CatalogContentType contentType)
    {
        var normalized = sourceType.Trim().ToLowerInvariant();
        if (normalized is "movie" or "series" or "anime")
        {
            return normalized;
        }

        return contentType == CatalogContentType.Movie ? "movie" : "series";
    }

    private static bool IsSupportedType(string type) =>
        type is "movie" or "series" or "tv" or "anime";

    private static CatalogStreamKind? DetermineStreamKind(string? url, string? infoHash)
    {
        if (!string.IsNullOrWhiteSpace(infoHash))
        {
            return CatalogStreamKind.Torrent;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogStreamKind.Torrent;
        }

        if (uri.Scheme is "http" or "https")
        {
            var path = uri.AbsolutePath;
            return path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
                ? CatalogStreamKind.Torrent
                : CatalogStreamKind.Direct;
        }

        return CatalogStreamKind.External;
    }

    private static string? NormalizeStreamUrl(string? value, bool allowLocalHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsoluteUri;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        ValidateRemoteUri(uri, allowLocalHttp);
        return uri.AbsoluteUri;
    }

    private static string? NormalizeHttpUrl(string? value, bool allowLocalHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            ValidateRemoteUri(uri, allowLocalHttp);
            return uri.AbsoluteUri;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? NormalizeInfoHash(string? value)
    {
        value = value?.Trim();
        return !string.IsNullOrWhiteSpace(value) && InfoHashRegex().IsMatch(value)
            ? value
            : null;
    }

    private static string BuildItemKey(CatalogContentType type, string id, string title, int? year)
    {
        if (ImdbIdRegex().IsMatch(id))
        {
            return $"imdb:{id.ToLowerInvariant()}";
        }

        var normalizedTitle = NonAlphaNumericRegex().Replace(title.ToLowerInvariant(), string.Empty);
        return $"{type}:{normalizedTitle}:{year?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
    }

    private static int? ParseYear(string? value)
    {
        var match = YearRegex().Match(value ?? string.Empty);
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string? ParseQuality(string value)
    {
        var match = QualityRegex().Match(value);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static int? ParseSeeders(string value)
    {
        var match = SeedersRegex().Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out var seeders) ? seeders : null;
    }

    private static long? ParseSize(string value)
    {
        var match = SizeRegex().Match(value);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        };
        var bytes = amount * multiplier;
        return bytes is > 0 and <= long.MaxValue ? (long)bytes : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static int? GetInteger(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number)
            ? number
            : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? TryGetNestedString(JsonElement element, string objectName, string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : null;
    }

    private static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Eklenti yanıtı izin verilen boyutu aşıyor.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "catalog-stream" : safe[..Math.Min(safe.Length, 80)];
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"(?:^|[^a-z])(anime|anim[eé]|japanese animation)(?:[^a-z]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AnimeTokenRegex();

    [GeneratedRegex(@"^[a-f0-9]{40}$|^[a-z2-7]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex InfoHashRegex();

    [GeneratedRegex(@"^tt\d{7,10}$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b(?:2160p|1080p|720p|480p|4k|uhd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"(?:seed(?:er)?s?|👤|🌱)\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeedersRegex();

    [GeneratedRegex(@"(\d+(?:[\.,]\d+)?)\s*(KB|MB|GB|TB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();
}
