namespace AltyaziDB.Player.Core.Models;

public enum RcloneCloudProvider
{
    GoogleDrive,
    Dropbox,
    OneDrive,
    PCloud,
    PCloudEurope
}

public sealed class SavedCloudAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RemoteName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public RcloneCloudProvider Provider { get; set; }
    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string LastBrowsePath { get; set; } = string.Empty;

    public string ProviderLabel => Provider switch
    {
        RcloneCloudProvider.GoogleDrive => "Google Drive",
        RcloneCloudProvider.Dropbox => "Dropbox",
        RcloneCloudProvider.OneDrive => "OneDrive",
        RcloneCloudProvider.PCloud => "pCloud ABD",
        RcloneCloudProvider.PCloudEurope => "pCloud Avrupa",
        _ => Provider.ToString()
    };

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName)
        ? ProviderLabel
        : $"{DisplayName} · {ProviderLabel}";
}

public sealed record RcloneCloudProviderOption(
    RcloneCloudProvider Provider,
    string Label,
    string Description)
{
    public override string ToString() => Label;
}

public sealed record RcloneRuntimeStatus(
    bool IsAvailable,
    string Version,
    string Diagnostic);
