namespace AltyaziDB.Player.Core.Models;

/// <summary>
/// Telegram Hub source rules. Session-discovered messages must not be called
/// "recently opened"; loopback Range URLs must never be persisted as sources.
/// No network, TDLib, playback or user/account state is touched here.
/// </summary>
public static class TelegramHubSourcePolicy
{
    public static bool IsPersistentTelegramSource(string? source) =>
        !string.IsNullOrWhiteSpace(source) &&
        (source.StartsWith("telegram:", StringComparison.OrdinalIgnoreCase) ||
         source.StartsWith("telegram-archive:", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<RecentSourceEntry> RecentlyOpened(
        IEnumerable<RecentSourceEntry> entries, int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (limit <= 0) return Array.Empty<RecentSourceEntry>();
        return entries.Where(x => IsPersistentTelegramSource(x.Source))
            .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.LastOpenedUtc).First())
            .OrderByDescending(x => x.LastOpenedUtc).Take(limit).ToArray();
    }

    public static IReadOnlyList<ResumeEntry> Resumable(
        IEnumerable<ResumeEntry> entries, int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (limit <= 0) return Array.Empty<ResumeEntry>();
        return entries.Where(x => IsPersistentTelegramSource(x.Source) &&
                         double.IsFinite(x.PositionSeconds) && double.IsFinite(x.DurationSeconds) &&
                         x.PositionSeconds >= 5 && x.DurationSeconds > 0 &&
                         x.DurationSeconds - x.PositionSeconds >= 45)
            .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.UpdatedAtUtc).First())
            .OrderByDescending(x => x.UpdatedAtUtc).Take(limit).ToArray();
    }
}
