namespace AltyaziDB.Player.Core.Models;

public sealed record PlayerApiAuthContext(
    string AccessToken,
    string DeviceKey)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(AccessToken)
        && Guid.TryParse(DeviceKey, out _);
}

public sealed record MediaMetadataMatch(
    string Source,
    string Method,
    double Confidence);

public sealed record ResolvedMediaMetadata(
    long? AdbId,
    string MediaType,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? ImdbId,
    string? TmdbId,
    string? PosterUrl,
    string? BackdropUrl,
    string? Overview);

public sealed record MediaSubtitleAvailability(
    bool Available,
    int TotalCount,
    int TurkishCount,
    int EnglishCount);

public sealed record MediaMetadataResult(
    bool Success,
    bool Matched,
    string? ClientId,
    MediaMetadataMatch? Match,
    ResolvedMediaMetadata? Media,
    MediaSubtitleAvailability Subtitles,
    bool CacheHit,
    string? Error)
{
    public static MediaMetadataResult Fail(string error, string? clientId = null) =>
        new(
            false,
            false,
            clientId,
            null,
            null,
            new MediaSubtitleAvailability(false, 0, 0, 0),
            false,
            error);
}
