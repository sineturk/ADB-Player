namespace AltyaziDB.Player.Core.Models;

public sealed record SubtitleSearchOptions(
    string ApiBaseUrl,
    string ApiKey,
    string Language = "tr",
    double DurationSeconds = 0,
    int Page = 1,
    int Limit = 100,
    string Sort = "date",
    string? VersionGroup = null);
