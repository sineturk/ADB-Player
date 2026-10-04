using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Sources;

/// <summary>
/// Resolves supported sharing pages without running their scripts, ads, frames or pop-ups.
/// Only the HTML/JSON needed to find a media endpoint is requested.
/// </summary>
internal sealed class WebLinkResolver
{
    private const int MaximumHtmlBytes = 2 * 1024 * 1024;
    private const int MaximumCandidates = 8;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 AltyaziDB-Player/0.8";

    private readonly HttpClient _client;
    private readonly IAppLogger _logger;
    private readonly CookieContainer? _cookies;

    public WebLinkResolver(HttpClient client, IAppLogger logger, CookieContainer? cookies)
    {
        _client = client;
        _logger = logger;
        _cookies = cookies;
    }

    public static bool IsSupported(Uri uri) => IsGdFlix(uri.Host) || IsHubCloud(uri.Host);

    public async Task<RemoteBrowseResult> ResolveAsync(Uri originalUri, CancellationToken cancellationToken)
    {
        ValidatePublicHttpUri(originalUri);
        var provider = IsGdFlix(originalUri.Host)
            ? RemoteSourceProvider.GdFlix
            : RemoteSourceProvider.HubCloud;
        var providerName = ProviderName(provider);

        var extraction = provider == RemoteSourceProvider.GdFlix
            ? await ResolveGdFlixAsync(originalUri, cancellationToken).ConfigureAwait(false)
            : await ResolveHubCloudAsync(originalUri, cancellationToken).ConfigureAwait(false);

        if (extraction.Candidates.Count == 0)
            throw new InvalidOperationException($"{providerName} sayfasında kullanılabilir bir medya sunucusu bulunamadı.");

        var probeResults = await Task.WhenAll(extraction.Candidates
            .Take(MaximumCandidates)
            .Select(candidate => ProbeRangeAsync(candidate, extraction.Title, cancellationToken)))
            .ConfigureAwait(false);
        var probes = probeResults.OfType<ProbeResult>().ToList();

        var selected = probes
            .OrderBy(item => item.Priority)
            .ThenByDescending(item => item.Size ?? 0)
            .FirstOrDefault();
        if (selected is null)
        {
            throw new InvalidOperationException(
                "Bağlantı çözüldü ancak sunucular byte-range ile ileri sarmayı desteklemedi. " +
                "Sayfayı tarayıcıda bir kez yenileyip yeni paylaşım bağlantısıyla tekrar deneyin.");
        }

        var headers = BuildPlaybackHeaders(selected.FinalUri, selected.Referer);
        var displayName = string.IsNullOrWhiteSpace(selected.FileName)
            ? extraction.Title
            : selected.FileName;
        if (string.IsNullOrWhiteSpace(displayName)) displayName = providerName + " videosu";

        var request = new RemoteOpenRequest(
            selected.FinalUri.ToString(),
            displayName,
            headers,
            providerName,
            originalUri.ToString());
        return new RemoteBrowseResult(
            provider,
            displayName,
            null,
            [],
            request,
            StatusMessage: $"{providerName} akışı hazır. İleri sarma doğrulandı.");
    }

    private async Task<ExtractionResult> ResolveHubCloudAsync(Uri startUri, CancellationToken cancellationToken)
    {
        var candidates = new List<WebCandidate>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = startUri;
        Uri? referer = null;
        var title = string.Empty;

        for (var depth = 0; depth < 4; depth++)
        {
            if (!visited.Add(current.AbsoluteUri)) break;
            var page = await GetPageAsync(current, referer, cancellationToken).ConfigureAwait(false);
            title = PreferTitle(title, ExtractTitle(page.Html));
            CollectDownloadCandidates(page, candidates);

            var next = ExtractJavascriptUrl(page)
                ?? ExtractAnchor(page, static (id, text, href) =>
                    id.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("generate direct", StringComparison.OrdinalIgnoreCase) ||
                    href.Contains("hubcloud.php", StringComparison.OrdinalIgnoreCase));
            if (next is null || candidates.Any(item => LooksLikeMediaEndpoint(item.Uri))) break;
            referer = page.FinalUri;
            current = next;
        }

        return new ExtractionResult(CleanTitle(title, "HubCloud videosu"), Deduplicate(candidates));
    }

    private async Task<ExtractionResult> ResolveGdFlixAsync(Uri startUri, CancellationToken cancellationToken)
    {
        var initial = await GetPageAsync(startUri, null, cancellationToken).ConfigureAwait(false);
        var title = CleanTitle(ExtractTitle(initial.Html), "GDFlix videosu");
        var key = FirstMatch(initial.Html,
            "formData\\.append\\(\\s*[\"']key[\"']\\s*,\\s*[\"'](?<value>[^\"']+)",
            "[?&]token=(?<value>[A-Za-z0-9._~-]+)");

        var pages = new List<HtmlPage> { initial };
        if (!string.IsNullOrWhiteSpace(key))
        {
            var directPage = await TryGdFlixDirectActionAsync(initial.FinalUri, key, cancellationToken).ConfigureAwait(false);
            if (directPage is not null) pages.Add(directPage);

            var tokenUri = AddQuery(initial.FinalUri, "token", key);
            var tokenPage = await TryGetPageAsync(tokenUri, initial.FinalUri, cancellationToken).ConfigureAwait(false);
            if (tokenPage is not null) pages.Add(tokenPage);
        }

        var candidates = new List<WebCandidate>();
        foreach (var page in pages)
        {
            title = PreferTitle(title, ExtractTitle(page.Html));
            CollectDownloadCandidates(page, candidates);
        }

        // GDFlix commonly hands the last step to HubCloud. Resolve that HTML as well,
        // but never open a browser or execute page JavaScript.
        foreach (var hub in candidates
                     .Where(item => IsHubCloud(item.Uri.Host))
                     .Select(item => item.Uri)
                     .DistinctBy(item => item.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                     .Take(3)
                     .ToArray())
        {
            try
            {
                var nested = await ResolveHubCloudCandidatesAsync(hub, cancellationToken).ConfigureAwait(false);
                title = PreferTitle(title, nested.Title);
                candidates.AddRange(nested.Candidates);
            }
            catch (Exception exception)
            {
                _logger.Warning($"GDFlix ara sunucusu çözülemedi: {exception.GetType().Name}");
            }
        }

        return new ExtractionResult(CleanTitle(title, "GDFlix videosu"), Deduplicate(candidates));
    }

    private async Task<ExtractionResult> ResolveHubCloudCandidatesAsync(Uri startUri, CancellationToken cancellationToken)
    {
        var candidates = new List<WebCandidate>();
        var page = await GetPageAsync(startUri, null, cancellationToken).ConfigureAwait(false);
        var title = ExtractTitle(page.Html);
        CollectDownloadCandidates(page, candidates);
        var next = ExtractJavascriptUrl(page)
            ?? ExtractAnchor(page, static (id, text, href) =>
                id.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("generate direct", StringComparison.OrdinalIgnoreCase));
        if (next is not null)
        {
            var downloadPage = await GetPageAsync(next, page.FinalUri, cancellationToken).ConfigureAwait(false);
            title = PreferTitle(title, ExtractTitle(downloadPage.Html));
            CollectDownloadCandidates(downloadPage, candidates);
        }
        return new ExtractionResult(CleanTitle(title, "HubCloud videosu"), Deduplicate(candidates));
    }

    private async Task<HtmlPage?> TryGdFlixDirectActionAsync(Uri pageUri, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, pageUri, pageUri);
            request.Headers.TryAddWithoutValidation("x-token", pageUri.Host);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "direct",
                ["key"] = key,
                ["action_token"] = string.Empty
            });
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await ReadLimitedStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? pageUri;

            if (TryExtractJsonUrl(body, out var resultUri))
            {
                var encoded = WebUtility.HtmlEncode(resultUri!.AbsoluteUri);
                return new HtmlPage(finalUri, $"<a id=\"gdlink\" href=\"{encoded}\">Direct download</a>", pageUri);
            }
            return new HtmlPage(finalUri, body, pageUri);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning($"GDFlix doğrudan bağlantı adımı yanıt vermedi: {exception.GetType().Name}");
            return null;
        }
    }

    private async Task<ProbeResult?> ProbeRangeAsync(WebCandidate candidate, string fallbackTitle, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            ValidatePublicHttpUri(candidate.Uri);
            using var request = CreateRequest(HttpMethod.Get, candidate.Uri, candidate.Referer);
            request.Headers.Range = new RangeHeaderValue(0, 1);
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? candidate.Uri;
            ValidatePublicHttpUri(finalUri);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                range is null ||
                !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                range.From != 0)
            {
                return null;
            }

            var contentDisposition = response.Content.Headers.ContentDisposition;
            var fileName = DecodeFileName(contentDisposition?.FileNameStar)
                ?? DecodeFileName(contentDisposition?.FileName)
                ?? FileNameFromUri(finalUri)
                ?? fallbackTitle;
            return new ProbeResult(
                finalUri,
                candidate.Referer,
                fileName,
                range.Length,
                CandidatePriority(candidate.Label, finalUri.Host));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warning("Web medya sunucusu Range doğrulamasında zaman aşımına uğradı.");
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning($"Web medya sunucusu Range doğrulamasını geçemedi: {exception.GetType().Name}");
            return null;
        }
    }

    private async Task<HtmlPage> GetPageAsync(Uri uri, Uri? referer, CancellationToken cancellationToken)
    {
        ValidatePublicHttpUri(uri);
        using var request = CreateRequest(HttpMethod.Get, uri, referer);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.6");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bağlantı sayfası HTTP {(int)response.StatusCode} yanıtı verdi.");
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        ValidatePublicHttpUri(finalUri);
        var html = await ReadLimitedStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return new HtmlPage(finalUri, html, referer);
    }

    private async Task<HtmlPage?> TryGetPageAsync(Uri uri, Uri? referer, CancellationToken cancellationToken)
    {
        try { return await GetPageAsync(uri, referer, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning($"Web bağlantısı ara adımı açılamadı: {exception.GetType().Name}");
            return null;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, Uri? referer)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("DNT", "1");
        request.Headers.TryAddWithoutValidation("Sec-GPC", "1");
        if (referer is not null) request.Headers.Referrer = referer;
        return request;
    }

    private IReadOnlyDictionary<string, string> BuildPlaybackHeaders(Uri finalUri, Uri? referer)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["DNT"] = "1",
            ["Sec-GPC"] = "1"
        };
        if (referer is not null) headers["Referer"] = referer.ToString();
        var cookie = _cookies?.GetCookieHeader(finalUri);
        if (!string.IsNullOrWhiteSpace(cookie)) headers["Cookie"] = cookie;
        return headers;
    }

    private static void CollectDownloadCandidates(HtmlPage page, ICollection<WebCandidate> candidates)
    {
        foreach (Match match in Regex.Matches(page.Html, "<a\\b(?<attrs>[^>]*)>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout))
        {
            var attributes = match.Groups["attrs"].Value;
            var href = AttributeValue(attributes, "href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            var id = AttributeValue(attributes, "id") ?? string.Empty;
            var text = StripHtml(match.Groups["text"].Value);
            if (!LooksLikeDownloadLink(id, text, href)) continue;
            if (!TryMakeUri(page.FinalUri, href, out var uri)) continue;
            uri = NormalizeCandidateUri(uri!);
            if (IsBlockedScheme(uri) || IsObviousAdvertisement(uri, text)) continue;
            candidates.Add(new WebCandidate(uri, page.FinalUri, text));
        }

        foreach (var variable in new[] { "url", "pxl", "downloadUrl", "directLink" })
        {
            var value = FirstMatch(page.Html, $"(?:var|let|const)\\s+{Regex.Escape(variable)}\\s*=\\s*[\"'](?<value>[^\"']+)");
            if (!TryMakeUri(page.FinalUri, value, out var uri)) continue;
            uri = NormalizeCandidateUri(uri!);
            if (!IsBlockedScheme(uri)) candidates.Add(new WebCandidate(uri, page.FinalUri, variable));
        }
    }

    private static Uri? ExtractJavascriptUrl(HtmlPage page)
    {
        var value = FirstMatch(page.Html,
            "(?:var|let|const)\\s+url\\s*=\\s*[\"'](?<value>[^\"']+)",
            "window\\.location(?:\\.href)?\\s*=\\s*[\"'](?<value>[^\"']+)");
        return TryMakeUri(page.FinalUri, value, out var result) ? result : null;
    }

    private static Uri? ExtractAnchor(HtmlPage page, Func<string, string, string, bool> predicate)
    {
        foreach (Match match in Regex.Matches(page.Html, "<a\\b(?<attrs>[^>]*)>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout))
        {
            var attributes = match.Groups["attrs"].Value;
            var href = AttributeValue(attributes, "href") ?? string.Empty;
            var id = AttributeValue(attributes, "id") ?? string.Empty;
            var text = StripHtml(match.Groups["text"].Value);
            if (predicate(id, text, href) && TryMakeUri(page.FinalUri, href, out var result)) return result;
        }
        return null;
    }

    private static bool LooksLikeDownloadLink(string id, string text, string href)
    {
        var joined = string.Join(' ', id, text, href);
        return id.Equals("gdlink", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("download", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("server", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("direct", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("fsl", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("pixeldrain", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("hubcdn", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("r2.dev", StringComparison.OrdinalIgnoreCase) ||
               LooksLikeMediaPath(href);
    }

    private static bool LooksLikeMediaEndpoint(Uri uri) =>
        LooksLikeMediaPath(uri.AbsolutePath) ||
        uri.Host.Contains("pixeldrain", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Contains("hubcdn", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith("r2.dev", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeMediaPath(string value)
    {
        var clean = value.Split('?', '#')[0];
        return new[] { ".mkv", ".mp4", ".webm", ".avi", ".mov", ".m4v", ".m2ts", ".ts" }
            .Any(extension => clean.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static Uri NormalizeCandidateUri(Uri uri)
    {
        if (uri.Host.Contains("pixeldrain.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/u/", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath[3..].Trim('/');
            if (!string.IsNullOrWhiteSpace(id)) return new Uri($"https://pixeldrain.com/api/file/{Uri.EscapeDataString(id)}?download");
        }
        return uri;
    }

    private static bool IsObviousAdvertisement(Uri uri, string label)
    {
        var value = uri.Host + " " + label;
        return value.Contains("doubleclick", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("popunder", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("advert", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("onclick", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedScheme(Uri uri) => uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps;

    private static IReadOnlyList<WebCandidate> Deduplicate(IEnumerable<WebCandidate> candidates) =>
        candidates
            .Where(item => !IsBlockedScheme(item.Uri))
            .GroupBy(item => item.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaximumCandidates)
            .ToArray();

    private static int CandidatePriority(string label, string host)
    {
        var text = label + " " + host;
        if (text.Contains("fsl", StringComparison.OrdinalIgnoreCase)) return 0;
        if (text.Contains("r2.dev", StringComparison.OrdinalIgnoreCase) || text.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)) return 1;
        if (text.Contains("pixel", StringComparison.OrdinalIgnoreCase)) return 2;
        if (text.Contains("10gbps", StringComparison.OrdinalIgnoreCase)) return 8;
        return 4;
    }

    private static string ExtractTitle(string html)
    {
        var value = FirstMatch(html,
            "<h[1-5][^>]*(?:class=[\"'][^\"']*(?:card-header|text-center)[^\"']*[\"'])?[^>]*>(?<value>.*?)</h[1-5]>",
            "<title[^>]*>(?<value>.*?)</title>");
        return StripHtml(value);
    }

    private static string CleanTitle(string value, string fallback)
    {
        value = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        foreach (var suffix in new[] { " - HubCloud", " | HubCloud", " - GDFlix", " | GDFlix" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) value = value[..^suffix.Length].Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string PreferTitle(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) || current.Contains("HubCloud", StringComparison.OrdinalIgnoreCase) || current.Contains("GDFlix", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : current;

    private static string? AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(attributes, $"\\b{Regex.Escape(name)}\\s*=\\s*(?:[\"'](?<value>.*?)[\"']|(?<value>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    private static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ", RegexOptions.Singleline, RegexTimeout))
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string FirstMatch(string input, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            if (match.Success) return WebUtility.HtmlDecode(match.Groups["value"].Value).Replace("\\/", "/", StringComparison.Ordinal);
        }
        return string.Empty;
    }

    private static bool TryMakeUri(Uri baseUri, string? value, out Uri? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
        value = WebUtility.HtmlDecode(value).Replace("\\/", "/", StringComparison.Ordinal).Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && !IsBlockedScheme(absolute))
            result = absolute;
        else
            result = Uri.TryCreate(baseUri, value, out var relative) ? relative : null;
        return result is not null && !IsBlockedScheme(result);
    }

    private static bool TryExtractJsonUrl(string body, out Uri? uri)
    {
        uri = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("url", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String) continue;
                return Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out uri) && !IsBlockedScheme(uri!);
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static Uri AddQuery(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        var separator = string.IsNullOrWhiteSpace(builder.Query) ? string.Empty : builder.Query.TrimStart('?') + "&";
        builder.Query = separator + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        return builder.Uri;
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumHtmlBytes)
            throw new InvalidOperationException("Bağlantı sayfası güvenli boyut sınırını aşıyor.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumHtmlBytes)
                throw new InvalidOperationException("Bağlantı sayfası güvenli boyut sınırını aşıyor.");
            buffer.Write(chunk, 0, read);
        }
        var encoding = TryGetEncoding(content.Headers.ContentType?.CharSet) ?? Encoding.UTF8;
        return encoding.GetString(buffer.ToArray());
    }

    private static Encoding? TryGetEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return Encoding.GetEncoding(name.Trim(' ', '\"', '\'')); }
        catch (ArgumentException) { return null; }
    }

    private static string? DecodeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('\"');
        if (value.StartsWith("UTF-8''", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    private static string? FileNameFromUri(Uri uri)
    {
        var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static void ValidatePublicHttpUri(Uri uri)
    {
        if (IsBlockedScheme(uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("Yalnız HTTP veya HTTPS bağlantıları desteklenir.");
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Yerel ağ adresleri web bağlantısı çözücüsünde kullanılamaz.");
        if (IPAddress.TryParse(uri.Host, out var address) && IsPrivateAddress(address))
            throw new InvalidOperationException("Özel ağ adresleri web bağlantısı çözücüsünde kullanılamaz.");
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Any)) return true;
            var ipv6 = address.GetAddressBytes();
            return (ipv6[0] & 0xFE) == 0xFC;
        }
        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               bytes[0] == 0 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }

    private static bool IsGdFlix(string host) => host.Contains("gdflix", StringComparison.OrdinalIgnoreCase);
    private static bool IsHubCloud(string host) => host.Contains("hubcloud", StringComparison.OrdinalIgnoreCase);
    private static string ProviderName(RemoteSourceProvider provider) => provider == RemoteSourceProvider.GdFlix ? "GDFlix" : "HubCloud";

    private sealed record HtmlPage(Uri FinalUri, string Html, Uri? Referer);
    private sealed record WebCandidate(Uri Uri, Uri? Referer, string Label);
    private sealed record ExtractionResult(string Title, IReadOnlyList<WebCandidate> Candidates);
    private sealed record ProbeResult(Uri FinalUri, Uri? Referer, string FileName, long? Size, int Priority);
}
