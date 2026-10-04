namespace AltyaziDB.Player.Core.Models;

public enum RemoteSourceProvider
{
    Direct,
    Dropbox,
    PCloud,
    GoogleDrive,
    OneDrive,
    GdFlix,
    HubCloud,
    VidMoly,
    OkRu,
    Vk,
    Abyss,
    AkiraBox,
    Gofile,
    PixelDrain,
    WebDav,
    Rclone
}

public enum RemoteSourceItemKind
{
    Folder,
    Video,
    Subtitle,
    Audio,
    Archive,
    Other
}

public sealed record RemoteOpenRequest(
    string Source,
    string DisplayName,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    string? Provider = null,
    string? PersistentSource = null);

public sealed record RemoteSourceItem(
    string Id,
    string Name,
    RemoteSourceItemKind Kind,
    string? OpenUrl,
    string? BrowsePath,
    long? Size,
    DateTimeOffset? ModifiedUtc,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    string? Provider = null)
{
    public bool IsFolder => Kind == RemoteSourceItemKind.Folder;
    public bool IsPlayable => Kind == RemoteSourceItemKind.Video && !string.IsNullOrWhiteSpace(OpenUrl);
    public bool IsSubtitle => Kind == RemoteSourceItemKind.Subtitle && !string.IsNullOrWhiteSpace(OpenUrl);
    public bool IsAudio => Kind == RemoteSourceItemKind.Audio && !string.IsNullOrWhiteSpace(OpenUrl);
    public bool IsArchive => Kind == RemoteSourceItemKind.Archive && !string.IsNullOrWhiteSpace(OpenUrl);

    public string KindLabel => Kind switch
    {
        RemoteSourceItemKind.Folder => "Klasör",
        RemoteSourceItemKind.Video => "Video",
        RemoteSourceItemKind.Subtitle => "Altyazı",
        RemoteSourceItemKind.Audio => "Ses",
        RemoteSourceItemKind.Archive => "Arşiv",
        _ => "Dosya"
    };

    public string SizeLabel => Size is null ? string.Empty : FormatSize(Size.Value);

    private static string FormatSize(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = Math.Max(0, value);
        var unit = 0;
        double display = amount;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.##} {units[unit]}";
    }
}

public sealed record RemoteBrowseResult(
    RemoteSourceProvider Provider,
    string DisplayName,
    string? CurrentPath,
    IReadOnlyList<RemoteSourceItem> Items,
    RemoteOpenRequest? DirectOpen = null,
    string? ParentPath = null,
    string? StatusMessage = null);

public sealed record WebDavConnection(
    string BaseUrl,
    string Username,
    string Password);

public sealed class SavedWebDavProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "WebDAV";
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    public override string ToString() => Name;
}

public sealed class SavedPublicLink
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset LastOpenedUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => Name;
}
