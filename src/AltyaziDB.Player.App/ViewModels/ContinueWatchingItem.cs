using AltyaziDB.Player.Core.Parsing;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.ViewModels;

public sealed record ContinueWatchingItem(
    string Source,
    string Title,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset UpdatedAtUtc,
    bool IsLocal,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    string? SeriesTitle = null,
    int? Season = null,
    int? Episode = null,
    int? Year = null)
{
    public double ProgressPercent => DurationSeconds <= 0
        ? 0
        : Math.Clamp(PositionSeconds / DurationSeconds * 100.0, 0, 100);

    // Preserve Source as the unique resume target; never merge different episodes or releases.
    public bool HasEpisode => Season is >= 0 && Episode is >= 0;

    public string EpisodeLabel => HasEpisode ? $"S{Season:00} · E{Episode:00}" : string.Empty;

    public string DisplayTitle => !string.IsNullOrWhiteSpace(SeriesTitle)
        ? SeriesTitle
        : Title;

    public string GroupKey => HasEpisode && !string.IsNullOrWhiteSpace(SeriesTitle)
        ? $"series:{CloudMediaIdentity.NormalizeTitle(SeriesTitle)}|year:{Year?.ToString() ?? "-"}"
        : $"source:{Source}";

    public string RemainingTimeText => FormatTime(Math.Max(0, DurationSeconds - PositionSeconds));

    public string ProgressPercentText => $"{ProgressPercent:0}%";

    public string ProgressText => $"{FormatTime(PositionSeconds)} / {FormatTime(DurationSeconds)}";

    public string SourceKindKey => TelegramHubSourcePolicy.IsPersistentTelegramSource(Source)
        ? "Home.ContinueWatching.Telegram"
        : IsLocal ? "Home.ContinueWatching.Local" : "Home.ContinueWatching.Remote";

    public string? ArtworkUrl =>
        !string.IsNullOrWhiteSpace(PosterUrl)
            ? PosterUrl
            : !string.IsNullOrWhiteSpace(BackdropUrl)
                ? BackdropUrl
                : null;

    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkUrl);

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var value = TimeSpan.FromSeconds(seconds);
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }
}
