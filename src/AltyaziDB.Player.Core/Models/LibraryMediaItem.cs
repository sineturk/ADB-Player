namespace AltyaziDB.Player.Core.Models;

public sealed record LibraryMediaItem(
    long Id,
    string Path,
    string RootPath,
    string FileName,
    string Title,
    string MediaType,
    int? Year,
    int? Season,
    int? Episode,
    long SizeBytes,
    DateTimeOffset ModifiedAtUtc,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset? LastPlayedAtUtc,
    string? ResolvedTitle = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    long? AdbId = null,
    string? TmdbId = null,
    string? ImdbId = null,
    string? Overview = null,
    string? MetadataSource = null,
    double? MatchConfidence = null,
    int TurkishSubtitleCount = 0,
    int EnglishSubtitleCount = 0)
{
    public bool IsSeries => string.Equals(MediaType, "series", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => DurationSeconds > 0 && PositionSeconds >= DurationSeconds * 0.90;
    public bool CanContinue => PositionSeconds >= 30 && DurationSeconds > 0 && !IsCompleted;
    public double ProgressPercent => DurationSeconds <= 0
        ? 0
        : Math.Clamp(PositionSeconds / DurationSeconds * 100, 0, 100);

    public string EpisodeLabel => Season is not null && Episode is not null
        ? $"S{Season.Value:00}E{Episode.Value:00}"
        : string.Empty;

    public string EffectiveTitle =>
        string.IsNullOrWhiteSpace(ResolvedTitle) ? Title : ResolvedTitle.Trim();

    public string? ArtworkUrl =>
        !string.IsNullOrWhiteSpace(PosterUrl)
            ? PosterUrl
            : !string.IsNullOrWhiteSpace(BackdropUrl)
                ? BackdropUrl
                : null;

    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkUrl);
    public bool HasOverview => !string.IsNullOrWhiteSpace(Overview);
    public bool HasMetadataIdentity =>
        AdbId is not null
        || !string.IsNullOrWhiteSpace(TmdbId)
        || !string.IsNullOrWhiteSpace(ImdbId);

    public string SubtitleAvailabilityLabel =>
        $"TR {TurkishSubtitleCount} · EN {EnglishSubtitleCount}";

    public string DisplayTitle
    {
        get
        {
            var suffix = EpisodeLabel;
            if (Year is not null && !IsSeries)
            {
                suffix = string.IsNullOrEmpty(suffix) ? Year.Value.ToString() : $"{suffix} · {Year}";
            }

            return string.IsNullOrEmpty(suffix) ? EffectiveTitle : $"{EffectiveTitle} · {suffix}";
        }
    }

    public string ProgressLabel => IsCompleted
        ? "İzlendi"
        : CanContinue
            ? $"%{ProgressPercent:0} · devam et"
            : "İzlenmedi";

    public string SizeLabel
    {
        get
        {
            const double gib = 1024d * 1024d * 1024d;
            const double mib = 1024d * 1024d;
            return SizeBytes >= gib ? $"{SizeBytes / gib:0.00} GB" : $"{SizeBytes / mib:0} MB";
        }
    }
}
