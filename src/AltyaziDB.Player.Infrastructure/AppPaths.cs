using System.Text.Json;

namespace AltyaziDB.Player.Infrastructure;

public sealed record AppPaths(
    string RootDirectory,
    string SettingsFile,
    string ResumeFile,
    string LibraryDatabaseFile,
    string SecretsFile,
    string LogDirectory,
    string CrashDirectory,
    string UpdateDirectory,
    string ScreenshotDirectory,
    string SubtitleDirectory,
    string RemoteCacheDirectory,
    string TelegramDatabaseDirectory,
    string TelegramFilesDirectory,
    string TorrentCacheDirectory,
    string RcloneDirectory,
    string RcloneRuntimeDirectory,
    string RcloneCacheDirectory,
    string RcloneLogDirectory,
    string ConnectionRegistryFile,
    string ConnectionCacheDirectory,
    string AddonRegistryFile,
    string AddonCacheDirectory,
    bool IsPortable)
{
    public static AppPaths CreateDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var portable = File.Exists(Path.Combine(baseDirectory, "portable.flag"));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var root = portable
            ? Path.Combine(baseDirectory, "data")
            : ResolveInstalledRoot(localAppData);

        var logs = Path.Combine(root, "logs");
        var crashes = Path.Combine(root, "crashes");
        var updates = Path.Combine(root, "updates");
        var subtitles = Path.Combine(root, "subtitles");
        var remoteCache = Path.Combine(root, "remote-cache");
        var telegramDatabase = Path.Combine(root, "telegram", "database");
        var telegramFiles = Path.Combine(root, "telegram", "files");
        var torrentCache = Path.Combine(root, "torrent-cache");
        var rclone = Path.Combine(root, "rclone");
        var rcloneRuntime = Path.Combine(rclone, "runtime");
        var rcloneCache = Path.Combine(rclone, "cache");
        // An optional dedicated cache location is read before services start.
        // The selected parent is NEVER cleared: only the reserved marked
        // ADB Player child directory can be used as a cache root.
        try
        {
            var settings = Path.Combine(root, "settings.json");
            if (File.Exists(settings))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settings));
                if (document.RootElement.TryGetProperty("CloudCacheDirectory", out var location)
                    && location.ValueKind == JsonValueKind.String
                    && TryPrepareCloudCacheDirectory(location.GetString(), out var chosen))
                    rcloneCache = chosen;
            }
        }
        catch (Exception) { /* fall back to the app-owned default cache */ }
        var rcloneLogs = Path.Combine(rclone, "logs");
        var connectionCache = Path.Combine(root, "connection-cache");
        var addonCache = Path.Combine(root, "addon-cache");
        var screenshots = portable || string.IsNullOrWhiteSpace(pictures)
            ? Path.Combine(root, "screenshots")
            : Path.Combine(pictures, "ADB Player");

        foreach (var directory in new[]
                 {
                     root, logs, crashes, updates, subtitles, remoteCache,
                     telegramDatabase, telegramFiles, torrentCache, rclone, rcloneRuntime, rcloneCache, rcloneLogs, connectionCache, addonCache, screenshots
                 })
        {
            Directory.CreateDirectory(directory);
        }

        return new AppPaths(
            root,
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "resume.json"),
            Path.Combine(root, "library.db"),
            Path.Combine(root, "secrets.dat"),
            logs,
            crashes,
            updates,
            screenshots,
            subtitles,
            remoteCache,
            telegramDatabase,
            telegramFiles,
            torrentCache,
            rclone,
            rcloneRuntime,
            rcloneCache,
            rcloneLogs,
            Path.Combine(root, "connections.json"),
            connectionCache,
            Path.Combine(root, "addons.json"),
            addonCache,
            portable);
    }
    public static bool TryPrepareCloudCacheDirectory(string? selectedParent, out string managedDirectory)
    {
        managedDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(selectedParent) || !Path.IsPathFullyQualified(selectedParent))
            return false;
        try
        {
            var parent = Path.GetFullPath(selectedParent);
            if (parent.StartsWith(@"\\", StringComparison.Ordinal) ||
                !Directory.Exists(parent) ||
                string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetPathRoot(parent)?.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            // Reject a parent whose path includes a junction/symlink, and
            // reject a preexisting non-empty child that we do not own.
            var dir = new DirectoryInfo(parent);
            for (var current = dir; current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var candidate = Path.Combine(parent, "ADB Player Cloud Cache");
            var marker = Path.Combine(candidate, ".adb-player-cache-owner");
            if (Directory.Exists(candidate))
            {
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (File.Exists(marker))
                {
                    if (File.ReadAllText(marker).Trim() != "ADB_PLAYER_CLOUD_CACHE_V1")
                        return false;
                }
                else if (Directory.EnumerateFileSystemEntries(candidate).Any())
                    return false;
            }
            Directory.CreateDirectory(candidate);
            if (!File.Exists(marker))
                File.WriteAllText(marker, "ADB_PLAYER_CLOUD_CACHE_V1");
            managedDirectory = candidate;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string ResolveInstalledRoot(string localAppData)
    {
        var root = Path.Combine(localAppData, "ADB", "Player");
        var legacyRoot = Path.Combine(localAppData, "AltyaziDB", "Player");

        if (Directory.Exists(root) || !Directory.Exists(legacyRoot))
        {
            return root;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(legacyRoot, root);

            var legacyParent = Path.GetDirectoryName(legacyRoot);
            if (!string.IsNullOrWhiteSpace(legacyParent)
                && Directory.Exists(legacyParent)
                && !Directory.EnumerateFileSystemEntries(legacyParent).Any())
            {
                Directory.Delete(legacyParent);
            }

            return root;
        }
        catch
        {
            // Keep existing user data usable if the one-time rename cannot be completed.
            return legacyRoot;
        }
    }

}
