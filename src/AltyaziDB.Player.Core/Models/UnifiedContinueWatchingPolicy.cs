namespace AltyaziDB.Player.Core.Models;

/// <summary>
/// Source-safe selection for the common Home continue-watching rail.
/// Telegram titles must be attached to a verified TDLib user identity;
/// session-seen files or ephemeral loopback Range URLs are never candidates.
/// </summary>
public static class UnifiedContinueWatchingPolicy
{
    public static IReadOnlyList<ResumeEntry> TelegramForAccount(
        IEnumerable<ResumeEntry> entries,
        IReadOnlyDictionary<string, long> sourceOwners,
        long? verifiedAccountId,
        bool telegramReady,
        int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(sourceOwners);
        if (!telegramReady || verifiedAccountId is not > 0 || limit <= 0)
            return Array.Empty<ResumeEntry>();

        // Filter account ownership BEFORE the P5 limit and deduplication, so
        // another account's newest files cannot push these entries off the rail.
        var scoped = entries.Where(x =>
            TelegramHubSourcePolicy.IsPersistentTelegramSource(x.Source) &&
            sourceOwners.TryGetValue(x.Source, out var owner) &&
            owner == verifiedAccountId.Value);
        return TelegramHubSourcePolicy.Resumable(scoped, limit);
    }

    public static IReadOnlyList<ResumeEntry> OtherSources(
        IEnumerable<ResumeEntry> entries,
        Func<string, bool> localFileExists,
        int limit = 20,
        Func<string, bool>? isConnectedCloudSource = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(localFileExists);
        if (limit <= 0) return Array.Empty<ResumeEntry>();

        return entries.Where(x =>
                !TelegramHubSourcePolicy.IsPersistentTelegramSource(x.Source) &&
                double.IsFinite(x.PositionSeconds) && double.IsFinite(x.DurationSeconds) &&
                x.PositionSeconds >= 5 && x.DurationSeconds > 0 &&
                x.DurationSeconds - x.PositionSeconds >= 45 &&
                IsReopenable(x.Source, localFileExists, isConnectedCloudSource))
            .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.UpdatedAtUtc).First())
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(limit).ToArray();
    }

    private static bool IsReopenable(
        string? source,
        Func<string, bool> localFileExists,
        Func<string, bool>? isConnectedCloudSource)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (localFileExists(source)) return true;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;

        // Never advertise an ephemeral loopback proxy. Connected rclone sources
        // are persisted as credential-free rclone://remote/path identifiers.
        // Check against the CURRENT account list to avoid exposing media from
        // a disconnected account in Home/Continue Watching.
        if (uri.Scheme.Equals("rclone", StringComparison.OrdinalIgnoreCase))
            return isConnectedCloudSource?.Invoke(source) == true;

        return (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
               !uri.IsLoopback;
    }
}
