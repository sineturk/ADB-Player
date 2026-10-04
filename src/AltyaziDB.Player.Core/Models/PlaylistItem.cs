namespace AltyaziDB.Player.Core.Models;

public sealed record PlaylistItem(
    string Source,
    string Title,
    bool IsLocal,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    string? Provider = null)
{
    public string KindLabel => IsLocal ? "Yerel" : string.IsNullOrWhiteSpace(Provider) ? "Bağlantı" : Provider;
    public string DisplaySource
    {
        get
        {
            if (IsLocal) return Source;
            if (!string.IsNullOrWhiteSpace(Provider)) return Provider;
            return Uri.TryCreate(Source, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.Host
                : "Bağlantı";
        }
    }
}
