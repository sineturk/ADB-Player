namespace AltyaziDB.Player.App.ViewModels;

public sealed record ContinueWatchingItem(
    string Source,
    string Title,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset UpdatedAtUtc,
    bool IsLocal,
    string? PosterUrl = null,
    string? BackdropUrl = null)
{
    public double ProgressPercent => DurationSeconds <= 0
        ? 0
        : Math.Clamp(PositionSeconds / DurationSeconds * 100.0, 0, 100);

    public string ProgressText => $"{FormatTime(PositionSeconds)} / {FormatTime(DurationSeconds)}";

    public string SourceKindKey => IsLocal ? "Home.ContinueWatching.Local" : "Home.ContinueWatching.Remote";

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
