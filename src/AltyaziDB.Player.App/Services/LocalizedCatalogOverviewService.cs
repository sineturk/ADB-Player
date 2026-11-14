using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Addons.Models;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.Services;

/// <summary>
/// On-demand, session-local overview enrichment for the visible Discover item.
/// The existing authenticated Player Metadata API returns media.overview, but
/// does not advertise an explicit response language. Never label an English
/// overview "Turkish" or mutate the addon catalog with unverified text.
/// </summary>
public sealed class LocalizedCatalogOverviewService : IDisposable
{
    private readonly IMediaMetadataService _metadata;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private static readonly HashSet<string> TurkishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bir", "ve", "için", "ile", "olan", "olarak", "ancak", "sonra", "önce",
        "kendi", "hayat", "hayatı", "yaşam", "yaşamı", "arasında", "yıllar",
        "ailesi", "ailesinin", "karşı", "hakkında", "olduğu", "olduğunu",
        "göre", "sırada", "sırasında", "gibi", "çünkü", "başlar", "başlayan",
        "gün", "yeni", "daha", "çok", "artık", "birlikte", "kendini", "şehir",
        "köy", "dünya", "dünyada", "hikâye", "hikayesi", "mücadele", "macera",
        "ortaya", "bulur", "keşfeder", "çalışır", "yaşayan"
    };

    public LocalizedCatalogOverviewService(IMediaMetadataService metadata, IAppLogger logger)
    {
        _metadata = metadata;
        _logger = logger;
    }

    public async Task<string?> GetTurkishOverviewAsync(
        CatalogItem item,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(item.Title))
            return null;

        var cacheKey = BuildCacheKey(item);
        if (TryCached(cacheKey, out var cached))
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return null;
            if (TryCached(cacheKey, out cached))
                return cached;

            var identity = BuildMediaIdentity(item);
            var result = await _metadata.ResolveAsync(
                    identity,
                    clientId: null,
                    cancellationToken: cancellationToken,
                    includeDetail: true)
                .ConfigureAwait(false);

            var overview = SelectVerifiedTurkishOverview(item, result);
            if (_cache.Count >= 300)
                _cache.Clear();
            _cache[cacheKey] = new CacheEntry(
                overview,
                DateTimeOffset.UtcNow.Add(overview is null
                    ? TimeSpan.FromSeconds(45)
                    : TimeSpan.FromHours(12)));
            return overview;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.Warning($"Localized Discover metadata unavailable: {error.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Allow a newly authenticated account to retry earlier misses.</summary>
    public void RetryAfterLogin()
    {
        foreach (var entry in _cache)
            if (entry.Value.Overview is null)
                _cache.TryRemove(entry.Key, out _);
    }

    private bool TryCached(string key, out string? overview)
    {
        overview = null;
        if (!_cache.TryGetValue(key, out var value))
            return false;
        if (value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _cache.TryRemove(key, out _);
            return false;
        }
        overview = value.Overview;
        return true;
    }

    public static MediaIdentity BuildMediaIdentity(CatalogItem item)
    {
        var external = item.ExternalId.Trim();
        var imdb = Regex.IsMatch(external, @"^tt[0-9]{5,12}$", RegexOptions.IgnoreCase)
            ? external.ToLowerInvariant()
            : external.StartsWith("imdb:tt", StringComparison.OrdinalIgnoreCase)
              && Regex.IsMatch(external[5..], @"^tt[0-9]{5,12}$", RegexOptions.IgnoreCase)
                ? external[5..].ToLowerInvariant()
                : null;
        var tmdb = external.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(external[5..], out var numericId) && numericId > 0
            ? numericId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var mediaType = item.ContentType == CatalogContentType.Movie ? "movie" : "series";
        return new MediaIdentity(item.Title, mediaType, item.Key, item.Year,
            ImdbId: imdb, TmdbId: tmdb);
    }

    public static string? SelectVerifiedTurkishOverview(
        CatalogItem item, MediaMetadataResult result)
    {
        if (!result.Success || !result.Matched || result.Media is null)
            return null;
        var media = result.Media;
        if (item.Year is > 0 && media.Year is > 0 && item.Year != media.Year)
            return null;

        var expectedType = item.ContentType == CatalogContentType.Movie ? "movie" : "series";
        if (!string.IsNullOrWhiteSpace(media.MediaType) &&
            !media.MediaType.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
            !media.MediaType.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
            return null;

        var identity = BuildMediaIdentity(item);
        if (identity.ImdbId is not null &&
            !string.IsNullOrWhiteSpace(media.ImdbId) &&
            !identity.ImdbId.Equals(media.ImdbId, StringComparison.OrdinalIgnoreCase))
            return null;
        if (identity.TmdbId is not null &&
            !string.IsNullOrWhiteSpace(media.TmdbId) &&
            !identity.TmdbId.Equals(media.TmdbId, StringComparison.OrdinalIgnoreCase))
            return null;

        // Title-only matches need stronger confidence to avoid substituting
        // another title's synopsis. These are hints, not authoritative IDs.
        if (identity.ImdbId is null && identity.TmdbId is null &&
            (result.Match?.Confidence ?? 0) < 0.90)
            return null;

        var overview = media.Overview?.Trim();
        if (string.IsNullOrWhiteSpace(overview) || !LooksTurkish(overview))
            return null;
        return overview.Length <= 1800 ? overview : overview[..1800];
    }

    public static bool LooksTurkish(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 24)
            return false;
        var words = Regex.Matches(value, @"[\p{L}]+")
            .Select(match => match.Value)
            .ToArray();
        var hits = words.Count(word => TurkishWords.Contains(word));
        var turkishCharacters = value.Any(character =>
            "çğıöşüÇĞİÖŞÜ".Contains(character));
        return hits >= 3 || (hits >= 1 && turkishCharacters);
    }

    private static string BuildCacheKey(CatalogItem item)
    {
        var identity = BuildMediaIdentity(item);
        var id = identity.ImdbId ?? identity.TmdbId ?? item.Title.Trim().ToLowerInvariant();
        return $"{identity.ContentType}|{id}|{item.Year?.ToString() ?? "?"}|tr-TR";
    }

    public void Dispose()
    {
        _disposed = true;
        _cache.Clear();
        // Do not dispose the semaphore while an in-flight request may still be
        // unwinding on shutdown. This service does not own the metadata client.
    }

    private sealed record CacheEntry(string? Overview, DateTimeOffset ExpiresAtUtc);
}
