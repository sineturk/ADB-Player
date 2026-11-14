namespace AltyaziDB.Player.Core.Models;

/// <summary>
/// A bounded, session-only Telegram Hub feed. Only media from currently starred
/// channels/groups is eligible; opening/history state is not used or persisted.
/// </summary>
public static class TelegramFavoriteFeedPolicy
{
    public static IReadOnlyList<TelegramMediaItem> Latest(
        IEnumerable<TelegramMediaItem> media,
        IEnumerable<long> favoriteChatIds,
        int limit = 24)
    {
        if (limit <= 0) return [];
        var favorites = favoriteChatIds.ToHashSet();
        if (favorites.Count == 0) return [];
        return media
            .Where(item => favorites.Contains(item.ChatId) &&
                           item.Kind != TelegramMediaKind.ArchivePart)
            .GroupBy(item => (item.ChatId, item.MessageId))
            .Select(group => group.OrderByDescending(item => item.Date).First())
            .OrderByDescending(item => item.Date)
            .ThenByDescending(item => item.MessageId)
            .Take(Math.Min(limit, 100))
            .ToArray();
    }
}
