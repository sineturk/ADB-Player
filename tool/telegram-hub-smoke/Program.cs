using AltyaziDB.Player.Core.Models;
static void Check(bool ok, string message)
{
    if (!ok) throw new Exception("[P5 FAIL] " + message);
    Console.WriteLine("[PASS] " + message);
}
var t = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
var opened = new[]
{
    new RecentSourceEntry("http://127.0.0.1:18765/file", "ephemeral", t, false),
    new RecentSourceEntry("https://example.com/file", "web", t, false),
    new RecentSourceEntry("telegram:12:34", "old", t.AddMinutes(-8), false),
    new RecentSourceEntry("telegram:12:34", "new", t, false),
    new RecentSourceEntry("telegram-archive:12:archive.7z:movie.mkv", "7z", t.AddMinutes(-2), false),
    new RecentSourceEntry("telegramx:12:34", "fake", t, false)
};
var recent = TelegramHubSourcePolicy.RecentlyOpened(opened);
Check(recent.Count == 2, "Only actual opened stable Telegram identities");
Check(recent[0].Title == "new" && recent[1].Title == "7z", "Dedup and recency preserved");
Check(TelegramHubSourcePolicy.RecentlyOpened(opened, 1).Count == 1 &&
      TelegramHubSourcePolicy.RecentlyOpened(opened, 0).Count == 0, "Recent bounds");
var resumes = new[]
{
    new ResumeEntry("telegram:12:34", 30, 1100, t),
    new ResumeEntry("telegram-archive:12:archive.7z:movie.mkv", 200, 900, t.AddMinutes(-2)),
    new ResumeEntry("telegram:12:35", 900, 910, t),
    new ResumeEntry("telegram:12:36", 2, 1200, t),
    new ResumeEntry("telegram:12:37", double.NaN, 900, t),
    new ResumeEntry("http://127.0.0.1:1234/stream", 90, 900, t)
};
var watch = TelegramHubSourcePolicy.Resumable(resumes);
Check(watch.Count == 2, "Unfinished Telegram resumes only");
Check(watch[0].Source == "telegram:12:34", "Resume most recent first");
Check(TelegramHubSourcePolicy.Resumable(resumes, 1).Count == 1, "Resume bounds");
Check(TelegramHubSourcePolicy.IsPersistentTelegramSource("TELEGRAM:12:34") &&
      !TelegramHubSourcePolicy.IsPersistentTelegramSource("telegramx:12:34") &&
      !TelegramHubSourcePolicy.IsPersistentTelegramSource(null), "Source prefix exactness");
// P4.15.4: Home's Telegram subset is account-scoped before its limit.
var scopedOwners = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
{
    ["telegram:12:34"] = 101,
    ["telegram-archive:12:archive.7z:movie.mkv"] = 202
};
var foreignHeavy = Enumerable.Range(0, 20)
    .Select(i => new ResumeEntry($"telegram:{i + 100}:12", 200, 1200, t.AddMinutes(i)))
    .Concat(resumes)
    .ToArray();
foreach (var item in foreignHeavy.Where(x => x.Source.StartsWith("telegram:") &&
                                           x.Source != "telegram:12:34"))
    scopedOwners[item.Source] = 202;
var own = UnifiedContinueWatchingPolicy.TelegramForAccount(foreignHeavy, scopedOwners, 101, true, 12);
Check(own.Count == 1 && own[0].Source == "telegram:12:34",
    "Unified Home returns only the verified account's stable Telegram source");
Check(UnifiedContinueWatchingPolicy.TelegramForAccount(foreignHeavy, scopedOwners, 202, true, 12)
      .All(x => x.Source != "telegram:12:34"),
    "Another Telegram account cannot see the previous account's resume");
Check(UnifiedContinueWatchingPolicy.TelegramForAccount(foreignHeavy, scopedOwners, null, true).Count == 0 &&
      UnifiedContinueWatchingPolicy.TelegramForAccount(foreignHeavy, scopedOwners, 101, false).Count == 0,
    "A disconnected or unverified account has no Telegram Home cards");
Check(UnifiedContinueWatchingPolicy.TelegramForAccount(foreignHeavy, scopedOwners, 101, true, 0).Count == 0,
    "Account scope respects zero item limit");
var other = new[]
{
    new ResumeEntry("C:/Existing/test.mkv", 42, 1000, t.AddMinutes(-2)),
    new ResumeEntry("https://cloud.example/video.mp4", 50, 600, t),
    new ResumeEntry("http://127.0.0.1:18765/stream", 55, 600, t),
    new ResumeEntry("telegram:12:34", 45, 1000, t),
    new ResumeEntry("gdrive:opaque/file.mkv", 50, 600, t),
    new ResumeEntry("https://cloud.example/finished", 900, 920, t)
};
var openable = UnifiedContinueWatchingPolicy.OtherSources(
    other, source => source == "C:/Existing/test.mkv");
Check(openable.Count == 2 && openable[0].Source == "https://cloud.example/video.mp4",
    "Home merges local and HTTP remote sources sorted by most recent play");
Check(!openable.Any(x => x.Source.Contains("127.0.0.1") || x.Source.StartsWith("telegram:")),
    "Ephemeral localhost Range URLs and Telegram are excluded from local/http candidates");
// P4.17.2: rclone resumes are stable only while their owning account remains connected.
var cloudResumes = other.Concat(new[]
{
    new ResumeEntry("rclone://adb-drive-1/Movies/Arrival.2016.mkv", 66, 640, t.AddMinutes(1)),
    new ResumeEntry("rclone://adb-drive-2/Private/Hidden.mkv", 80, 500, t.AddMinutes(3))
}).ToArray();
var disconnected = UnifiedContinueWatchingPolicy.OtherSources(
    cloudResumes, x => x == "C:/Existing/test.mkv");
Check(disconnected.Count == 2, "Default policy never reveals disconnected cloud media");
var connected = UnifiedContinueWatchingPolicy.OtherSources(
    cloudResumes,
    x => x == "C:/Existing/test.mkv",
    isConnectedCloudSource: x => x.StartsWith("rclone://adb-drive-1/", StringComparison.OrdinalIgnoreCase));
Check(connected.Count == 3 &&
      connected.Any(x => x.Source == "rclone://adb-drive-1/Movies/Arrival.2016.mkv") &&
      !connected.Any(x => x.Source.Contains("adb-drive-2", StringComparison.OrdinalIgnoreCase)),
    "Only explicitly connected cloud media appears on Home");
Check(connected.All(x => !x.Source.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)),
    "Temporary local rclone playback proxy is never shown after restart");
Console.WriteLine("[PASS] Unified Continue Watching synthetic smoke; no live accounts, Cloud auth or playback");
// R5/P4.17.8: dedicated feed comes only from current starred chat IDs,
// deduplicates TDLib merge hits, and sorts by actual Telegram message timestamp.
var favoriteSamples = new[]
{
    new TelegramMediaItem(11, 101, 1, "Channel A", "older.mkv", "", "video/x-matroska",
        100, 100, t.AddMinutes(-5), TelegramMediaKind.Video),
    new TelegramMediaItem(22, 202, 2, "Group B", "newer.mkv", "", "video/x-matroska",
        100, 100, t.AddMinutes(5), TelegramMediaKind.Video),
    new TelegramMediaItem(11, 101, 1, "Channel A", "older.mkv", "", "video/x-matroska",
        100, 100, t.AddMinutes(-5), TelegramMediaKind.Video),
    new TelegramMediaItem(33, 303, 3, "Not starred", "hidden.mkv", "", "video/x-matroska",
        100, 100, t.AddMinutes(10), TelegramMediaKind.Video),
    new TelegramMediaItem(22, 204, 4, "Group B", "partial.002", "", "application/octet-stream",
        100, 0, t.AddMinutes(8), TelegramMediaKind.ArchivePart)
};
var feed = TelegramFavoriteFeedPolicy.Latest(favoriteSamples, [11L, 22L]);
Check(feed.Count == 2 && feed[0].MessageId == 202 && feed[1].MessageId == 101,
    "Favorite feed orders newest first, excludes unstarred and raw archive parts");
Check(TelegramFavoriteFeedPolicy.Latest(favoriteSamples, [11L], 1).Count == 1 &&
      TelegramFavoriteFeedPolicy.Latest(favoriteSamples, [11L], 1)[0].ChatId == 11,
    "Favorite feed respects per-account allowed chat IDs and result limits");
Check(TelegramFavoriteFeedPolicy.Latest(favoriteSamples, [], 24).Count == 0 &&
      TelegramFavoriteFeedPolicy.Latest(favoriteSamples, [22L], 0).Count == 0,
    "Favorite feed discloses nothing after unstar or when limit is zero");
Console.WriteLine("[PASS] Telegram Hub synthetic smoke; no live account or playback");
