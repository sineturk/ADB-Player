namespace AltyaziDB.Player.Core.Models;

public sealed record CloudPlaylistItem(
    int Position,
    string MediaKey,
    string Title,
    string MediaType,
    int? Year,
    int? Season,
    int? Episode,
    string? ImdbId,
    string? TmdbId,
    string SourceKind);

public sealed record CloudPlaylistSnapshot(
    IReadOnlyList<CloudPlaylistItem> Items,
    string? CurrentMediaKey,
    DateTimeOffset UpdatedAt);

public sealed record CloudPlaylistResult(
    bool Success,
    CloudPlaylistSnapshot? Snapshot,
    string? ErrorMessage = null)
{
    public static CloudPlaylistResult Ok(CloudPlaylistSnapshot snapshot) =>
        new(true, snapshot);

    public static CloudPlaylistResult Fail(string message) =>
        new(false, null, message);
}
