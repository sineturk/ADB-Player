using System.Text.RegularExpressions;

namespace AltyaziDB.Player.App.Services;

/// <summary>
/// Matches a playing series episode to tracks stored inside a season ZIP.
/// It never guesses an episode when the video name does not carry one.
/// </summary>
public static partial class TelegramEpisodeTrackMatcher
{
    [GeneratedRegex(@"(?<![A-Za-z0-9])S(?<season>\d{1,2})[\s._-]*E(?<episode>\d{1,3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?<!\d)(?<season>\d{1,2})[xX](?<episode>\d{1,3})(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex XEpisodeRegex();

    public static (int Season, int Episode)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var regex in new[] { SeasonEpisodeRegex(), XEpisodeRegex() })
        {
            var match = regex.Match(text);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["season"].Value, out var season) ||
                !int.TryParse(match.Groups["episode"].Value, out var episode) ||
                season < 1 || episode < 1) continue;
            return (season, episode);
        }
        return null;
    }

    public static string? Code(string? currentMedia)
    {
        var episode = Parse(currentMedia);
        return episode is null ? null : $"S{episode.Value.Season:00}E{episode.Value.Episode:00}";
    }

    public static IReadOnlyList<int> MatchingIndices(
        string? currentMedia, IReadOnlyList<string> names)
    {
        var playing = Parse(currentMedia);
        if (playing is null) return [];
        return names.Select((name, index) => (name, index))
            .Where(entry => Parse(entry.name) == playing)
            .Select(entry => entry.index)
            .ToArray();
    }
}
