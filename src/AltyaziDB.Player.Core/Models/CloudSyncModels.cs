namespace AltyaziDB.Player.Core.Models;

public sealed record CloudLibraryManifestItem(
    string MediaKey,
    string Title,
    string MediaType,
    int? Year,
    int? Season,
    int? Episode,
    string? ImdbId,
    string? TmdbId,
    string SourceKind);

public sealed record CloudWatchProgressMutation(
    string MediaKey,
    string Source,
    string Title,
    string MediaType,
    int? Year,
    int? Season,
    int? Episode,
    string? ImdbId,
    string? TmdbId,
    string SourceKind,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsCompleted => DurationSeconds > 0 && PositionSeconds >= DurationSeconds * 0.90;
}

public sealed record CloudWatchProgressEntry(
    string MediaKey,
    string Title,
    string MediaType,
    int? Year,
    int? Season,
    int? Episode,
    string? ImdbId,
    string? TmdbId,
    string SourceKind,
    bool HasProgress,
    double PositionSeconds,
    double DurationSeconds,
    bool Completed,
    DateTimeOffset? ClientUpdatedAtUtc,
    DateTimeOffset ServerUpdatedAtUtc)
{
    public bool CanContinue =>
        HasProgress &&
        PositionSeconds >= 5 &&
        DurationSeconds > 0 &&
        DurationSeconds - PositionSeconds >= 45 &&
        !Completed;
}

public sealed record CloudWatchSyncResult(
    bool Success,
    IReadOnlyList<CloudWatchProgressEntry> Entries,
    string? ErrorMessage = null)
{
    public static CloudWatchSyncResult Ok(IReadOnlyList<CloudWatchProgressEntry> entries) =>
        new(true, entries);

    public static CloudWatchSyncResult Fail(string message) =>
        new(false, Array.Empty<CloudWatchProgressEntry>(), message);
}
