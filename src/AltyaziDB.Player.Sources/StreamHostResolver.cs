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
/// Resolves public player/embed pages without starting a browser, WebView or page script.
/// The resolver only reads HTML/JSON text and validates the resulting HLS, DASH or
/// byte-range media endpoint before handing it to libmpv.
/// </summary>
internal sealed class StreamHostResolver
{
    private const int MaximumPageBytes = 2 * 1024 * 1024;
    private const int MaximumManifestBytes = 512 * 1024;
    private const int MaximumPages = 5;
    private const int MaximumCandidates = 20;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 AltyaziDB-Player/0.8";

    private readonly HttpClient _client;
    private readonly IAppLogger _logger;
    private readonly CookieContainer? _cookies;

    public StreamHostResolver(HttpClient client, IAppLogger logger, CookieContainer? cookies)
    {
        _client = client;
        _logger = logger;
        _cookies = cookies;
    }

    public static bool IsSupported(Uri uri) => ProviderFromHost(uri.Host) is not null;

    public async Task<RemoteBrowseResult> ResolveAsync(Uri originalUri, CancellationToken cancellationToken)
    {
        ValidatePublicHttpUri(originalUri);
        var provider = ProviderFromHost(originalUri.Host)
            ?? throw new InvalidOperationException("Bu yayın sağlayıcısı desteklenmiyor.");
        var providerName = ProviderName(provider);
        var extraction = await ExtractAsync(originalUri, provider, cancellationToken).ConfigureAwait(false);

        if (extraction.Candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"{providerName} sayfasında herkese açık bir video akışı bulunamadı. " +
                "Video gizli, süresi dolmuş veya tarayıcı doğrulaması istiyor olabilir.");
        }

        var probeResults = await Task.WhenAll(extraction.Candidates
                .Take(MaximumCandidates)
                .Select(candidate => ProbeAsync(candidate, extraction.Title, cancellationToken)))
            .ConfigureAwait(false);
        var probes = probeResults.OfType<ProbeResult>().ToList();
        var selected = probes
            .OrderBy(item => item.Priority)
            .ThenByDescending(item => item.Size ?? 0)
            .FirstOrDefault();

        if (selected is null)
        {
            throw new InvalidOperationException(
                $"{providerName} bağlantısı çözüldü ancak oynatılabilir HLS/DASH veya ileri sarılabilir video akışı doğrulanamadı.");
        }

        var displayName = string.IsNullOrWhiteSpace(selected.FileName)
            ? extraction.Title
            : selected.FileName;
        if (string.IsNullOrWhiteSpace(displayName)) displayName = providerName + " videosu";

        var request = new RemoteOpenRequest(
            selected.FinalUri.ToString(),
            displayName,
            BuildPlaybackHeaders(selected.FinalUri, selected.Referer),
            providerName,
            originalUri.ToString());
        return new RemoteBrowseResult(
            provider,
            displayName,
            null,
            [],
            request,
            StatusMessage: $"{providerName} {selected.KindLabel} akışı hazır. Video doğrudan açılabilir.");
    }

    private async Task<ExtractionResult> ExtractAsync(
        Uri originalUri,
        RemoteSourceProvider provider,
        CancellationToken cancellationToken)
    {
        var candidates = new List<MediaCandidate>();
        var pages = new Queue<PageRequest>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var title = string.Empty;

        try
        {
            var bootstrap = provider == RemoteSourceProvider.Vk
                ? await GetVkPlayerPayloadAsync(originalUri, cancellationToken).ConfigureAwait(false)
                : null;
            if (bootstrap is not null)
            {
                title = PreferTitle(title, ExtractBootstrapTitle(bootstrap, provider), provider);
                CollectMediaCandidates(bootstrap, candidates);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning($"{ProviderName(provider)} public player verisi alınamadı: {exception.GetType().Name}");
        }

        EnqueueInitialPages(pages, originalUri, provider);
        while (pages.Count > 0 && visited.Count < MaximumPages)
        {
            var pending = pages.Dequeue();
            if (!visited.Add(pending.Uri.AbsoluteUri)) continue;

            HtmlPage page;
            try
            {
                page = await GetPageAsync(pending.Uri, pending.Referer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && visited.Count > 1)
            {
                _logger.Warning($"{ProviderName(provider)} player ara sayfası açılamadı: {exception.GetType().Name}");
                continue;
            }

            title = PreferTitle(title, ExtractTitle(page.Html), provider);
            CollectMediaCandidates(page, candidates);

            foreach (var frame in ExtractPlayerFrames(page))
            {
                if (ProviderFromHost(frame.Host) != provider || visited.Contains(frame.AbsoluteUri)) continue;
                pages.Enqueue(new PageRequest(frame, page.FinalUri));
            }
        }

        return new ExtractionResult(
            CleanTitle(title, ProviderName(provider) + " videosu"),
            Deduplicate(candidates));
    }

    private static void EnqueueInitialPages(
        Queue<PageRequest> pages,
        Uri originalUri,
        RemoteSourceProvider provider)
    {
        if (provider == RemoteSourceProvider.OkRu && TryExtractOkId(originalUri, out var okId))
        {
            pages.Enqueue(new PageRequest(new Uri($"https://ok.ru/videoembed/{Uri.EscapeDataString(okId)}"), originalUri));
            return;
        }

        pages.Enqueue(new PageRequest(originalUri, null));
        if (provider == RemoteSourceProvider.VidMoly && TryExtractLastPathId(originalUri, out var vidMolyId))
        {
            var embed = new UriBuilder(originalUri.Scheme, originalUri.Host)
            {
                Path = $"/embed-{Uri.EscapeDataString(vidMolyId)}.html"
            }.Uri;
            if (embed != originalUri) pages.Enqueue(new PageRequest(embed, originalUri));
        }
        else if (provider == RemoteSourceProvider.Vk && TryExtractVkVideoId(originalUri, out var vkVideoId))
        {
            var parts = vkVideoId.Split('_', 2);
            var vkHost = VkApiHost(originalUri.Host);
            pages.Enqueue(new PageRequest(
                new Uri($"https://{vkHost}/video_ext.php?oid={Uri.EscapeDataString(parts[0])}&id={Uri.EscapeDataString(parts[1])}"),
                originalUri));
        }
    }

    private static void CollectMediaCandidates(HtmlPage page, ICollection<MediaCandidate> candidates)
    {
        foreach (var text in DecodeVariants(page.Html))
        {
            foreach (Match match in Regex.Matches(
                         text,
                         "<(?:source|video)\\b(?<attrs>[^>]*)>",
                         RegexOptions.IgnoreCase | RegexOptions.Singleline,
                         RegexTimeout))
            {
                AddCandidate(page, AttributeValue(match.Groups["attrs"].Value, "src"), "source", candidates);
            }

            foreach (Match match in Regex.Matches(
                         text,
                         "(?:[\"']?(?<label>file|src|source|url(?:\\d+)?|cache\\d+|extra_data|live_mp4|postlive_mp4|videoUrl|downloadUrl|hls(?:_[A-Za-z0-9_]+)?|dash(?:_[A-Za-z0-9_]+)?|hlsManifestUrl|dashManifestUrl|manifest)[\"']?\\s*[:=]\\s*[\"'])(?<value>.*?)(?<!\\\\)[\"']",
                         RegexOptions.IgnoreCase | RegexOptions.Singleline,
                         RegexTimeout))
            {
                AddCandidate(page, match.Groups["value"].Value, match.Groups["label"].Value, candidates);
            }

            foreach (Match match in Regex.Matches(
                         text,
                         "(?<value>https?:\\\\?/\\\\?/[^\"'<>\\s]+)",
                         RegexOptions.IgnoreCase,
                         RegexTimeout))
            {
                AddCandidate(page, match.Groups["value"].Value, "url", candidates);
            }

            CollectDecodedBase64Candidates(page, text, candidates);
        }
    }

    private static void CollectDecodedBase64Candidates(
        HtmlPage page,
        string text,
        ICollection<MediaCandidate> candidates)
    {
        foreach (Match match in Regex.Matches(
                     text,
                     "[\"'](?<value>[A-Za-z0-9+/]{48,}={0,2})[\"']",
                     RegexOptions.None,
                     RegexTimeout))
        {
            try
            {
                var bytes = Convert.FromBase64String(match.Groups["value"].Value);
                if (bytes.Length > MaximumManifestBytes) continue;
                var decoded = Encoding.UTF8.GetString(bytes);
                if (!decoded.Contains("http", StringComparison.OrdinalIgnoreCase) &&
                    !decoded.Contains("m3u8", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Match url in Regex.Matches(
                             decoded,
                             "(?<value>https?://[^\"'<>\\s]+)",
                             RegexOptions.IgnoreCase,
                             RegexTimeout))
                {
                    AddCandidate(page, url.Groups["value"].Value, "base64", candidates);
                }
            }
            catch (FormatException)
            {
                // Not a complete Base64 string. It is intentionally not executed or repaired.
            }
        }
    }

    private static void AddCandidate(
        HtmlPage page,
        string? rawValue,
        string label,
        ICollection<MediaCandidate> candidates)
    {
        if (!TryMakeUri(page.FinalUri, rawValue, out var uri)) return;
        uri = NormalizeMediaUri(uri!);
        if (!LooksLikeMediaUri(uri, label) || IsObviousAdvertisement(uri, label)) return;
        candidates.Add(new MediaCandidate(uri, page.FinalUri, label));
    }

    private async Task<ProbeResult?> ProbeAsync(
        MediaCandidate candidate,
        string fallbackTitle,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            ValidatePublicHttpUri(candidate.Uri);

            var expectedManifest = ManifestKindFromUri(candidate.Uri);
            using var request = CreateRequest(HttpMethod.Get, candidate.Uri, candidate.Referer);
            if (expectedManifest == ManifestKind.None)
            {
                request.Headers.Range = new RangeHeaderValue(0, 1);
                request.Headers.AcceptEncoding.Clear();
                request.Headers.AcceptEncoding.ParseAdd("identity");
            }
            else
            {
                request.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl,application/dash+xml,text/plain;q=0.8,*/*;q=0.4");
            }

            using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? candidate.Uri;
            ValidatePublicHttpUri(finalUri);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var responseManifest = ManifestKindFromResponse(finalUri, contentType);
            if (responseManifest != ManifestKind.None && response.IsSuccessStatusCode)
            {
                var manifest = await ReadLimitedStringAsync(
                        response.Content,
                        MaximumManifestBytes,
                        timeout.Token)
                    .ConfigureAwait(false);
                if (!IsValidManifest(manifest, responseManifest)) return null;
                return new ProbeResult(
                    finalUri,
                    candidate.Referer,
                    fallbackTitle,
                    null,
                    CandidatePriority(candidate.Label, finalUri, responseManifest),
                    responseManifest == ManifestKind.Hls ? "HLS" : "DASH");
            }

            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                range is null ||
                !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                range.From != 0)
            {
                return null;
            }

            var disposition = response.Content.Headers.ContentDisposition;
            var fileName = DecodeFileName(disposition?.FileNameStar)
                ?? DecodeFileName(disposition?.FileName)
                ?? FileNameFromUri(finalUri)
                ?? fallbackTitle;
            return new ProbeResult(
                finalUri,
                candidate.Referer,
                fileName,
                range.Length,
                CandidatePriority(candidate.Label, finalUri, ManifestKind.None),
                "video");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warning("Stream sunucusu doğrulamasında zaman aşımına uğradı.");
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning($"Stream sunucusu doğrulamayı geçemedi: {exception.GetType().Name}");
            return null;
        }
    }

    private async Task<HtmlPage> GetPageAsync(Uri uri, Uri? referer, CancellationToken cancellationToken)
    {
        ValidatePublicHttpUri(uri);
        using var request = CreateRequest(HttpMethod.Get, uri, referer);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.5");
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Player sayfası HTTP {(int)response.StatusCode} yanıtı verdi.");
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        ValidatePublicHttpUri(finalUri);
        var html = await ReadLimitedStringAsync(response.Content, MaximumPageBytes, cancellationToken).ConfigureAwait(false);
        return new HtmlPage(finalUri, html);
    }

    private async Task<HtmlPage?> GetVkPlayerPayloadAsync(Uri originalUri, CancellationToken cancellationToken)
    {
        if (!TryExtractVkVideoId(originalUri, out var videoId)) return null;
        var endpoint = new Uri($"https://{VkApiHost(originalUri.Host)}/al_video.php");
        ValidatePublicHttpUri(endpoint);
        using var request = CreateRequest(HttpMethod.Post, endpoint, originalUri);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("application/json,text/plain;q=0.9,*/*;q=0.5");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["act"] = "show",
            ["al"] = "1",
            ["video"] = videoId
        });
        using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"VK player verisi HTTP {(int)response.StatusCode} yanıtı verdi.");
        var body = await ReadLimitedStringAsync(response.Content, MaximumPageBytes, cancellationToken).ConfigureAwait(false);
        return new HtmlPage(originalUri, body);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, Uri? referer)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("DNT", "1");
        request.Headers.TryAddWithoutValidation("Sec-GPC", "1");
        if (referer is not null)
        {
            request.Headers.Referrer = referer;
            request.Headers.TryAddWithoutValidation("Origin", referer.GetLeftPart(UriPartial.Authority));
        }
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
        if (referer is not null)
        {
            headers["Referer"] = referer.ToString();
            headers["Origin"] = referer.GetLeftPart(UriPartial.Authority);
        }
        var cookie = _cookies?.GetCookieHeader(finalUri);
        if (!string.IsNullOrWhiteSpace(cookie)) headers["Cookie"] = cookie;
        return headers;
    }

    private static IEnumerable<Uri> ExtractPlayerFrames(HtmlPage page)
    {
        foreach (Match match in Regex.Matches(
                     page.Html,
                     "<iframe\\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline,
                     RegexTimeout))
        {
            if (TryMakeUri(page.FinalUri, AttributeValue(match.Groups["attrs"].Value, "src"), out var frame))
                yield return frame!;
        }
    }

    private static IEnumerable<string> DecodeVariants(string value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = value;
        for (var index = 0; index < 4 && seen.Add(current); index++)
        {
            yield return current;
            current = DecodeText(current);
        }
    }

    private static string DecodeText(string value)
    {
        value = WebUtility.HtmlDecode(value)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\x2F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\x3A", ":", StringComparison.OrdinalIgnoreCase)
            .Replace("\\x3D", "=", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(
            value,
            "\\\\u(?<hex>[0-9a-fA-F]{4})",
            match => ((char)Convert.ToInt32(match.Groups["hex"].Value, 16)).ToString(),
            RegexOptions.None,
            RegexTimeout);
    }

    private static IReadOnlyList<MediaCandidate> Deduplicate(IEnumerable<MediaCandidate> candidates) =>
        candidates
            .GroupBy(item => item.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaximumCandidates)
            .ToArray();

    private static bool LooksLikeMediaUri(Uri uri, string label)
    {
        var value = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
        if (new[] { ".m3u8", ".mpd", ".mp4", ".mkv", ".webm", ".m4v", ".mov", ".ts" }
            .Any(value.Contains)) return true;
        return label.Contains("hls", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("dash", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("video", StringComparison.OrdinalIgnoreCase) ||
               label.Length > 3 && label.StartsWith("url", StringComparison.OrdinalIgnoreCase) ||
               label.StartsWith("cache", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("extra_data", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("live_mp4", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("postlive_mp4", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("file", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("source", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObviousAdvertisement(Uri uri, string label)
    {
        var value = uri.Host + " " + uri.AbsolutePath + " " + label;
        return new[] { "doubleclick", "googlesyndication", "popunder", "adservice", "adsterra", "onclick" }
            .Any(item => value.Contains(item, StringComparison.OrdinalIgnoreCase));
    }

    private static Uri NormalizeMediaUri(Uri uri)
    {
        return uri;
    }

    private static ManifestKind ManifestKindFromUri(Uri uri)
    {
        var path = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
        if (path.Contains(".m3u8", StringComparison.Ordinal)) return ManifestKind.Hls;
        if (path.Contains(".mpd", StringComparison.Ordinal)) return ManifestKind.Dash;
        return ManifestKind.None;
    }

    private static ManifestKind ManifestKindFromResponse(Uri uri, string contentType)
    {
        var fromUri = ManifestKindFromUri(uri);
        if (fromUri != ManifestKind.None) return fromUri;
        if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return ManifestKind.Hls;
        if (contentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)) return ManifestKind.Dash;
        return ManifestKind.None;
    }

    private static bool IsValidManifest(string body, ManifestKind kind) => kind switch
    {
        ManifestKind.Hls => body.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase),
        ManifestKind.Dash => body.Contains("<MPD", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static int CandidatePriority(string label, Uri uri, ManifestKind manifest)
    {
        if (manifest == ManifestKind.Hls) return 0;
        if (manifest == ManifestKind.Dash) return 1;
        var value = label + " " + uri.AbsoluteUri;
        if (value.Contains("1080", StringComparison.OrdinalIgnoreCase) || value.Contains("full", StringComparison.OrdinalIgnoreCase)) return 2;
        if (value.Contains("720", StringComparison.OrdinalIgnoreCase) || value.Contains("hd", StringComparison.OrdinalIgnoreCase)) return 3;
        if (value.Contains("480", StringComparison.OrdinalIgnoreCase)) return 5;
        if (value.Contains("360", StringComparison.OrdinalIgnoreCase) || value.Contains("mobile", StringComparison.OrdinalIgnoreCase)) return 6;
        return 4;
    }

    private static string ExtractTitle(string html)
    {
        // Large VK bootstrap responses can contain hundreds of kilobytes of script text.
        // Search tag boundaries first so a malformed page cannot make a broad HTML regex
        // backtrack across the complete response.
        var ogIndex = html.IndexOf("og:title", StringComparison.OrdinalIgnoreCase);
        if (ogIndex >= 0)
        {
            var metaStart = html.LastIndexOf("<meta", ogIndex, StringComparison.OrdinalIgnoreCase);
            var metaEnd = html.IndexOf('>', ogIndex);
            if (metaStart >= 0 && metaEnd > metaStart && metaEnd - metaStart <= 4096)
            {
                var content = AttributeValue(html[metaStart..(metaEnd + 1)], "content");
                if (!string.IsNullOrWhiteSpace(content)) return StripHtml(content);
            }
        }

        var titleStart = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (titleStart < 0) return string.Empty;
        var titleOpenEnd = html.IndexOf('>', titleStart);
        if (titleOpenEnd < 0) return string.Empty;
        var titleEnd = html.IndexOf("</title>", titleOpenEnd + 1, StringComparison.OrdinalIgnoreCase);
        if (titleEnd <= titleOpenEnd || titleEnd - titleOpenEnd > 8192) return string.Empty;
        return StripHtml(html[(titleOpenEnd + 1)..titleEnd]);
    }

    private static string ExtractBootstrapTitle(HtmlPage page, RemoteSourceProvider provider) => ExtractTitle(page.Html);

    private static string CleanTitle(string value, string fallback)
    {
        value = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        foreach (var suffix in new[] { " - VidMoly", " | VidMoly", " - OK.ru", " | VK Video" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) value = value[..^suffix.Length].Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string PreferTitle(string current, string candidate, RemoteSourceProvider provider) =>
        string.IsNullOrWhiteSpace(current) || current.Contains(ProviderName(provider), StringComparison.OrdinalIgnoreCase)
            ? candidate
            : current;

    private static bool TryExtractOkId(Uri uri, out string id)
    {
        id = string.Empty;
        var match = Regex.Match(uri.AbsolutePath, "/(?:videoembed|video|live)/?(?<id>[0-9]+)", RegexOptions.IgnoreCase, RegexTimeout);
        if (!match.Success) return false;
        id = match.Groups["id"].Value;
        return id.Length > 0;
    }

    private static bool TryExtractLastPathId(Uri uri, out string id)
    {
        id = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (id.StartsWith("embed-", StringComparison.OrdinalIgnoreCase)) id = id[6..];
        if (id.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) id = id[..^5];
        return Regex.IsMatch(id, "^[A-Za-z0-9_-]{5,64}$", RegexOptions.None, RegexTimeout);
    }

    private static bool TryExtractVkVideoId(Uri uri, out string videoId)
    {
        videoId = string.Empty;
        var source = Uri.UnescapeDataString(uri.AbsolutePath + uri.Query);
        var match = Regex.Match(
            source,
            "(?:video|clip)(?<id>-?[0-9]+_[0-9]+)",
            RegexOptions.IgnoreCase,
            RegexTimeout);
        if (!match.Success) return false;
        videoId = match.Groups["id"].Value;
        return videoId.Length > 2;
    }

    private static string VkApiHost(string sourceHost)
    {
        sourceHost = sourceHost.ToLowerInvariant();
        if (sourceHost.Equals("vkvideo.ru", StringComparison.Ordinal) ||
            sourceHost.EndsWith(".vkvideo.ru", StringComparison.Ordinal)) return "vkvideo.ru";
        if (sourceHost.Equals("vk.ru", StringComparison.Ordinal) ||
            sourceHost.EndsWith(".vk.ru", StringComparison.Ordinal)) return "vk.ru";
        return "vk.com";
    }

    private static string? AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            $"\\b{Regex.Escape(name)}\\s*=\\s*(?:[\"'](?<value>.*?)[\"']|(?<value>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            RegexTimeout);
        return match.Success ? DecodeText(match.Groups["value"].Value) : null;
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
            if (match.Success) return DecodeText(match.Groups["value"].Value);
        }
        return string.Empty;
    }

    private static bool TryMakeUri(Uri baseUri, string? value, out Uri? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = DecodeText(value).Trim().Trim('"', '\'', '`', ' ', '\\', ',', ';', ')', ']', '}');
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            result = absolute;
        }
        else if (Uri.TryCreate(baseUri, value, out var relative))
        {
            result = relative;
        }
        return result is not null &&
               (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
    }

    private static async Task<string> ReadLimitedStringAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            throw new InvalidOperationException("Player yanıtı güvenli boyut sınırını aşıyor.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidOperationException("Player yanıtı güvenli boyut sınırını aşıyor.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? DecodeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('"');
        if (value.StartsWith("UTF-8''", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    private static string? FileNameFromUri(Uri uri)
    {
        var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static RemoteSourceProvider? ProviderFromHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.Contains("vidmoly", StringComparison.Ordinal)) return RemoteSourceProvider.VidMoly;
        if (host.Equals("ok.ru", StringComparison.Ordinal) || host.EndsWith(".ok.ru", StringComparison.Ordinal)) return RemoteSourceProvider.OkRu;
        if (host.Equals("vk.com", StringComparison.Ordinal) ||
            host.EndsWith(".vk.com", StringComparison.Ordinal) ||
            host.Equals("vk.ru", StringComparison.Ordinal) ||
            host.EndsWith(".vk.ru", StringComparison.Ordinal) ||
            host.Equals("vkvideo.ru", StringComparison.Ordinal) ||
            host.EndsWith(".vkvideo.ru", StringComparison.Ordinal)) return RemoteSourceProvider.Vk;
        return null;
    }

    private static string ProviderName(RemoteSourceProvider provider) => provider switch
    {
        RemoteSourceProvider.VidMoly => "VidMoly",
        RemoteSourceProvider.OkRu => "OK.ru",
        RemoteSourceProvider.Vk => "VK Video",
        _ => "Web video"
    };

    private static void ValidatePublicHttpUri(Uri uri)
    {
        if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("Yalnız HTTP veya HTTPS bağlantıları desteklenir.");
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Yerel ağ adresleri bağlantı çözücüsünde kullanılamaz.");
        if (IPAddress.TryParse(uri.Host, out var address) && IsPrivateAddress(address))
            throw new InvalidOperationException("Özel ağ adresleri bağlantı çözücüsünde kullanılamaz.");
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

    private enum ManifestKind { None, Hls, Dash }
    private sealed record PageRequest(Uri Uri, Uri? Referer);
    private sealed record HtmlPage(Uri FinalUri, string Html);
    private sealed record MediaCandidate(Uri Uri, Uri? Referer, string Label);
    private sealed record ExtractionResult(string Title, IReadOnlyList<MediaCandidate> Candidates);
    private sealed record ProbeResult(
        Uri FinalUri,
        Uri? Referer,
        string FileName,
        long? Size,
        int Priority,
        string KindLabel);
}
