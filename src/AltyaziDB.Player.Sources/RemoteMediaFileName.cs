using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Selects a real, display-safe media filename. IDs, Google confirmation
/// endpoints and CDN path fragments are not release titles.
/// This class never performs network requests or logs URL/query parameters.
/// </summary>
internal static class RemoteMediaFileName
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".webm", ".mov", ".m4v", ".ts", ".m2ts",
        ".mts", ".mpeg", ".mpg", ".wmv", ".flv", ".ogv", ".vob", ".3gp"
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    internal static string? FromContentDisposition(ContentDispositionHeaderValue? disposition) =>
        Clean(disposition?.FileNameStar) ?? Clean(disposition?.FileName);

    internal static string? FromUrlPath(Uri uri) =>
        Clean(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? string.Empty));

    internal static string? Clean(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var name = WebUtility.HtmlDecode(input).Trim().Trim('"', '\'', ' ');
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.Length is < 5 or > 255 || name.Any(char.IsControl)) return null;
        if (name.Contains('<') || name.Contains('>') || name.Contains('?') || name.Contains('*'))
            return null;

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || !MediaExtensions.Contains(name[dot..])) return null;
        return name;
    }

    internal static string? FromGoogleDriveHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        foreach (Match meta in Regex.Matches(html, @"<meta\b[^>]{1,1500}>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout))
        {
            var tag = meta.Value;
            var kind = Attribute(tag, "property") ?? Attribute(tag, "name")
                ?? Attribute(tag, "itemprop");
            if (kind is null || !(kind.Equals("og:title", StringComparison.OrdinalIgnoreCase)
                 || kind.Equals("title", StringComparison.OrdinalIgnoreCase)
                 || kind.Equals("name", StringComparison.OrdinalIgnoreCase)))
                continue;
            var name = Clean(Attribute(tag, "content"));
            if (name is not null) return name;
        }

        // Google's public preview sometimes has "filename.mkv - Google Drive"
        // in <title>; confirmation screens usually have no filename and fail safely.
        var title = Regex.Match(html, @"<title\b[^>]*>(?<name>[^<]{1,500})</title\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        if (title.Success)
        {
            var name = WebUtility.HtmlDecode(title.Groups["name"].Value).Trim();
            foreach (var suffix in new[] { " - Google Drive", " — Google Drive", " – Google Drive" })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    name = name[..^suffix.Length].Trim();
            var clean = Clean(name);
            if (clean is not null) return clean;
        }
        return null;
    }

    private static string? Attribute(string tag, string attribute)
    {
        var found = Regex.Match(tag,
            @"\b" + Regex.Escape(attribute) + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        return found.Success ? found.Groups["v"].Value : null;
    }
}
