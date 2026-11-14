using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Core.Parsing;

namespace AltyaziDB.Player.App.Services;

/// <summary>
/// Conservative gate for artwork on the Home rail. A metadata service "matched"
/// response alone is not sufficient evidence for displaying somebody else's poster.
/// No API keys are held here; the app's existing authenticated metadata service is used.
/// </summary>
public static class ContinueWatchingArtworkPolicy
{
    public static bool IsTrusted(MediaIdentity requested, MediaMetadataResult result)
    {
        if (!result.Success || !result.Matched || result.Media is not { } media)
            return false;

        var isSeries = media.MediaType.Trim().ToLowerInvariant() is
            "tv" or "series" or "show" or "tv_series" or "tv_show" or "television";
        var isMovie = media.MediaType.Trim().ToLowerInvariant() is "movie" or "film";
        if (requested.IsSeries ? !isSeries : !isMovie)
            return false;

        if (requested.Year is { } year && media.Year is { } matchedYear
            && Math.Abs(year - matchedYear) > 1)
            return false;

        if (!string.IsNullOrWhiteSpace(requested.ImdbId)
            && !string.IsNullOrWhiteSpace(media.ImdbId)
            && !string.Equals(requested.ImdbId, media.ImdbId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(requested.TmdbId)
            && !string.IsNullOrWhiteSpace(media.TmdbId)
            && !string.Equals(requested.TmdbId, media.TmdbId, StringComparison.OrdinalIgnoreCase))
            return false;

        var exactId =
            (!string.IsNullOrWhiteSpace(requested.ImdbId)
             && string.Equals(requested.ImdbId, media.ImdbId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(requested.TmdbId)
                && string.Equals(requested.TmdbId, media.TmdbId, StringComparison.OrdinalIgnoreCase));

        var normalizedTitle = CloudMediaIdentity.NormalizeTitle(requested.Title);
        var exactTitle = !string.IsNullOrWhiteSpace(normalizedTitle)
            && (normalizedTitle == CloudMediaIdentity.NormalizeTitle(media.Title)
                || (!string.IsNullOrWhiteSpace(media.OriginalTitle)
                    && normalizedTitle == CloudMediaIdentity.NormalizeTitle(media.OriginalTitle)));

        // Explicit matching IDs can justify a localized title; otherwise require
        // both a strong score and an exact normalized title match.
        var confidence = result.Match?.Confidence ?? 0d;
        if (confidence > 1d && confidence <= 100d)
            confidence /= 100d;

        return exactId || (exactTitle && double.IsFinite(confidence) && confidence >= 0.86d);
    }

    public static string? SafeArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return uri.AbsoluteUri;
    }
}
