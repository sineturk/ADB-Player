namespace AltyaziDB.Player.Core.Models;

public sealed record CloudCollection(
    Guid Id,
    string Name,
    string Kind,
    int ItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsSystem =>
        Kind.Equals("favorites", StringComparison.OrdinalIgnoreCase) ||
        Kind.Equals("watchlist", StringComparison.OrdinalIgnoreCase);
}

public sealed record CloudCollectionItem(
    Guid CollectionId,
    string MediaKey,
    string Title,
    string MediaType,
    int? Year,
    int? Season,
    int? Episode,
    string? ImdbId,
    string? TmdbId,
    string SourceKind,
    DateTimeOffset AddedAt);

public sealed record CloudCollectionsResult(
    bool Success,
    IReadOnlyList<CloudCollection> Collections,
    string? ErrorMessage = null)
{
    public static CloudCollectionsResult Ok(IReadOnlyList<CloudCollection> collections) =>
        new(true, collections);

    public static CloudCollectionsResult Fail(string message) =>
        new(false, Array.Empty<CloudCollection>(), message);
}

public sealed record CloudCollectionItemsResult(
    bool Success,
    CloudCollection? Collection,
    IReadOnlyList<CloudCollectionItem> Items,
    string? ErrorMessage = null)
{
    public static CloudCollectionItemsResult Ok(
        CloudCollection collection,
        IReadOnlyList<CloudCollectionItem> items) =>
        new(true, collection, items);

    public static CloudCollectionItemsResult Fail(string message) =>
        new(false, null, Array.Empty<CloudCollectionItem>(), message);
}
