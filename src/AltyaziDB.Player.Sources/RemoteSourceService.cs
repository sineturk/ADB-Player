using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Sources;

public sealed class RemoteSourceService : IRemoteSourceService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".webm", ".mov", ".m4v", ".ts", ".m2ts", ".mts",
        ".mpeg", ".mpg", ".wmv", ".flv", ".ogv", ".vob", ".3gp"
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".truehd", ".flac",
        ".mka", ".m4a", ".mp3", ".ogg", ".opus", ".wav"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z"
    };

    private readonly HttpClient _client;
    private readonly IAppLogger _logger;
    private readonly WebLinkResolver _webLinkResolver;
    private readonly StreamHostResolver _streamHostResolver;
    private readonly PixelDrainLinkResolver _pixelDrainLinkResolver;
    private readonly DirectStreamResolver _directStreamResolver;
    private readonly CookieContainer? _cookies;

    public RemoteSourceService(IAppLogger logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        HttpMessageHandler actualHandler;
        if (handler is null)
        {
            _cookies = new CookieContainer();
            actualHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                CookieContainer = _cookies,
                UseCookies = true,
                MaxAutomaticRedirections = 8
            };
        }
        else
        {
            actualHandler = handler;
        }
        _client = new HttpClient(actualHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AltyaziDB-Player-Windows", "0.5.0"));
        _webLinkResolver = new WebLinkResolver(_client, _logger, _cookies);
        _streamHostResolver = new StreamHostResolver(_client, _logger, _cookies);
        _pixelDrainLinkResolver = new PixelDrainLinkResolver(_client);
        _directStreamResolver = new DirectStreamResolver(_client);
    }

    public async Task<RemoteBrowseResult> ResolvePublicLinkAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var trimmed = url.Trim();

        if (trimmed.StartsWith("pcloud://", StringComparison.OrdinalIgnoreCase))
            return await BrowsePCloudAsync(trimmed, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Geçerli bir HTTP veya HTTPS bağlantısı girin.");

        var host = uri.Host.ToLowerInvariant();
        if (PixelDrainLinkResolver.IsSupported(uri))
            return await _pixelDrainLinkResolver.ResolveAsync(trimmed, cancellationToken).ConfigureAwait(false);
        if (WebLinkResolver.IsSupported(uri))
            return await _webLinkResolver.ResolveAsync(uri, cancellationToken).ConfigureAwait(false);
        if (StreamHostResolver.IsSupported(uri))
            return await _streamHostResolver.ResolveAsync(uri, cancellationToken).ConfigureAwait(false);
        if (IsDropbox(host)) return ResolveDropbox(uri);
        if (IsPCloud(host)) return await BrowsePCloudAsync(trimmed, cancellationToken).ConfigureAwait(false);
        if (IsGoogleDrive(host)) return ResolveGoogleDrive(uri);
        if (IsOneDrive(host)) return ResolveOneDrive(uri);

        return await _directStreamResolver.ResolveAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteBrowseResult> BrowseWebDavAsync(WebDavConnection connection, string? path = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection.BaseUrl);
        if (!Uri.TryCreate(connection.BaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("WebDAV sunucu adresi geçerli değil.");

        var target = string.IsNullOrWhiteSpace(path)
            ? EnsureTrailingSlash(baseUri)
            : new Uri(baseUri, path);
        target = EnsureTrailingSlash(target);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), target);
        request.Headers.TryAddWithoutValidation("Depth", "1");
        request.Headers.Accept.ParseAdd("application/xml, text/xml");
        if (!string.IsNullOrWhiteSpace(connection.Username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        request.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:displayname/><d:getcontentlength/><d:getlastmodified/><d:getcontenttype/><d:resourcetype/></d:prop></d:propfind>",
            Encoding.UTF8,
            "application/xml");

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MultiStatus)
            throw new InvalidOperationException($"WebDAV HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");

        var xmlText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xmlText, LoadOptions.None);
        XNamespace dav = "DAV:";
        var header = BuildBasicHeaders(connection);
        var currentPath = Uri.UnescapeDataString(target.AbsolutePath);
        var items = new List<RemoteSourceItem>();

        foreach (var element in document.Descendants(dav + "response"))
        {
            var href = element.Element(dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            var itemUri = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : new Uri(target, href);
            var itemPath = Uri.UnescapeDataString(itemUri.AbsolutePath);
            if (PathsEqual(itemPath, currentPath)) continue;

            var property = element.Descendants(dav + "prop").FirstOrDefault();
            if (property is null) continue;
            var isFolder = property.Element(dav + "resourcetype")?.Element(dav + "collection") is not null;
            var displayName = WebUtility.HtmlDecode(property.Element(dav + "displayname")?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = Uri.UnescapeDataString(itemUri.Segments.LastOrDefault()?.Trim('/') ?? "Öğe");

            long? size = long.TryParse(property.Element(dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize)
                ? parsedSize
                : null;
            DateTimeOffset? modified = DateTimeOffset.TryParse(property.Element(dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)
                ? parsedDate
                : null;

            items.Add(new RemoteSourceItem(
                itemPath,
                displayName,
                isFolder ? RemoteSourceItemKind.Folder : DetectKind(displayName),
                isFolder ? null : itemUri.ToString(),
                isFolder ? itemPath : null,
                size,
                modified,
                header,
                "WebDAV"));
        }

        items.Sort(ItemComparison);
        var parent = ParentPath(currentPath, baseUri.AbsolutePath);
        return new RemoteBrowseResult(
            RemoteSourceProvider.WebDav,
            target.Host,
            currentPath,
            items,
            ParentPath: parent,
            StatusMessage: $"{items.Count} WebDAV öğesi bulundu.");
    }

    public async Task<byte[]> DownloadAsync(RemoteSourceItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.OpenUrl))
            throw new InvalidOperationException("Seçilen dosyanın indirme adresi yok.");

        using var request = new HttpRequestMessage(HttpMethod.Get, item.OpenUrl);
        ApplyHeaders(request, item.HttpHeaders);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadToFileAsync(
        RemoteSourceItem item,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (string.IsNullOrWhiteSpace(item.OpenUrl))
            throw new InvalidOperationException("Seçilen dosyanın indirme adresi yok.");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var request = new HttpRequestMessage(HttpMethod.Get, item.OpenUrl);
        ApplyHeaders(request, item.HttpHeaders);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private RemoteBrowseResult ResolveDropbox(Uri uri)
    {
        var builder = new UriBuilder(uri);
        var query = ParseQuery(builder.Query);
        query.Remove("dl");
        query["raw"] = "1";
        builder.Query = BuildQuery(query);
        var name = FileNameFromUri(uri, "Dropbox paylaşımı");
        return DirectResult(RemoteSourceProvider.Dropbox, name, builder.Uri.ToString(), "Dropbox doğrudan bağlantısı hazır.");
    }

    private async Task<RemoteBrowseResult> BrowsePCloudAsync(string source, CancellationToken cancellationToken)
    {
        var state = ParsePCloudState(source);
        Exception? lastError = null;
        foreach (var apiHost in state.ApiHosts)
        {
            try
            {
                var showUrl = new UriBuilder(Uri.UriSchemeHttps, apiHost) { Path = "/showpublink", Query = "code=" + Uri.EscapeDataString(state.Code) }.Uri;
                using var document = await GetJsonAsync(showUrl, cancellationToken).ConfigureAwait(false);
                ThrowPCloudError(document.RootElement);
                if (!TryGetProperty(document.RootElement, "metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("pCloud paylaşım bilgisi eksik döndü.");

                var rootName = GetString(metadata, "name") ?? "pCloud paylaşımı";
                var rootIsFolder = GetBoolean(metadata, "isfolder");
                if (!rootIsFolder)
                {
                    var fileId = GetLong(metadata, "fileid");
                    var direct = await GetPCloudDownloadUrlAsync(apiHost, state.Code, fileId, cancellationToken).ConfigureAwait(false);
                    return DirectResult(RemoteSourceProvider.PCloud, rootName, direct, "pCloud dosyası hazır.");
                }

                var rootFolderId = GetLong(metadata, "folderid") ?? 0;
                var flattened = FlattenPCloudEntries(metadata, rootFolderId);
                var selectedFolderId = state.FolderId ?? rootFolderId;
                var selectedFolder = flattened.FirstOrDefault(item => item.IsFolder && item.Id == selectedFolderId);
                var selectedName = selectedFolder?.Name ?? rootName;
                var items = new List<RemoteSourceItem>();

                foreach (var entry in flattened.Where(item => item.ParentId == selectedFolderId))
                {
                    if (entry.IsFolder)
                    {
                        var browse = $"pcloud://{state.Code}/{entry.Id}?api={Uri.EscapeDataString(apiHost)}";
                        items.Add(new RemoteSourceItem(
                            entry.Id.ToString(CultureInfo.InvariantCulture), entry.Name, RemoteSourceItemKind.Folder,
                            null, browse, null, entry.ModifiedUtc, null, "pCloud"));
                    }
                    else
                    {
                        var direct = await GetPCloudDownloadUrlAsync(apiHost, state.Code, entry.Id, cancellationToken).ConfigureAwait(false);
                        items.Add(new RemoteSourceItem(
                            entry.Id.ToString(CultureInfo.InvariantCulture), entry.Name, DetectKind(entry.Name),
                            direct, null, entry.Size, entry.ModifiedUtc, null, "pCloud"));
                    }
                }

                items.Sort(ItemComparison);
                string? parent = null;
                if (selectedFolderId != rootFolderId)
                {
                    var parentId = selectedFolder?.ParentId;
                    parent = parentId is null || parentId == rootFolderId
                        ? $"pcloud://{state.Code}/?api={Uri.EscapeDataString(apiHost)}"
                        : $"pcloud://{state.Code}/{parentId}?api={Uri.EscapeDataString(apiHost)}";
                }

                return new RemoteBrowseResult(
                    RemoteSourceProvider.PCloud,
                    selectedName,
                    $"pcloud://{state.Code}/{(selectedFolderId == rootFolderId ? string.Empty : selectedFolderId.ToString(CultureInfo.InvariantCulture))}?api={Uri.EscapeDataString(apiHost)}",
                    items,
                    ParentPath: parent,
                    StatusMessage: $"{items.Count} pCloud öğesi bulundu.");
            }
            catch (Exception exception)
            {
                lastError = exception;
                _logger.Warning($"pCloud {apiHost} denemesi başarısız: {exception.Message}");
            }
        }

        throw new InvalidOperationException(lastError?.Message ?? "pCloud paylaşımı açılamadı.", lastError);
    }

    private static RemoteBrowseResult ResolveGoogleDrive(Uri uri)
    {
        var id = ExtractGoogleDriveId(uri);
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Google Drive dosya kimliği bağlantıdan çıkarılamadı. Yalnız herkese açık dosya bağlantıları desteklenir.");
        var direct = $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(id)}&export=download&confirm=t";
        var name = FileNameFromUri(uri, "Google Drive dosyası");
        return DirectResult(RemoteSourceProvider.GoogleDrive, name, direct, "Google Drive herkese açık dosya bağlantısı hazır.");
    }

    private static RemoteBrowseResult ResolveOneDrive(Uri uri)
    {
        var builder = new UriBuilder(uri);
        var query = ParseQuery(builder.Query);
        query["download"] = "1";
        builder.Query = BuildQuery(query);
        return DirectResult(RemoteSourceProvider.OneDrive, FileNameFromUri(uri, "OneDrive dosyası"), builder.Uri.ToString(), "OneDrive indirme bağlantısı hazır.");
    }

    private async Task<string> GetPCloudDownloadUrlAsync(string apiHost, string code, long? fileId, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["code"] = code,
            ["forcedownload"] = "0",
            ["skipfilename"] = "0"
        };
        if (fileId is > 0) query["fileid"] = fileId.Value.ToString(CultureInfo.InvariantCulture);
        var builder = new UriBuilder(Uri.UriSchemeHttps, apiHost) { Path = "/getpublinkdownload", Query = BuildQuery(query) };
        using var document = await GetJsonAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        ThrowPCloudError(document.RootElement);
        var path = GetString(document.RootElement, "path");
        if (!TryGetProperty(document.RootElement, "hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() == 0 || string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("pCloud indirme adresi oluşturulamadı.");
        var host = hosts[0].GetString();
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("pCloud indirme sunucusu bulunamadı.");
        return $"https://{host}{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<PCloudEntry> FlattenPCloudEntries(JsonElement root, long rootFolderId)
    {
        var result = new List<PCloudEntry>();

        void Collect(JsonElement folder, long fallbackParentId)
        {
            if (!TryGetProperty(folder, "contents", out var contents) || contents.ValueKind != JsonValueKind.Array) return;
            foreach (var item in contents.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var isFolder = GetBoolean(item, "isfolder");
                var id = isFolder ? GetLong(item, "folderid") : GetLong(item, "fileid");
                if (id is null or <= 0) continue;
                var rawParentId = GetLong(item, "parentfolderid");
                var parentId = rawParentId is > 0 ? rawParentId.Value : fallbackParentId;
                result.Add(new PCloudEntry(
                    id.Value,
                    parentId,
                    GetString(item, "name") ?? "Öğe",
                    isFolder,
                    isFolder ? null : GetLong(item, "size"),
                    ParsePCloudDate(GetString(item, "modified"))));
                if (isFolder) Collect(item, id.Value);
            }
        }

        Collect(root, rootFolderId);
        return result
            .GroupBy(item => (item.Id, item.IsFolder))
            .Select(group => group.First())
            .ToArray();
    }

    private static (string Code, long? FolderId, IReadOnlyList<string> ApiHosts) ParsePCloudState(string source)
    {
        if (source.StartsWith("pcloud://", StringComparison.OrdinalIgnoreCase))
        {
            var custom = new Uri(source);
            var customCode = custom.Host;
            var folderText = custom.AbsolutePath.Trim('/');
            long? customFolderId = long.TryParse(folderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            var api = ParseQuery(custom.Query).GetValueOrDefault("api");
            IReadOnlyList<string> customHosts = string.IsNullOrWhiteSpace(api)
                ? new[] { "api.pcloud.com", "eapi.pcloud.com" }
                : new[] { api };
            return (customCode, customFolderId, customHosts);
        }

        var uri = new Uri(source);
        var query = ParseQuery(uri.Query);
        var code = query.GetValueOrDefault("code");
        if (string.IsNullOrWhiteSpace(code) && uri.Host.Equals("pc.cd", StringComparison.OrdinalIgnoreCase))
            code = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("pCloud paylaşım kodu bulunamadı.");
        var hosts = uri.Host.StartsWith("e.", StringComparison.OrdinalIgnoreCase)
            ? new[] { "eapi.pcloud.com", "api.pcloud.com" }
            : new[] { "api.pcloud.com", "eapi.pcloud.com" };
        return (code, null, hosts);
    }

    private static RemoteBrowseResult DirectResult(RemoteSourceProvider provider, string name, string url, string status) =>
        new(provider, name, null, [], new RemoteOpenRequest(url, name, null, ProviderLabel(provider)), null, status);

    private static string ProviderLabel(RemoteSourceProvider provider) => provider switch
    {
        RemoteSourceProvider.Dropbox => "Dropbox",
        RemoteSourceProvider.PCloud => "pCloud",
        RemoteSourceProvider.GoogleDrive => "Google Drive",
        RemoteSourceProvider.OneDrive => "OneDrive",
        RemoteSourceProvider.GdFlix => "GDFlix",
        RemoteSourceProvider.HubCloud => "HubCloud",
        RemoteSourceProvider.VidMoly => "VidMoly",
        RemoteSourceProvider.OkRu => "OK.ru",
        RemoteSourceProvider.Vk => "VK Video",
        RemoteSourceProvider.PixelDrain => "PixelDrain",
        RemoteSourceProvider.WebDav => "WebDAV",
        _ => "Bağlantı"
    };

    private static IReadOnlyDictionary<string, string>? BuildBasicHeaders(WebDavConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Username)) return null;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}"));
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Basic " + token };
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private static string? ParentPath(string currentPath, string rootPath)
    {
        var current = currentPath.TrimEnd('/');
        var root = rootPath.TrimEnd('/');
        if (current.Equals(root, StringComparison.OrdinalIgnoreCase) || current.Length <= root.Length) return null;
        var slash = current.LastIndexOf('/');
        if (slash <= 0) return null;
        var parent = current[..(slash + 1)];
        return parent.Length < rootPath.Length ? rootPath : parent;
    }

    private static bool PathsEqual(string left, string right) =>
        left.TrimEnd('/').Equals(right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static int ItemComparison(RemoteSourceItem left, RemoteSourceItem right)
    {
        if (left.IsFolder != right.IsFolder) return left.IsFolder ? -1 : 1;
        return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
    }

    private static RemoteSourceItemKind DetectKind(string name)
    {
        var extension = Path.GetExtension(name);
        if (VideoExtensions.Contains(extension)) return RemoteSourceItemKind.Video;
        if (SubtitleExtensions.Contains(extension)) return RemoteSourceItemKind.Subtitle;
        if (AudioExtensions.Contains(extension)) return RemoteSourceItemKind.Audio;
        if (ArchiveExtensions.Contains(extension)) return RemoteSourceItemKind.Archive;
        return RemoteSourceItemKind.Other;
    }

    private static string FileNameFromUri(Uri uri, string fallback)
    {
        var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static bool IsDropbox(string host) => host.EndsWith("dropbox.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("dropboxusercontent.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsPCloud(string host) => host.Equals("pc.cd", StringComparison.OrdinalIgnoreCase) || host.EndsWith("pcloud.link", StringComparison.OrdinalIgnoreCase) || host.EndsWith("pcloud.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsGoogleDrive(string host) => host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase) || host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsOneDrive(string host) => host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase) || host.Contains("onedrive", StringComparison.OrdinalIgnoreCase) || host.EndsWith("sharepoint.com", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractGoogleDriveId(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
            if (segments[index].Equals("d", StringComparison.OrdinalIgnoreCase)) return segments[index + 1];
        return ParseQuery(uri.Query).GetValueOrDefault("id");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            result[Uri.UnescapeDataString(pair[0])] = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
        }
        return result;
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return false;
        return value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n == 1) || value.ToString() == "1";
    }

    private static DateTimeOffset? ParsePCloudDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static void ThrowPCloudError(JsonElement root)
    {
        var result = GetLong(root, "result") ?? 0;
        if (result == 0) return;
        var error = GetString(root, "error");
        throw new InvalidOperationException(result switch
        {
            7001 => "pCloud paylaşım bağlantısı geçersiz.",
            7002 => "pCloud paylaşım bağlantısı silinmiş.",
            7004 => "pCloud paylaşım bağlantısının süresi dolmuş.",
            7005 => "pCloud paylaşım bağlantısının trafik sınırı dolmuş.",
            7006 => "pCloud paylaşım bağlantısının indirme sınırı dolmuş.",
            _ => string.IsNullOrWhiteSpace(error) ? $"pCloud hata kodu: {result}" : error
        });
    }

    private sealed record PCloudEntry(
        long Id,
        long ParentId,
        string Name,
        bool IsFolder,
        long? Size,
        DateTimeOffset? ModifiedUtc);

    public void Dispose() => _client.Dispose();
}
