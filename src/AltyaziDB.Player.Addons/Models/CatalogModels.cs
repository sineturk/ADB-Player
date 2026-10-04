using System.Text.Json.Serialization;

namespace AltyaziDB.Player.Addons.Models;

public enum CatalogContentType
{
    All,
    Movie,
    Series,
    Anime
}

public enum CatalogStreamKind
{
    Torrent,
    Direct,
    External
}

public sealed record AddonRegistration(
    string Id,
    string Name,
    string ManifestUrl,
    string Version,
    bool IsEnabled,
    DateTimeOffset AddedUtc,
    string? Description = null,
    bool HasCatalogs = false,
    bool SupportsStreams = false,
    bool SupportsMetadata = false,
    bool AllowLocalHttp = false)
{
    [JsonIgnore]
    public string SafeManifestUrl
    {
        get
        {
            if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme}://{uri.Host}{port}/…/manifest.json";
        }
    }

    [JsonIgnore]
    public string CapabilityLabel => string.Join(" · ", new[]
    {
        HasCatalogs ? "Catalog" : string.Empty,
        SupportsMetadata ? "Meta" : string.Empty,
        SupportsStreams ? "Stream" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record AddonCatalogDescriptor(
    string AddonId,
    string AddonName,
    string Type,
    string Id,
    string Name,
    IReadOnlyList<string> Genres,
    bool SupportsSkip);

public sealed record CatalogItem(
    string Key,
    string ExternalId,
    string SourceType,
    CatalogContentType ContentType,
    string Title,
    int? Year,
    string? PosterUrl,
    string? BackgroundUrl,
    string? Description,
    string? Rating,
    DateTimeOffset? ReleasedUtc,
    IReadOnlyList<string> Genres,
    string OriginAddonId,
    string OriginAddonName,
    string OriginCatalogId)
{
    public string YearLabel => Year?.ToString() ?? string.Empty;
    public string GenreLabel => string.Join(" · ", Genres.Take(3));
}

public sealed record CatalogEpisode(
    string ExternalId,
    string Title,
    int? Season,
    int? Episode,
    DateTimeOffset? ReleasedUtc,
    string? ThumbnailUrl,
    string? Description)
{
    public string NumberLabel => Season is not null && Episode is not null
        ? $"S{Season:00}E{Episode:00}"
        : Episode is not null
            ? $"E{Episode:00}"
            : string.Empty;

    public string DisplayLabel => string.Join(" · ", new[] { NumberLabel, Title }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record CatalogStream(
    string Key,
    string AddonId,
    string AddonName,
    CatalogStreamKind Kind,
    string Name,
    string Description,
    string? Url,
    string? InfoHash,
    int? FileIndex,
    string? FileName,
    string? Quality,
    int? Seeders,
    long? SizeBytes)
{
    public string SizeLabel => FormatSize(SizeBytes);
    public string SeedersLabel => Seeders is > 0 ? $"{Seeders} seed" : string.Empty;
    public string DetailLabel => string.Join(" · ", new[] { Quality, SizeLabel, SeedersLabel }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

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

public sealed record AddonCatalogSnapshot(
    IReadOnlyList<AddonRegistration> Addons,
    IReadOnlyList<CatalogItem> Items,
    DateTimeOffset LoadedUtc,
    IReadOnlyList<string> Warnings);
