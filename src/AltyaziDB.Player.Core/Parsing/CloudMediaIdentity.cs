using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Parsing;

public static class CloudMediaIdentity
{
    public static string CreateMediaKey(MediaIdentity identity, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var canonicalIdentity = !string.IsNullOrWhiteSpace(identity.ImdbId)
            ? $"imdb:{identity.ImdbId}"
            : !string.IsNullOrWhiteSpace(identity.TmdbId)
                ? $"tmdb:{identity.TmdbId}"
                : $"title:{NormalizeTitle(title)}|year:{identity.Year?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

        canonicalIdentity +=
            $"|type:{identity.ContentType}|season:{identity.Season?.ToString(CultureInfo.InvariantCulture) ?? "-"}|episode:{identity.Episode?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));
        return "v1:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string CreateMatchSignature(
        string title,
        string mediaType,
        int? year,
        int? season,
        int? episode)
    {
        return string.Join("|", new[]
        {
            NormalizeTitle(title),
            string.IsNullOrWhiteSpace(mediaType) ? "unknown" : mediaType.Trim().ToLowerInvariant(),
            year?.ToString(CultureInfo.InvariantCulture) ?? "-",
            season?.ToString(CultureInfo.InvariantCulture) ?? "-",
            episode?.ToString(CultureInfo.InvariantCulture) ?? "-"
        });
    }

    public static string NormalizeTitle(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        return Regex.Replace(normalized, @"\s+", " ");
    }
}
