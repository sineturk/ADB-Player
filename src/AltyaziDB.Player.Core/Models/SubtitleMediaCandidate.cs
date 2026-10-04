namespace AltyaziDB.Player.Core.Models;

public sealed record SubtitleMediaCandidate(
    string Title,
    string ContentType,
    int? Year,
    string? ImdbId,
    string? TmdbId,
    string? PosterUrl,
    string? Overview)
{
    public string DisplayLabel => Year is null ? Title : $"{Title} ({Year})";

    public MediaIdentity ApplyTo(MediaIdentity current)
    {
        var type = string.IsNullOrWhiteSpace(ContentType) ? current.ContentType : ContentType;
        var series = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
        return current with
        {
            Title = Title,
            ContentType = type,
            Year = Year ?? current.Year,
            Season = series ? current.Season : null,
            Episode = series ? current.Episode : null,
            ImdbId = ImdbId ?? current.ImdbId,
            TmdbId = TmdbId ?? current.TmdbId
        };
    }
}
