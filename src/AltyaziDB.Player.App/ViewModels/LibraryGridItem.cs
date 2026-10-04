using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.ViewModels;

public sealed record LibraryGridItem(
    string Key,
    bool IsSeriesHub,
    string DisplayTitle,
    string MediaType,
    int? Year,
    IReadOnlyList<LibraryMediaItem> Members,
    string MetaLabel)
{
    public LibraryMediaItem Representative =>
        Members.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PosterUrl))
        ?? Members.First();

    public string? ArtworkUrl => Representative.ArtworkUrl;
    public string? BackdropUrl => Representative.BackdropUrl;

    public int EpisodeCount => IsSeriesHub ? Members.Count : 0;

    public int SeasonCount => IsSeriesHub
        ? Members
            .Select(item => item.Season ?? 0)
            .Distinct()
            .Count()
        : 0;

    public long SizeBytes => Members.Sum(item => Math.Max(0, item.SizeBytes));

    public string SizeLabel
    {
        get
        {
            const double gib = 1024d * 1024d * 1024d;
            const double mib = 1024d * 1024d;
            return SizeBytes >= gib
                ? $"{SizeBytes / gib:0.00} GB"
                : $"{SizeBytes / mib:0} MB";
        }
    }

    public int CompletedEpisodeCount => IsSeriesHub
        ? Members.Count(item => item.IsCompleted)
        : Representative.IsCompleted
            ? 1
            : 0;

    public double ProgressPercent
    {
        get
        {
            if (!IsSeriesHub)
                return Representative.ProgressPercent;

            if (Members.Count == 0)
                return 0;

            var total = Members.Sum(item =>
                item.IsCompleted
                    ? 100d
                    : item.ProgressPercent);

            return Math.Clamp(total / Members.Count, 0, 100);
        }
    }

    public LibraryMediaItem NextPlayableItem
    {
        get
        {
            if (!IsSeriesHub)
                return Representative;

            var ordered = Members
                .OrderBy(item => item.Season ?? int.MaxValue)
                .ThenBy(item => item.Episode ?? int.MaxValue)
                .ThenBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            return ordered.FirstOrDefault(item => item.CanContinue)
                   ?? ordered.FirstOrDefault(item => !item.IsCompleted)
                   ?? ordered.First();
        }
    }
}

public sealed record LibrarySeriesSeasonChoice(
    int Season,
    int EpisodeCount,
    string Label);
