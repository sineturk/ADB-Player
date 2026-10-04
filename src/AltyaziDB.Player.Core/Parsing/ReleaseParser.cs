using System.Text.RegularExpressions;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Parsing;

public sealed partial class ReleaseParser
{
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "1080p", "720p", "2160p", "480p", "4k", "uhd", "hdr", "hdr10", "dv",
        "dolby", "vision", "web", "webdl", "webrip", "web-dl", "bluray", "bdrip",
        "remux", "hdtv", "x264", "x265", "h264", "h265", "hevc", "av1", "aac",
        "ac3", "eac3", "ddp", "ddp5", "ddp5.1", "atmos", "proper", "repack",
        "internal", "multi", "dual", "audio", "tr", "eng", "english", "turkish",
        "nf", "amzn", "dsnp", "hmax", "atvp", "hotstar"
    };

    public MediaIdentity Parse(string fileName, string? caption = null)
    {
        fileName ??= string.Empty;
        var metadataCaption = NormalizeMetadataCaption(caption ?? string.Empty);
        var source = !string.IsNullOrWhiteSpace(fileName) ? fileName.Trim() : metadataCaption;
        var combined = string.IsNullOrWhiteSpace(metadataCaption) || metadataCaption == source
            ? source
            : source + "\n" + metadataCaption;

        var imdb = ImdbRegex().Match(combined) is { Success: true } imdbMatch
            ? imdbMatch.Value.ToLowerInvariant()
            : null;
        var tmdbMatch = TmdbRegex().Match(combined);
        var tmdb = tmdbMatch.Success ? tmdbMatch.Groups[1].Value : null;

        int? season = null;
        int? episode = null;
        var episodeMatch = EpisodeRegex().Match(source);
        if (!episodeMatch.Success && metadataCaption != source) episodeMatch = EpisodeRegex().Match(metadataCaption);
        Match? alternateMatch = null;
        if (!episodeMatch.Success)
        {
            alternateMatch = AlternateEpisodeRegex().Match(source);
            if (!alternateMatch.Success && metadataCaption != source)
                alternateMatch = AlternateEpisodeRegex().Match(metadataCaption);
        }

        if (episodeMatch.Success)
        {
            season = ParseInt(episodeMatch.Groups[1].Value);
            episode = ParseInt(episodeMatch.Groups[2].Value);
        }
        else if (alternateMatch is { Success: true })
        {
            season = ParseInt(alternateMatch.Groups[1].Value);
            episode = ParseInt(alternateMatch.Groups[2].Value);
        }
        else
        {
            var captionEpisode = CaptionEpisodeRegex().Match(metadataCaption);
            var captionSeason = CaptionSeasonRegex().Match(metadataCaption);
            episode = captionEpisode.Success ? ParseInt(captionEpisode.Groups[1].Value) : null;
            season = captionSeason.Success ? ParseInt(captionSeason.Groups[1].Value) : null;
        }

        var yearMatch = YearRegex().Match(combined);
        var year = yearMatch.Success ? ParseInt(yearMatch.Groups[1].Value) : null;
        var title = ExtractTitle(source);
        var contentType = season is not null || episode is not null ? "series" : "movie";

        return new MediaIdentity(
            title,
            contentType,
            string.IsNullOrWhiteSpace(fileName) ? source : fileName.Trim(),
            year,
            season,
            episode,
            imdb,
            tmdb);
    }

    public static HashSet<string> ReleaseTokens(string value) =>
        NonAlphaNumericRegex()
            .Split(ExtensionRegex().Replace(value.ToLowerInvariant(), string.Empty))
            .Where(token => token.Length >= 3 && !NoiseTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeMetadataCaption(string caption)
    {
        var firstLine = caption.Trim().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(firstLine)) return string.Empty;
        if (Uri.TryCreate(firstLine, UriKind.Absolute, out var uri) &&
            uri.Scheme is "magnet" or "http" or "https" or "file") return string.Empty;
        if (AbsolutePathRegex().IsMatch(firstLine)) return string.Empty;
        return firstLine;
    }

    private static string ExtractTitle(string source)
    {
        var value = ExtensionRegex().Replace(source, string.Empty);
        value = UserTagRegex().Replace(value, " ");
        value = BracketRegex().Replace(value, " ");
        value = ParenthesisRegex().Replace(value, " ");
        value = NamedEpisodeRegex().Replace(value, " ");
        value = EpisodeTailRegex().Replace(value, " ");
        value = AlternateEpisodeTailRegex().Replace(value, " ");
        value = YearTailRegex().Replace(value, " ");
        value = value.Replace('.', ' ').Replace('_', ' ');
        value = DashRegex().Replace(value, " ");

        var kept = new List<string>();
        foreach (var token in WhitespaceRegex().Split(value).Where(token => !string.IsNullOrWhiteSpace(token)))
        {
            if (NoiseTokens.Contains(token) || CodecTokenRegex().IsMatch(token)) break;
            kept.Add(token);
        }

        var title = string.Join(" ", kept).Trim();
        return string.IsNullOrWhiteSpace(title)
            ? System.IO.Path.GetFileNameWithoutExtension(source).Replace('.', ' ').Replace('_', ' ').Trim()
            : title;
    }

    private static int? ParseInt(string value) => int.TryParse(value, out var parsed) ? parsed : null;

    [GeneratedRegex(@"\btt\d{7,10}\b", RegexOptions.IgnoreCase)] private static partial Regex ImdbRegex();
    [GeneratedRegex(@"\btmdb(?:[_\s:#-]*id)?[_\s:#-]*(\d{2,10})\b", RegexOptions.IgnoreCase)] private static partial Regex TmdbRegex();
    [GeneratedRegex(@"\bS(\d{1,2})[ ._-]*E(\d{1,3})\b", RegexOptions.IgnoreCase)] private static partial Regex EpisodeRegex();
    [GeneratedRegex(@"\b(\d{1,2})x(\d{1,3})\b", RegexOptions.IgnoreCase)] private static partial Regex AlternateEpisodeRegex();
    [GeneratedRegex(@"(?:^|\s)E(?:P)?[ ._-]?(\d{1,3})(?:\s|$)", RegexOptions.IgnoreCase)] private static partial Regex CaptionEpisodeRegex();
    [GeneratedRegex(@"(?:^|\s)S(?:EASON)?[ ._-]?(\d{1,2})(?:\s|$)", RegexOptions.IgnoreCase)] private static partial Regex CaptionSeasonRegex();
    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)")] private static partial Regex YearRegex();
    [GeneratedRegex(@"\.[a-z0-9]{2,5}$", RegexOptions.IgnoreCase)] private static partial Regex ExtensionRegex();
    [GeneratedRegex(@"@[a-zA-Z0-9_]{3,}")] private static partial Regex UserTagRegex();
    [GeneratedRegex(@"\[[^\]]+\]")] private static partial Regex BracketRegex();
    [GeneratedRegex(@"\([^\)]*\)")] private static partial Regex ParenthesisRegex();
    [GeneratedRegex(@"(^|\s)(?:episode|ep|e|bölüm|bolum)[ ._-]*\d{1,3}(?=\s|$)", RegexOptions.IgnoreCase)] private static partial Regex NamedEpisodeRegex();
    [GeneratedRegex(@"\bS\d{1,2}[ ._-]*E\d{1,3}\b.*$", RegexOptions.IgnoreCase)] private static partial Regex EpisodeTailRegex();
    [GeneratedRegex(@"\b\d{1,2}x\d{1,3}\b.*$", RegexOptions.IgnoreCase)] private static partial Regex AlternateEpisodeTailRegex();
    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d).*$")] private static partial Regex YearTailRegex();
    [GeneratedRegex(@"[-]+")] private static partial Regex DashRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"^(?:x26[45]|h26[45]|ddp?\d?|aac\d?|\d{3,4}p)$", RegexOptions.IgnoreCase)] private static partial Regex CodecTokenRegex();
    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)] private static partial Regex NonAlphaNumericRegex();
    [GeneratedRegex(@"^(?:[a-zA-Z]:[\\/]|/|\\\\)")] private static partial Regex AbsolutePathRegex();
}
