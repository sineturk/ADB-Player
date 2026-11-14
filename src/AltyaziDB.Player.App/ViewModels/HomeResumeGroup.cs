namespace AltyaziDB.Player.App.ViewModels;

/// <summary>
/// A visual home-rail group. Each episode retains its original Source and resume timestamp.
/// Grouping never alters/deletes records in the persistent resume store.
/// </summary>
public sealed record HomeResumeGroup(
    string Key,
    string Title,
    IReadOnlyList<ContinueWatchingItem> Episodes)
{
    public ContinueWatchingItem Latest => Episodes[0];
    public int Count => Episodes.Count;
    public bool HasEpisodeChoices => Count > 1;
    public string ChoiceCountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public static IReadOnlyList<HomeResumeGroup> Build(IEnumerable<ContinueWatchingItem> source)
    {
        return source
            .GroupBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var episodes = group
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var latest = episodes[0];
                return new HomeResumeGroup(group.Key, latest.DisplayTitle, episodes);
            })
            .OrderByDescending(group => group.Latest.UpdatedAtUtc)
            .ToArray();
    }
}
