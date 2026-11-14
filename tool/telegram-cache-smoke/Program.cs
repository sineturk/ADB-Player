using System.Text.Json;
using AltyaziDB.Player.Core.Models;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException("P4.14 cache smoke failed: " + message);
}

var defaults = new PlayerSettings();
Check(defaults.TelegramCacheLimitGiB == 10, "10 GiB default cache target");
Check(defaults.TelegramCacheAutoCleanup, "automatic cache maintenance is opt-out");
Check(string.IsNullOrWhiteSpace(defaults.TelegramCacheDirectory),
    "default cache stays inside AppPaths");

defaults.TelegramCacheLimitGiB = 20;
defaults.TelegramCacheAutoCleanup = false;
defaults.TelegramCacheDirectory = @"D:\ADB-Player-Telegram-Cache";
var json = JsonSerializer.Serialize(defaults);
var restored = JsonSerializer.Deserialize<PlayerSettings>(json)
    ?? throw new InvalidDataException("settings deserialization failed");
Check(restored.TelegramCacheLimitGiB == 20 &&
      !restored.TelegramCacheAutoCleanup &&
      restored.TelegramCacheDirectory == defaults.TelegramCacheDirectory,
      "settings survive JSON serialization without changing the Telegram session");

var usage = new TelegramCacheUsage(
    FilesBytes: 2L * 1024 * 1024 * 1024,
    FileCount: 4,
    DatabaseBytes: 200L * 1024 * 1024,
    ArchiveTemporaryBytes: 128L * 1024 * 1024,
    FilesDirectory: defaults.TelegramCacheDirectory);
Check(usage.CacheBytes == usage.FilesBytes + usage.ArchiveTemporaryBytes,
    "cache totals include archive scratch without counting the login database");
Check(usage.CacheBytes < usage.FilesBytes + usage.ArchiveTemporaryBytes + usage.DatabaseBytes,
    "database is excluded from removable cache usage");

Console.WriteLine("[PASS] P4.14 persisted cache settings, immutable database boundary, usage accounting");
