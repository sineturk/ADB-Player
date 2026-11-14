using System.Globalization;
using System.Text.Json;

namespace AltyaziDB.Player.Telegram;

/// <summary>
/// Merges TDLib topic-history and search-index results without losing the
/// negative chat identifiers assigned to Telegram supergroups/channels.
/// </summary>
public static class TelegramForumMessageMerge
{
    public static int Add(
        IDictionary<string, JsonElement> destination,
        IEnumerable<JsonElement> incoming)
    {
        var added = 0;
        foreach (var message in incoming)
        {
            var id = ReadId(message, "id");
            var chatId = ReadId(message, "chat_id");
            if (id <= 0 || chatId == 0) continue;

            var key = chatId.ToString(CultureInfo.InvariantCulture) + ":" +
                      id.ToString(CultureInfo.InvariantCulture);
            if (!destination.ContainsKey(key)) added++;
            destination[key] = message.Clone();
        }
        return added;
    }

    private static long ReadId(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number)) return number;
        return 0;
    }
}
