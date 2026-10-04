using System.Text.Json.Serialization;

namespace AltyaziDB.Player.Connections.Models;

public enum ExternalConnectionType
{
    Prowlarr,
    Torznab,
    Sonarr,
    Radarr
}

public enum ExternalContentType
{
    All,
    Movie,
    Series,
    Anime,
    Other
}

public sealed record ConnectionProfile(
    string Id,
    string Name,
    ExternalConnectionType Type,
    bool IsEnabled,
    DateTimeOffset AddedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastTestUtc = null,
    bool LastTestSucceeded = false,
    string? LastVersion = null,
    int? LastItemCount = null,
    string? LastMessage = null)
{
    [JsonIgnore]
    public bool SupportsReleaseSearch => Type is ExternalConnectionType.Prowlarr or ExternalConnectionType.Torznab;

    [JsonIgnore]
    public string StatusGlyph => LastTestSucceeded ? "●" : "○";

    [JsonIgnore]
    public string ItemCountLabel => LastItemCount is > -1 ? LastItemCount.Value.ToString() : string.Empty;
}

public sealed record ConnectionConfiguration(
    string? Id,
    string Name,
    ExternalConnectionType Type,
    string BaseUrl,
    bool HasStoredApiKey,
    string MovieCategories,
    string SeriesCategories,
    string AnimeCategories,
    string OtherCategories,
    int ResultLimit,
    bool AllowLocalHttp);

public sealed record SaveConnectionRequest(
    string? Id,
    string Name,
    ExternalConnectionType Type,
    string BaseUrl,
    string? ApiKey,
    string MovieCategories,
    string SeriesCategories,
    string AnimeCategories,
    string OtherCategories,
    int ResultLimit,
    bool AllowLocalHttp,
    bool LegalNoticeAccepted,
    bool UserOwnsServiceConfirmed);

public sealed record ConnectionTestResult(
    bool IsSuccess,
    string Message,
    string? Version = null,
    int? ItemCount = null,
    bool SupportsReleaseSearch = false);

public sealed record ExternalSearchRequest(
    ExternalContentType ContentType,
    string Query,
    int Limit = 100);

public sealed record ExternalRelease(
    string Key,
    string ConnectionId,
    string ConnectionName,
    ExternalConnectionType ConnectionType,
    string Title,
    ExternalContentType ContentType,
    DateTimeOffset? PublishedUtc,
    long? SizeBytes,
    int? Seeders,
    int? Leechers,
    string? Indexer,
    string? MagnetUri,
    string? DownloadUrl,
    string? InfoHash,
    string? ImdbId,
    string? TmdbId,
    string? TvdbId,
    IReadOnlyList<int> CategoryIds,
    IReadOnlyList<string> CategoryNames)
{
    public string SizeLabel => FormatSize(SizeBytes);
    public string SeedersLabel => Seeders is >= 0 ? $"{Seeders} seed" : string.Empty;
    public string PublishedLabel => PublishedUtc?.ToLocalTime().ToString("g") ?? string.Empty;
    public string CategoryLabel => string.Join(" · ", CategoryNames.Take(3));
    public string DetailLabel => string.Join(" · ", new[] { SizeLabel, SeedersLabel, PublishedLabel }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool HasPlayableSource => !string.IsNullOrWhiteSpace(MagnetUri) ||
                                     !string.IsNullOrWhiteSpace(InfoHash) ||
                                     !string.IsNullOrWhiteSpace(DownloadUrl);

    private static string FormatSize(long? value)
    {
        if (value is null or <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value.Value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}

public sealed record ExternalSearchResult(
    IReadOnlyList<ExternalRelease> Releases,
    IReadOnlyList<string> Warnings,
    DateTimeOffset LoadedUtc);
