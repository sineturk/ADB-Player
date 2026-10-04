namespace AltyaziDB.Player.App.ViewModels;

public sealed record HistoryItem(
    string Source,
    string Title,
    DateTimeOffset LastOpenedUtc,
    bool IsLocal,
    string? PosterUrl = null,
    string? BackdropUrl = null)
{
    public string? ArtworkUrl =>
        !string.IsNullOrWhiteSpace(PosterUrl)
            ? PosterUrl
            : !string.IsNullOrWhiteSpace(BackdropUrl)
                ? BackdropUrl
                : null;

    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkUrl);

    public string LastOpenedText =>
        LastOpenedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
}
