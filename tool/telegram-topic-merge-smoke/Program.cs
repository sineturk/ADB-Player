using System.Text.Json;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Telegram;

static void Require(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException("R5/P4.13g forum merge: " + message);
}

const long forumChat = -1001987654321;
var names = new[]
{
    "Siccin.9.mkv.7z.001", "Siccin.9.mkv.7z.002",
    "Avatar.2026.mkv.7z.001", "Avatar.2026.mkv.7z.002",
    "Weapons.2025.mkv.z01", "Weapons.2025.mkv.z02", "Weapons.2025.mkv.zip",
    "Resident.Evil.mkv.7z.001", "Resident.Evil.mkv.7z.002",
    "Spider.Man.mkv.7z.001", "Spider.Man.mkv.7z.002",
    "Standalone.mkv"
};
var rawMessages = names.Select((name, idx) =>
{
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        id = 5000 + idx,
        chat_id = forumChat.ToString(),
        name
    }));
    return doc.RootElement.Clone();
}).ToArray();
var all = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
Require(TelegramForumMessageMerge.Add(all, rawMessages.Take(2)) == 2,
    "forum-history response has only 2 physical files");
Require(TelegramForumMessageMerge.Add(all, rawMessages) == 10,
    "search-index supplements the 10 missing files");
Require(TelegramForumMessageMerge.Add(all, rawMessages.Take(2)) == 0,
    "history/search duplicates cannot create duplicate cards");
Require(all.Count == 12, "all 12 physical Telegram files collected");
Require(all.Keys.All(key => key.StartsWith(forumChat + ":")),
    "negative supergroup chat IDs are retained and identify each message");

var media = all.Values.Select(raw =>
{
    var name = raw.GetProperty("name").GetString()!;
    var id = raw.GetProperty("id").GetInt32();
    var kind = TelegramArchiveSetDetector.IsArchivePartFileName(name)
        ? TelegramMediaKind.ArchivePart : TelegramMediaKind.Video;
    return new TelegramMediaItem(
        forumChat, id, id, "AMZN", name, "", "application/octet-stream",
        1024L, 0, DateTimeOffset.UtcNow, kind);
}).ToArray();
var cards = TelegramArchiveSetDetector.Group(media);
Require(cards.Count == 6, "12 physical files group into 6 playable cards");
Require(cards.Count(item => item.IsArchiveSet) == 5,
    "5 multipart archives recognized as single cards");
Require(cards.Sum(item => item.ArchiveParts?.Count ?? 1) == 12,
    "physical file count is preserved after grouping");
var splitZip = cards.Single(item => item.FileName.StartsWith("Weapons."));
Require(splitZip.ArchiveFormat == TelegramArchiveFormat.SplitZip &&
    splitZip.ArchiveParts?.Count == 3, "z01/z02/zip remain one card");
Require(cards.Single(item => item.FileName.StartsWith("Siccin.")).ArchiveParts?.Count == 2,
    "legacy playable 7zip part grouping is preserved");

// A second negative chat ID with the same message ID is a different file.
using(var doc = JsonDocument.Parse("""{"id":"5000","chat_id":"-1001999000111","name":"Elsewhere.mkv"}"""))
    Require(TelegramForumMessageMerge.Add(all, [doc.RootElement]) == 1,
        "same message ID in different chat is not accidentally deduped");
Require(all.Count == 13, "cross-chat document independently retained");

Console.WriteLine("[PASS] Telegram forum merge: negative chat IDs, history 2 + search 12, 6 grouped cards, raw file count 12");
