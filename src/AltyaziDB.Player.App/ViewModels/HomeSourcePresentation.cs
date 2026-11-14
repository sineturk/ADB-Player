using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.ViewModels;

/// <summary>Display-only home source tile. The original account is never copied or merged.</summary>
public sealed record HomeCloudTile(
    SavedCloudAccount Account,
    string DisplayName,
    string ProviderLabel,
    string RuntimeStatus,
    string ProviderGlyph);

/// <summary>Display-only shortcut for either a saved public link or a WebDAV profile.</summary>
public sealed record HomeConnectionTile(
    SavedPublicLink? Link,
    SavedWebDavProfile? WebDav,
    string DisplayName,
    string ProviderLabel,
    string StatusLabel,
    string ProviderGlyph);

/// <summary>
/// Builds privacy-safe labels for Home. It never uses a saved URL as a visible tooltip.
/// No connectivity check is implied by the "stored" or rclone runtime status labels.
/// </summary>
public static class HomeSourcePresentation
{
    private static readonly Regex EmbeddedUrl = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int MaxLabelLength = 43;

    public static HomeCloudTile Cloud(SavedCloudAccount account, bool hasDuplicateName,
        string runtimeStatus)
    {
        var label = SafeLabel(account.DisplayName, account.ProviderLabel);
        if (hasDuplicateName)
            label = Trim(label, 34) + " · " + StableSuffix(account.Id, account.RemoteName);

        // rclone availability is global; never claim this particular account was probed.
        return new HomeCloudTile(account, label, account.ProviderLabel,
            runtimeStatus, CloudGlyph(account.Provider));
    }

    public static HomeConnectionTile Link(SavedPublicLink link, string storedText)
    {
        var provider = SafeProvider(link.Provider, link.Url);
        var name = SafeLabel(link.Name, provider);
        return new HomeConnectionTile(link, null, name, provider,
            storedText, "\uE71B");
    }

    public static HomeConnectionTile WebDav(SavedWebDavProfile profile, string storedText)
    {
        var name = SafeLabel(profile.Name, "WebDAV");
        return new HomeConnectionTile(null, profile, name, "WebDAV",
            storedText, "\uE8B7");
    }

    public static string StableAccountLabel(SavedCloudAccount account)
    {
        var name = SafeLabel(account.DisplayName, account.ProviderLabel);
        return Trim(name, 30) + " · " + StableSuffix(account.Id, account.RemoteName);
    }

    public static string SafeLabel(string? value, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Trim(fallback ?? "—", MaxLabelLength);

        var raw = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        var embedded = EmbeddedUrl.Match(raw);
        if (embedded.Success && Uri.TryCreate(embedded.Value, UriKind.Absolute, out var embeddedUri))
            return Trim(embeddedUri.IdnHost, MaxLabelLength);

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "https" or "http")
                return Trim(uri.IdnHost, MaxLabelLength);

            return Trim(fallback ?? "Kaynak", MaxLabelLength);
        }

        // A text masquerading as a URL/path must not leak a token, query or path.
        if (raw.Contains("://", StringComparison.Ordinal)
            || raw.StartsWith("\\\\", StringComparison.Ordinal)
            || raw.Contains("?token=", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("?key=", StringComparison.OrdinalIgnoreCase))
            return Trim(fallback ?? "Kaynak", MaxLabelLength);

        var visible = raw.Split('?', '#')[0].Trim();
        return visible.Length == 0
            ? Trim(fallback ?? "Kaynak", MaxLabelLength)
            : Trim(visible, MaxLabelLength);
    }

    private static string SafeProvider(string? provider, string? url)
    {
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var known = provider.Trim().ToLowerInvariant();
            if (known.Contains("google", StringComparison.Ordinal)) return "Google Drive";
            if (known.Contains("gofile", StringComparison.Ordinal)) return "Gofile";
            if (known.Contains("pcloud", StringComparison.Ordinal)) return "pCloud";
            if (known.Contains("dropbox", StringComparison.Ordinal)) return "Dropbox";
            if (known.Contains("onedrive", StringComparison.Ordinal)) return "OneDrive";
            if (known.Contains("webdav", StringComparison.Ordinal)) return "WebDAV";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "https" or "http")
        {
            var host = parsed.IdnHost.ToLowerInvariant();
            if (host == "drive.google.com" || host.EndsWith(".google.com", StringComparison.Ordinal))
                return "Google Drive";
            if (host == "gofile.io" || host.EndsWith(".gofile.io", StringComparison.Ordinal))
                return "Gofile";
            if (host == "pcloud.com" || host.EndsWith(".pcloud.com", StringComparison.Ordinal)
                || host == "pcloud.link" || host.EndsWith(".pcloud.link", StringComparison.Ordinal))
                return "pCloud";
            return Trim(host, 30);
        }
        return "Web";
    }

    private static string CloudGlyph(RcloneCloudProvider provider) => provider switch
    {
        RcloneCloudProvider.GoogleDrive => "\uE753",
        RcloneCloudProvider.Dropbox => "\uE8B7",
        RcloneCloudProvider.OneDrive => "\uE753",
        RcloneCloudProvider.PCloud => "\uE753",
        RcloneCloudProvider.PCloudEurope => "\uE753",
        _ => "\uE753"
    };

    private static string StableSuffix(string? accountId, string? remoteName)
    {
        // IDs are generated locally, not credentials. Show only six hex chars.
        // Legacy entries with no ID get a stable fingerprint; never expose remoteName.
        var source = !string.IsNullOrWhiteSpace(accountId) ? accountId : remoteName ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash.AsSpan(0, 3)).ToLowerInvariant();
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
