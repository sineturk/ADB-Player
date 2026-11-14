using System.Text.Json;

namespace AltyaziDB.Player.Telegram;

/// <summary>
/// TDLib can return far fewer messages than requested. Never treat one
/// short forum-history page as end-of-history; advance the message cursor.
/// </summary>
public static class TelegramForumTopicHistoryPager
{
    public static async Task<IReadOnlyList<JsonElement>> ReadAsync(
        Func<long, int, CancellationToken, Task<JsonElement>> requestPage,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestPage);
        limit = Math.Clamp(limit, 1, 2500);
        var messages = new Dictionary<long, JsonElement>();
        long cursor = 0;
        for (var page = 0; page < Math.Min(limit, 500) && messages.Count < limit; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await requestPage(cursor, Math.Min(100, limit - messages.Count),
                cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("messages", out var batch) ||
                batch.ValueKind != JsonValueKind.Array || batch.GetArrayLength() == 0)
                break;

            long oldest = long.MaxValue;
            var added = 0;
            foreach (var message in batch.EnumerateArray())
            {
                if (!message.TryGetProperty("id", out var idValue)) continue;
                long id;
                if (idValue.ValueKind == JsonValueKind.String)
                {
                    if (!long.TryParse(idValue.GetString(), out id)) continue;
                }
                else if (idValue.ValueKind == JsonValueKind.Number)
                {
                    if (!idValue.TryGetInt64(out id)) continue;
                }
                else continue;
                if (id <= 0) continue;
                oldest = Math.Min(oldest, id);
                if (messages.TryAdd(id, message.Clone())) added++;
            }

            if (oldest == long.MaxValue || oldest <= 1 || added == 0 ||
                (cursor > 0 && oldest >= cursor)) break;
            // TDLib expects the ID of a real starting message, not an
            // arithmetic predecessor that may not exist in a forum thread.
            // The next page can repeat this boundary; the dictionary dedupes
            // it while allowing the older messages to be read.
            cursor = oldest;
        }
        return messages.OrderByDescending(entry => entry.Key)
            .Select(entry => entry.Value)
            .ToArray();
    }
}
