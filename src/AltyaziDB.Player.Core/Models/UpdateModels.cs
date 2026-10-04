namespace AltyaziDB.Player.Core.Models;

public sealed record UpdateManifest(
    int Schema,
    string Version,
    string Channel,
    DateTimeOffset? PublishedUtc,
    int MinimumWindowsBuild,
    string InstallerUrl,
    string InstallerSha256,
    string PortableUrl,
    string PortableSha256,
    string ReleaseNotes,
    bool Mandatory);

public sealed record UpdateCheckResult(
    bool IsConfigured,
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version? AvailableVersion,
    UpdateManifest? Manifest,
    string Message);

public sealed record UpdateDownloadResult(
    string FilePath,
    string Sha256,
    long SizeBytes);
