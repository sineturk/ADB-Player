namespace AltyaziDB.Player.Core.Models;

public sealed record CloudAccountUser(
    string Id,
    string Email,
    string DisplayName,
    string? ImageUrl = null,
    bool EmailVerified = false);

public sealed record CloudAccountSession(
    string Id,
    string UserId,
    DateTimeOffset? ExpiresAt = null);

public sealed record CloudAccountSnapshot(
    CloudAccountUser User,
    CloudAccountSession? Session);

public sealed record CloudAccountResult(
    bool Success,
    CloudAccountSnapshot? Snapshot,
    string? ErrorMessage = null)
{
    public static CloudAccountResult Ok(CloudAccountSnapshot snapshot) => new(true, snapshot);
    public static CloudAccountResult Fail(string message) => new(false, null, message);
}

public sealed record CloudProfile(
    string UserId,
    string DisplayName,
    string? AvatarUrl,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CloudDevice(
    Guid Id,
    string UserId,
    string DeviceKey,
    string DisplayName,
    string Platform,
    string? AppVersion,
    DateTimeOffset LastSeenAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsCurrentDevice = false);

public sealed record CloudAccountCoreSnapshot(
    CloudProfile? Profile,
    IReadOnlyList<CloudDevice> Devices,
    CloudDevice? CurrentDevice);

public sealed record CloudAccountCoreResult(
    bool Success,
    CloudAccountCoreSnapshot? Snapshot,
    string? ErrorMessage = null)
{
    public static CloudAccountCoreResult Ok(CloudAccountCoreSnapshot snapshot) => new(true, snapshot);
    public static CloudAccountCoreResult Fail(string message) => new(false, null, message);
}
