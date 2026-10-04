namespace AltyaziDB.Player.Core.Models;

public sealed record MediaIdentity(
    string Title,
    string ContentType,
    string ReleaseName,
    int? Year = null,
    int? Season = null,
    int? Episode = null,
    string? ImdbId = null,
    string? TmdbId = null)
{
    public bool CanSearch =>
        !string.IsNullOrWhiteSpace(ImdbId) ||
        !string.IsNullOrWhiteSpace(TmdbId) ||
        !string.IsNullOrWhiteSpace(Title);

    public bool IsSeries => string.Equals(ContentType, "series", StringComparison.OrdinalIgnoreCase);

    public string EpisodeLabel => Season is not null && Episode is not null
        ? $"S{Season.Value:00}E{Episode.Value:00}"
        : string.Empty;
}
