using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using AltyaziDB.Player.Connections.Models;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Parsing;

namespace AltyaziDB.Player.Connections.Services;

public sealed class ExternalConnectionService : IExternalConnectionService
{
    private const int MaximumResponseBytes = 5 * 1024 * 1024;
    private const int MaximumTorrentBytes = 8 * 1024 * 1024;
    private const string SecretPrefix = "external.connection.";

    private readonly ConnectionRegistryStore _registry;
    private readonly ISecretStore _secrets;
    private readonly IAppLogger _logger;
    private readonly HttpClient _http;
    private readonly ReleaseParser _releaseParser = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    public ExternalConnectionService(string registryFilePath, ISecretStore secrets, IAppLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryFilePath);
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registry = new ConnectionRegistryStore(registryFilePath, logger);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AltyaziDB-Player/0.10.3.2");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml", 0.8));
    }

    public Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _registry.LoadAsync(cancellationToken);
    }

    public async Task<ConnectionConfiguration?> GetConfigurationAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var profile = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return null;
        }

        var payload = await LoadPayloadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        return payload is null
            ? null
            : new ConnectionConfiguration(
                profile.Id,
                profile.Name,
                profile.Type,
                payload.BaseUrl,
                !string.IsNullOrWhiteSpace(payload.ApiKey),
                payload.MovieCategories,
                payload.SeriesCategories,
                payload.AnimeCategories,
                payload.OtherCategories,
                payload.ResultLimit,
                payload.AllowLocalHttp);
    }

    public async Task<ConnectionTestResult> TestDraftAsync(
        SaveConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            ValidateName(request.Name);
            var payload = await ResolvePayloadAsync(request, cancellationToken).ConfigureAwait(false);
            ValidatePayload(request.Type, payload);
            return await TestPayloadAsync(request.Type, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Harici bağlantı testi başarısız oldu.", exception);
            return new ConnectionTestResult(false, ToSafeFailureMessage(exception));
        }
    }

    public async Task<ConnectionTestResult> TestAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = profiles.FindIndex(item => item.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return new ConnectionTestResult(false, "Bağlantı kaydı bulunamadı.");
            }

            var profile = profiles[index];
            var payload = await LoadPayloadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return new ConnectionTestResult(false, "Bağlantının şifreli ayarları bulunamadı.");
            }

            ConnectionTestResult result;
            try
            {
                ValidatePayload(profile.Type, payload);
                result = await TestPayloadAsync(profile.Type, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error("Kayıtlı harici bağlantı testi başarısız oldu.", exception);
                result = new ConnectionTestResult(false, ToSafeFailureMessage(exception));
            }

            profiles[index] = profile with
            {
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastTestUtc = DateTimeOffset.UtcNow,
                LastTestSucceeded = result.IsSuccess,
                LastVersion = result.Version,
                LastItemCount = result.ItemCount,
                LastMessage = result.Message
            };
            await _registry.SaveAsync(profiles, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<ConnectionProfile> SaveAsync(
        SaveConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!request.LegalNoticeAccepted || !request.UserOwnsServiceConfirmed)
        {
            throw new InvalidOperationException("Bağlantıyı kaydetmek için kullanım ve yetki onaylarını kabul edin.");
        }

        ValidateName(request.Name);
        var payload = await ResolvePayloadAsync(request, cancellationToken).ConfigureAwait(false);
        ValidatePayload(request.Type, payload);
        var testResult = await TestPayloadAsync(request.Type, payload, cancellationToken).ConfigureAwait(false);
        if (!testResult.IsSuccess)
        {
            throw new InvalidOperationException(testResult.Message);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var now = DateTimeOffset.UtcNow;
            var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id.Trim();
            if (!Guid.TryParse(id, out _))
            {
                throw new InvalidDataException("Bağlantı kimliği geçersiz.");
            }

            var existingIndex = profiles.FindIndex(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var addedUtc = existingIndex >= 0 ? profiles[existingIndex].AddedUtc : now;
            var profile = new ConnectionProfile(
                id,
                request.Name.Trim(),
                request.Type,
                true,
                addedUtc,
                now,
                now,
                true,
                testResult.Version,
                testResult.ItemCount,
                testResult.Message);

            await SavePayloadAsync(id, payload, cancellationToken).ConfigureAwait(false);
            if (existingIndex >= 0)
            {
                profiles[existingIndex] = profile;
            }
            else
            {
                profiles.Add(profile);
            }

            try
            {
                await _registry.SaveAsync(profiles, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (existingIndex < 0)
                {
                    await _secrets.RemoveAsync(SecretPrefix + id, cancellationToken).ConfigureAwait(false);
                }

                throw;
            }

            return profile;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task RemoveAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            profiles.RemoveAll(item => item.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            await _registry.SaveAsync(profiles, cancellationToken).ConfigureAwait(false);
            await _secrets.RemoveAsync(SecretPrefix + profileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SetEnabledAsync(
        string profileId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = profiles.FindIndex(item => item.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("Bağlantı kaydı bulunamadı.");
            }

            profiles[index] = profiles[index] with { IsEnabled = enabled, UpdatedUtc = DateTimeOffset.UtcNow };
            await _registry.SaveAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<ExternalSearchResult> SearchAsync(
        ExternalSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var profiles = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.IsEnabled && item.LastTestSucceeded && item.SupportsReleaseSearch)
            .ToArray();
        if (profiles.Length == 0)
        {
            return new ExternalSearchResult(
                Array.Empty<ExternalRelease>(),
                ["Etkin bir Prowlarr veya Torznab bağlantısı bulunmuyor."],
                DateTimeOffset.UtcNow);
        }

        var releases = new List<ExternalRelease>();
        var warnings = new List<string>();
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await LoadPayloadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                warnings.Add($"{profile.Name}: şifreli bağlantı ayarları bulunamadı.");
                continue;
            }

            try
            {
                IReadOnlyList<ExternalRelease> current = profile.Type switch
                {
                    ExternalConnectionType.Prowlarr => await SearchProwlarrAsync(profile, payload, request, cancellationToken).ConfigureAwait(false),
                    ExternalConnectionType.Torznab => await SearchTorznabAsync(profile, payload, request, cancellationToken).ConfigureAwait(false),
                    _ => Array.Empty<ExternalRelease>()
                };
                releases.AddRange(current);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error($"Harici arama bağlantısı başarısız oldu: {profile.Name}", exception);
                warnings.Add($"{profile.Name}: bağlantı yanıt vermedi ({exception.GetType().Name}).");
            }
        }

        var limit = Math.Clamp(request.Limit, 1, 300);
        var distinct = releases
            .Where(item => item.HasPlayableSource)
            .GroupBy(item => BuildReleaseIdentity(item), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Seeders ?? -1)
                .ThenByDescending(item => item.PublishedUtc ?? DateTimeOffset.MinValue)
                .First())
            .OrderByDescending(item => item.PublishedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.Seeders ?? -1)
            .Take(limit)
            .ToArray();

        return new ExternalSearchResult(distinct, warnings, DateTimeOffset.UtcNow);
    }

    public async Task<string?> ResolveMagnetAsync(
        ExternalRelease release,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Uri.TryCreate(release.MagnetUri, UriKind.Absolute, out var directMagnet) &&
            directMagnet.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            return directMagnet.AbsoluteUri;
        }

        var profile = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id.Equals(release.ConnectionId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return null;
        }

        var payload = await LoadPayloadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        var candidates = new List<Uri>();
        foreach (var value in new[] { release.MagnetUri, release.DownloadUrl })
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            if (profile.Type == ExternalConnectionType.Torznab)
            {
                uri = AddTorznabApiKeyWhenSameOrigin(uri, payload);
            }

            if (!candidates.Any(existing =>
                    existing.AbsoluteUri.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(uri);
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var magnet = await TryResolveMagnetProxyAsync(
                        candidate,
                        profile.Type,
                        payload.ApiKey,
                        payload.AllowLocalHttp,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(magnet))
                {
                    return magnet;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A failed magnet-proxy probe must not prevent trying the authoritative
                // .torrent DownloadUrl afterwards.
                _logger.Error("Prowlarr magnet proxy çözümlemesi başarısız oldu.", exception);
            }
        }

        return null;
    }

    private async Task<string?> TryResolveMagnetProxyAsync(
        Uri uri,
        ExternalConnectionType type,
        string? apiKey,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        ValidateRemoteUri(uri, allowLocalHttp);
        var original = uri;
        var current = uri;

        for (var redirect = 0; redirect <= 3; redirect++)
        {
            ValidateRemoteUri(current, allowLocalHttp);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if ((type is ExternalConnectionType.Prowlarr or ExternalConnectionType.Sonarr or ExternalConnectionType.Radarr) &&
                !string.IsNullOrWhiteSpace(apiKey) &&
                IsSameAuthority(original, current))
            {
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            }

            using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location
                               ?? throw new HttpRequestException("Sunucu yönlendirmesi hedef adres içermiyor.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);

                if (next.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeMagnet(next.AbsoluteUri)
                           ?? throw new InvalidDataException("Prowlarr geçersiz bir magnet yönlendirmesi döndürdü.");
                }

                // All non-magnet redirects still use the existing SSRF/local-network rules.
                ValidateRemoteUri(next, allowLocalHttp);
                if (!next.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase) &&
                    IsLocalNetworkHost(next) &&
                    !IsLocalNetworkHost(original))
                {
                    throw new HttpRequestException(
                        "Uzak sunucu yerel ağ adresine yönlendirme yaptı; güvenlik nedeniyle istek durduruldu.");
                }

                current = next;
                continue;
            }

            response.EnsureSuccessStatusCode();

            // A torrent proxy may return the .torrent directly. In that case this
            // probe intentionally returns null and DownloadTorrentAsync handles it.
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType?.Equals("application/x-bittorrent", StringComparison.OrdinalIgnoreCase) == true ||
                response.Content.Headers.ContentDisposition?.FileName?.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true ||
                response.Content.Headers.ContentDisposition?.FileNameStar?.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > 64 * 1024)
            {
                return null;
            }

            var bytes = await ReadBytesWithLimitAsync(response, 64 * 1024, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes[0] == (byte)'d')
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(bytes).Trim();
            return NormalizeMagnet(text);
        }

        throw new HttpRequestException("Sunucu çok fazla yönlendirme döndürdü.");
    }

    public async Task<string> DownloadTorrentAsync(
        ExternalRelease release,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(release.DownloadUrl) ||
            !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            throw new InvalidOperationException("Seçili sonuç indirilebilir bir .torrent adresi içermiyor.");
        }

        var profile = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id.Equals(release.ConnectionId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            throw new InvalidOperationException("Sonucun bağlantı kaydı bulunamadı.");
        }

        var payload = await LoadPayloadAsync(profile.Id, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Bağlantının şifreli ayarları bulunamadı.");
        if (profile.Type == ExternalConnectionType.Torznab)
        {
            downloadUri = AddTorznabApiKeyWhenSameOrigin(downloadUri, payload);
        }

        ValidateRemoteUri(downloadUri, payload.AllowLocalHttp);
        using var response = await SendAsync(downloadUri, profile.Type, payload.ApiKey, payload.AllowLocalHttp, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBytesWithLimitAsync(response, MaximumTorrentBytes, cancellationToken).ConfigureAwait(false);
        var first = bytes.FirstOrDefault(value => !char.IsWhiteSpace((char)value));
        if (bytes.Length < 16 || first != (byte)'d')
        {
            throw new InvalidDataException("Sunucu geçerli bir torrent dosyası döndürmedi.");
        }

        Directory.CreateDirectory(targetDirectory);
        var safeName = MakeSafeFileName(release.Title);
        var path = Path.Combine(targetDirectory, $"connection-{Guid.NewGuid():N}-{safeName}.torrent");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private async Task<ConnectionTestResult> TestPayloadAsync(
        ExternalConnectionType type,
        ConnectionSecretPayload payload,
        CancellationToken cancellationToken)
    {
        return type switch
        {
            ExternalConnectionType.Prowlarr => await TestServarrAsync(type, payload, "api/v1/system/status", "api/v1/indexer", cancellationToken).ConfigureAwait(false),
            ExternalConnectionType.Sonarr => await TestServarrAsync(type, payload, "api/v3/system/status", "api/v3/series", cancellationToken).ConfigureAwait(false),
            ExternalConnectionType.Radarr => await TestServarrAsync(type, payload, "api/v3/system/status", "api/v3/movie", cancellationToken).ConfigureAwait(false),
            ExternalConnectionType.Torznab => await TestTorznabAsync(payload, cancellationToken).ConfigureAwait(false),
            _ => new ConnectionTestResult(false, "Desteklenmeyen bağlantı türü.")
        };
    }

    private async Task<ConnectionTestResult> TestServarrAsync(
        ExternalConnectionType type,
        ConnectionSecretPayload payload,
        string statusPath,
        string collectionPath,
        CancellationToken cancellationToken)
    {
        var baseUri = NormalizeServarrBaseUri(payload.BaseUrl, payload.AllowLocalHttp);
        var statusUri = new Uri(baseUri, statusPath);
        using var statusDocument = await GetJsonAsync(statusUri, type, payload.ApiKey, payload.AllowLocalHttp, cancellationToken).ConfigureAwait(false);
        if (statusDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new ConnectionTestResult(false, "Sunucu beklenen durum yanıtını döndürmedi.");
        }

        var version = GetString(statusDocument.RootElement, "version");
        int? itemCount = null;
        try
        {
            using var collectionDocument = await GetJsonAsync(
                    new Uri(baseUri, collectionPath),
                    type,
                    payload.ApiKey,
                    payload.AllowLocalHttp,
                    cancellationToken)
                .ConfigureAwait(false);
            if (collectionDocument.RootElement.ValueKind == JsonValueKind.Array)
            {
                itemCount = collectionDocument.RootElement.GetArrayLength();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error("Bağlantı koleksiyon sayısı alınamadı.", exception);
        }

        var label = type switch
        {
            ExternalConnectionType.Prowlarr => "Prowlarr",
            ExternalConnectionType.Sonarr => "Sonarr",
            ExternalConnectionType.Radarr => "Radarr",
            _ => "Servarr"
        };
        return new ConnectionTestResult(
            true,
            $"{label} bağlantısı doğrulandı.",
            version,
            itemCount,
            type == ExternalConnectionType.Prowlarr);
    }

    private async Task<ConnectionTestResult> TestTorznabAsync(
        ConnectionSecretPayload payload,
        CancellationToken cancellationToken)
    {
        var endpoint = NormalizeTorznabUri(payload.BaseUrl, payload.AllowLocalHttp);
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("t", "caps")
        };
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            parameters.Add(new KeyValuePair<string, string?>("apikey", payload.ApiKey));
        }

        var uri = AppendQuery(endpoint, parameters);
        var document = await GetXmlAsync(uri, payload.AllowLocalHttp, cancellationToken).ConfigureAwait(false);
        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("caps", StringComparison.OrdinalIgnoreCase))
        {
            return new ConnectionTestResult(false, "Torznab sunucusu geçerli caps yanıtı döndürmedi.");
        }

        var categoryCount = root.Descendants().Count(element =>
            element.Name.LocalName.Equals("category", StringComparison.OrdinalIgnoreCase));
        var version = root.Attribute("version")?.Value;
        return new ConnectionTestResult(
            true,
            "Torznab bağlantısı doğrulandı.",
            version,
            categoryCount,
            true);
    }

    private async Task<IReadOnlyList<ExternalRelease>> SearchProwlarrAsync(
        ConnectionProfile profile,
        ConnectionSecretPayload payload,
        ExternalSearchRequest request,
        CancellationToken cancellationToken)
    {
        var baseUri = NormalizeServarrBaseUri(payload.BaseUrl, payload.AllowLocalHttp);
        // Deliberately do not send indexerIds here. A Prowlarr profile represents
        // one Prowlarr instance; Prowlarr fans the search out to its eligible enabled indexers.
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("type", "search"),
            new("limit", Math.Clamp(Math.Min(request.Limit, payload.ResultLimit), 1, 300).ToString(CultureInfo.InvariantCulture)),
            new("offset", "0")
        };
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            parameters.Add(new KeyValuePair<string, string?>("query", request.Query.Trim()));
        }

        foreach (var category in GetCategories(payload, request.ContentType))
        {
            parameters.Add(new KeyValuePair<string, string?>("categories", category.ToString(CultureInfo.InvariantCulture)));
        }

        var uri = AppendQuery(new Uri(baseUri, "api/v1/search"), parameters);
        using var document = await GetJsonAsync(
                uri,
                ExternalConnectionType.Prowlarr,
                payload.ApiKey,
                payload.AllowLocalHttp,
                cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalRelease>();
        }

        var result = new List<ExternalRelease>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var protocol = GetString(element, "protocol");
            if (protocol?.Equals("usenet", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            var title = GetString(element, "title")?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var categoryIds = new List<int>();
            var categoryNames = new List<string>();
            if (element.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
            {
                foreach (var category in categories.EnumerateArray())
                {
                    if (category.ValueKind == JsonValueKind.Object)
                    {
                        var id = GetInteger(category, "id");
                        var name = GetString(category, "name");
                        if (id is not null) categoryIds.Add(id.Value);
                        if (!string.IsNullOrWhiteSpace(name)) categoryNames.Add(name.Trim());
                    }
                    else if (category.TryGetInt32(out var categoryId))
                    {
                        categoryIds.Add(categoryId);
                    }
                }
            }

            var infoHash = NormalizeInfoHash(GetString(element, "infoHash"));
            var magnetValue = GetString(element, "magnetUrl");

            // Prowlarr deliberately proxies both DownloadUrl and MagnetUrl through
            // its own /{indexerId}/download endpoint. A proxied magnet is therefore
            // an http(s) URL until Prowlarr redirects it to the real magnet: URI.
            // Keep that proxy URL instead of discarding it.
            var magnetUri = NormalizeMagnet(magnetValue)
                            ?? ResolveHttpUrl(magnetValue, baseUri);

            // Depending on the indexer/Prowlarr version, downloadUrl can be an
            // absolute URL or a Prowlarr-relative proxy path. Preserve Prowlarr as
            // the authority for .torrent downloads instead of discarding relative
            // endpoints and fabricating a magnet from infoHash.
            var downloadUrl = ResolveHttpUrl(GetString(element, "downloadUrl"), baseUri);
            if (downloadUrl is null)
            {
                downloadUrl = ResolveHttpUrl(magnetValue, baseUri);
            }

            var guid = GetString(element, "guid") ?? infoHash ?? title;
            result.Add(new ExternalRelease(
                $"prowlarr:{profile.Id}:{guid}",
                profile.Id,
                profile.Name,
                profile.Type,
                title,
                Classify(title, categoryIds, categoryNames),
                ParseDate(GetString(element, "publishDate")),
                GetLong(element, "size"),
                GetInteger(element, "seeders"),
                GetInteger(element, "leechers"),
                GetString(element, "indexer"),
                magnetUri,
                downloadUrl,
                infoHash,
                NormalizeImdbId(GetInteger(element, "imdbId"), GetString(element, "imdbId")),
                NormalizeNumericId(GetInteger(element, "tmdbId"), GetString(element, "tmdbId")),
                NormalizeNumericId(GetInteger(element, "tvdbId"), GetString(element, "tvdbId")),
                categoryIds.Distinct().ToArray(),
                categoryNames.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray()));
        }

        return result;
    }

    private async Task<IReadOnlyList<ExternalRelease>> SearchTorznabAsync(
        ConnectionProfile profile,
        ConnectionSecretPayload payload,
        ExternalSearchRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = NormalizeTorznabUri(payload.BaseUrl, payload.AllowLocalHttp);
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("t", "search"),
            new("extended", "1"),
            new("limit", Math.Clamp(Math.Min(request.Limit, payload.ResultLimit), 1, 300).ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            parameters.Add(new KeyValuePair<string, string?>("q", request.Query.Trim()));
        }

        var categories = GetCategories(payload, request.ContentType);
        if (categories.Count > 0)
        {
            parameters.Add(new KeyValuePair<string, string?>("cat", string.Join(",", categories)));
        }

        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            parameters.Add(new KeyValuePair<string, string?>("apikey", payload.ApiKey));
        }

        var document = await GetXmlAsync(
                AppendQuery(endpoint, parameters),
                payload.AllowLocalHttp,
                cancellationToken)
            .ConfigureAwait(false);
        var items = document.Descendants().Where(element =>
            element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase));
        var result = new List<ExternalRelease>();
        foreach (var item in items)
        {
            var title = GetElementValue(item, "title")?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var attributes = item.Descendants()
                .Where(element => element.Name.LocalName.Equals("attr", StringComparison.OrdinalIgnoreCase))
                .Select(element => new
                {
                    Name = element.Attribute("name")?.Value,
                    Value = element.Attribute("value")?.Value
                })
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Name))
                .GroupBy(pair => pair.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

            var categoryIds = new List<int>();
            var categoryNames = new List<string>();
            foreach (var category in item.Elements().Where(element =>
                         element.Name.LocalName.Equals("category", StringComparison.OrdinalIgnoreCase)))
            {
                var value = category.Value.Trim();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    categoryIds.Add(id);
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    categoryNames.Add(value);
                }
            }

            if (attributes.TryGetValue("category", out var attributeCategory) &&
                int.TryParse(attributeCategory, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributeCategoryId))
            {
                categoryIds.Add(attributeCategoryId);
            }

            var enclosure = item.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("enclosure", StringComparison.OrdinalIgnoreCase));
            var downloadUrl = NormalizeHttpUrl(enclosure?.Attribute("url")?.Value)
                              ?? NormalizeHttpUrl(GetElementValue(item, "link"));
            var magnetUri = NormalizeMagnet(attributes.GetValueOrDefault("magneturl"))
                            ?? NormalizeMagnet(GetElementValue(item, "link"));
            var infoHash = NormalizeInfoHash(attributes.GetValueOrDefault("infohash"));
            var size = ParseLong(attributes.GetValueOrDefault("size"))
                       ?? ParseLong(enclosure?.Attribute("length")?.Value);
            var seeders = ParseInteger(attributes.GetValueOrDefault("seeders"));
            var peers = ParseInteger(attributes.GetValueOrDefault("peers"));
            int? leechers = peers is not null && seeders is not null
                ? Math.Max(0, peers.Value - seeders.Value)
                : null;
            var guid = GetElementValue(item, "guid") ?? infoHash ?? title;

            result.Add(new ExternalRelease(
                $"torznab:{profile.Id}:{guid}",
                profile.Id,
                profile.Name,
                profile.Type,
                title,
                Classify(title, categoryIds, categoryNames),
                ParseDate(GetElementValue(item, "pubDate")),
                size,
                seeders,
                leechers,
                attributes.GetValueOrDefault("indexer") ?? profile.Name,
                magnetUri,
                downloadUrl,
                infoHash,
                NormalizeImdbId(ParseInteger(attributes.GetValueOrDefault("imdbid")), attributes.GetValueOrDefault("imdbid")),
                NormalizeNumericId(ParseInteger(attributes.GetValueOrDefault("tmdbid")), attributes.GetValueOrDefault("tmdbid")),
                NormalizeNumericId(ParseInteger(attributes.GetValueOrDefault("tvdbid")), attributes.GetValueOrDefault("tvdbid")),
                categoryIds.Distinct().ToArray(),
                categoryNames.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray()));
        }

        return result;
    }

    private ExternalContentType Classify(
        string title,
        IReadOnlyCollection<int> categoryIds,
        IReadOnlyCollection<string> categoryNames)
    {
        if (categoryIds.Any(id => id == 5070) ||
            categoryNames.Any(name => name.Contains("anime", StringComparison.OrdinalIgnoreCase)) ||
            title.Contains("anime", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalContentType.Anime;
        }

        if (categoryIds.Any(id => id is >= 2000 and < 3000))
        {
            return ExternalContentType.Movie;
        }

        if (categoryIds.Any(id => id is >= 5000 and < 6000))
        {
            return ExternalContentType.Series;
        }

        var identity = _releaseParser.Parse(title);
        return identity.ContentType.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? ExternalContentType.Series
            : categoryNames.Any(name => name.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                                        name.Contains("film", StringComparison.OrdinalIgnoreCase))
                ? ExternalContentType.Movie
                : ExternalContentType.Other;
    }

    private async Task<ConnectionSecretPayload> ResolvePayloadAsync(
        SaveConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ConnectionSecretPayload? existing = null;
        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            existing = await LoadPayloadAsync(request.Id, cancellationToken).ConfigureAwait(false);
        }

        var normalizedBaseUrl = NormalizeBaseUrlText(request.Type, request.BaseUrl, out var embeddedApiKey);
        var apiKey = !string.IsNullOrWhiteSpace(request.ApiKey)
            ? request.ApiKey.Trim()
            : !string.IsNullOrWhiteSpace(embeddedApiKey)
                ? embeddedApiKey
                : existing?.ApiKey;

        return new ConnectionSecretPayload(
            normalizedBaseUrl,
            apiKey,
            NormalizeCategoryText(request.MovieCategories),
            NormalizeCategoryText(request.SeriesCategories),
            NormalizeCategoryText(request.AnimeCategories),
            NormalizeCategoryText(request.OtherCategories),
            Math.Clamp(request.ResultLimit, 1, 300),
            request.AllowLocalHttp);
    }

    private async Task<ConnectionSecretPayload?> LoadPayloadAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var value = await _secrets.GetAsync(SecretPrefix + profileId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConnectionSecretPayload>(value);
        }
        catch (JsonException exception)
        {
            _logger.Error("Şifreli harici bağlantı ayarı çözümlenemedi.", exception);
            return null;
        }
    }

    private Task SavePayloadAsync(
        string profileId,
        ConnectionSecretPayload payload,
        CancellationToken cancellationToken) =>
        _secrets.SetAsync(SecretPrefix + profileId, JsonSerializer.Serialize(payload), cancellationToken);

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("Bağlantı adı gereklidir.");
        }

        if (name.Trim().Length > 80)
        {
            throw new InvalidDataException("Bağlantı adı 80 karakteri aşamaz.");
        }
    }

    private static void ValidatePayload(ExternalConnectionType type, ConnectionSecretPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.BaseUrl))
        {
            throw new InvalidDataException("Sunucu adresi gereklidir.");
        }

        var uri = type == ExternalConnectionType.Torznab
            ? NormalizeTorznabUri(payload.BaseUrl, payload.AllowLocalHttp)
            : NormalizeServarrBaseUri(payload.BaseUrl, payload.AllowLocalHttp);
        ValidateRemoteUri(uri, payload.AllowLocalHttp);
        if (type != ExternalConnectionType.Torznab && string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            throw new InvalidDataException("Bu bağlantı türü için API anahtarı gereklidir.");
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        Uri uri,
        ExternalConnectionType type,
        string? apiKey,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        ValidateRemoteUri(uri, allowLocalHttp);
        using var response = await SendAsync(uri, type, apiKey, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBytesWithLimitAsync(response, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private async Task<XDocument> GetXmlAsync(
        Uri uri,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        ValidateRemoteUri(uri, allowLocalHttp);
        using var response = await SendAsync(
                uri,
                ExternalConnectionType.Torznab,
                null,
                allowLocalHttp,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBytesWithLimitAsync(response, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        await using var stream = new MemoryStream(bytes, writable: false);
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumResponseBytes * 2L,
            MaxCharactersFromEntities = 0
        };
        using var reader = XmlReader.Create(stream, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        ExternalConnectionType type,
        string? apiKey,
        bool allowLocalHttp,
        CancellationToken cancellationToken)
    {
        ValidateRemoteUri(uri, allowLocalHttp);
        var original = uri;
        var current = uri;
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            ValidateRemoteUri(current, allowLocalHttp);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if ((type is ExternalConnectionType.Prowlarr or ExternalConnectionType.Sonarr or ExternalConnectionType.Radarr) &&
                !string.IsNullOrWhiteSpace(apiKey) &&
                IsSameAuthority(original, current))
            {
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new HttpRequestException("Sunucu yönlendirmesi hedef adres içermiyor.");
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                ValidateRemoteUri(next, allowLocalHttp);
                if (!next.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase) &&
                    IsLocalNetworkHost(next) &&
                    !IsLocalNetworkHost(original))
                {
                    throw new HttpRequestException("Uzak sunucu yerel ağ adresine yönlendirme yaptı; güvenlik nedeniyle istek durduruldu.");
                }

                current = next;
                continue;
            }

            return response;
        }

        throw new HttpRequestException("Sunucu çok fazla yönlendirme döndürdü.");
    }

    private static bool IsSameAuthority(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static async Task<byte[]> ReadBytesWithLimitAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("Sunucu yanıtı izin verilen boyutu aşıyor.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Sunucu yanıtı izin verilen boyutu aşıyor.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static Uri NormalizeServarrBaseUri(string value, bool allowLocalHttp)
    {
        var text = value.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/api/v1", "/api/v3", "/api" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        if (!Uri.TryCreate(text + "/", UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("Geçerli bir sunucu adresi girin.");
        }

        ValidateRemoteUri(uri, allowLocalHttp);
        return uri;
    }

    private static Uri NormalizeTorznabUri(string value, bool allowLocalHttp)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("Geçerli bir Torznab API adresi girin.");
        }

        ValidateRemoteUri(uri, allowLocalHttp);
        return uri;
    }

    private static string NormalizeBaseUrlText(
        ExternalConnectionType type,
        string value,
        out string? embeddedApiKey)
    {
        embeddedApiKey = null;
        value = value?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var inputUri))
        {
            return value;
        }

        var builder = new UriBuilder(inputUri);
        var retained = new List<string>();
        foreach (var part in builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawName = separator < 0 ? part : part[..separator];
            var rawValue = separator < 0 ? string.Empty : part[(separator + 1)..];
            var name = Uri.UnescapeDataString(rawName.Replace('+', ' '));
            var decodedValue = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            if (name.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("api_key", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(decodedValue))
                {
                    embeddedApiKey = decodedValue.Trim();
                }

                continue;
            }

            if (type == ExternalConnectionType.Torznab &&
                new[] { "t", "q", "cat", "limit", "offset", "extended" }
                    .Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            retained.Add(part);
        }

        builder.Query = type == ExternalConnectionType.Torznab ? string.Join("&", retained) : string.Empty;
        builder.Fragment = string.Empty;
        var text = builder.Uri.AbsoluteUri.TrimEnd('/');
        if (type == ExternalConnectionType.Torznab)
        {
            return text;
        }

        foreach (var suffix in new[] { "/api/v1", "/api/v3", "/api" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        return text;
    }

    private static void ValidateRemoteUri(Uri uri, bool allowLocalHttp)
    {
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new InvalidDataException("Kullanıcı adı veya parola URL içinde bulunamaz.");
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Yalnız HTTP veya HTTPS adresleri desteklenir.");
        }

        if (!allowLocalHttp || !IsLocalNetworkHost(uri))
        {
            throw new InvalidDataException("HTTP yalnız kullanıcı onayıyla localhost veya yerel ağ adreslerinde kullanılabilir. Uzak bağlantılar HTTPS kullanmalıdır.");
        }
    }

    private static bool IsLocalNetworkHost(Uri uri)
    {
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.'))
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    private static Uri AppendQuery(Uri uri, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        var additions = parameters
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        builder.Query = string.Join("&", new[] { query }.Where(value => !string.IsNullOrWhiteSpace(value)).Concat(additions));
        return builder.Uri;
    }

    private static Uri AddTorznabApiKeyWhenSameOrigin(Uri downloadUri, ConnectionSecretPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.ApiKey) ||
            downloadUri.Query.Contains("apikey=", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(payload.BaseUrl, UriKind.Absolute, out var baseUri) ||
            !downloadUri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            downloadUri.Port != baseUri.Port)
        {
            return downloadUri;
        }

        return AppendQuery(downloadUri, [new KeyValuePair<string, string?>("apikey", payload.ApiKey)]);
    }

    private static IReadOnlyList<int> GetCategories(ConnectionSecretPayload payload, ExternalContentType contentType)
    {
        var text = contentType switch
        {
            ExternalContentType.Movie => payload.MovieCategories,
            ExternalContentType.Series => payload.SeriesCategories,
            ExternalContentType.Anime => payload.AnimeCategories,
            ExternalContentType.Other => payload.OtherCategories,
            _ => string.Join(",", new[]
            {
                payload.MovieCategories,
                payload.SeriesCategories,
                payload.AnimeCategories,
                payload.OtherCategories
            }.Where(value => !string.IsNullOrWhiteSpace(value)))
        };

        return text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1)
            .Where(value => value >= 0)
            .Distinct()
            .ToArray();
    }

    private static string NormalizeCategoryText(string value) =>
        string.Join(",", (value ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Distinct(StringComparer.Ordinal));

    private static string BuildReleaseIdentity(ExternalRelease release)
    {
        if (!string.IsNullOrWhiteSpace(release.InfoHash))
        {
            return "hash:" + release.InfoHash;
        }

        if (!string.IsNullOrWhiteSpace(release.MagnetUri))
        {
            return "magnet:" + release.MagnetUri;
        }

        return $"{release.Title}|{release.SizeBytes}|{release.ConnectionId}";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInteger(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String ? ParseInteger(value.GetString()) : null;
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String ? ParseLong(value.GetString()) : null;
    }

    private static int? ParseInteger(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string? NormalizeHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;

    private static string? ResolveHttpUrl(string? value, Uri baseUri)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme is "http" or "https"
                ? absolute.AbsoluteUri
                : null;
        }

        return Uri.TryCreate(baseUri, value, out var resolved) &&
               resolved.Scheme is "http" or "https"
            ? resolved.AbsoluteUri
            : null;
    }

    private static string? NormalizeMagnet(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;

    private static string? NormalizeInfoHash(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return (value.Length is 40 or 32) && value.All(char.IsLetterOrDigit) ? value.ToUpperInvariant() : null;
    }

    private static string? NormalizeImdbId(int? numeric, string? text)
    {
        if (numeric is > 0)
        {
            return $"tt{numeric.Value:0000000}";
        }

        text = text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return text.ToLowerInvariant();
        return int.TryParse(text, out var parsed) && parsed > 0 ? $"tt{parsed:0000000}" : null;
    }

    private static string? NormalizeNumericId(int? numeric, string? text)
    {
        if (numeric is > 0) return numeric.Value.ToString(CultureInfo.InvariantCulture);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string? GetElementValue(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var result = builder.ToString().Trim().Trim('.');
        if (result.Length > 100) result = result[..100];
        return string.IsNullOrWhiteSpace(result) ? "release" : result;
    }

    private static string ToSafeFailureMessage(Exception exception) => exception switch
    {
        HttpRequestException http when http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "Sunucu API anahtarını reddetti.",
        HttpRequestException http when http.StatusCode is not null =>
            $"Sunucu HTTP {(int)http.StatusCode.Value} yanıtı döndürdü.",
        TaskCanceledException => "Bağlantı zaman aşımına uğradı.",
        InvalidDataException data => Redact(data.Message),
        _ => "Bağlantı kurulamadı. Sunucu adresini, API anahtarını ve ağ erişimini kontrol edin."
    };

    private static string Redact(string value)
    {
        var marker = value.IndexOf("apikey=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return value;
        }

        var end = value.IndexOf('&', marker);
        return end < 0 ? value[..marker] + "apikey=***" : value[..marker] + "apikey=***" + value[end..];
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _mutationGate.Dispose();
    }

    private sealed record ConnectionSecretPayload(
        string BaseUrl,
        string? ApiKey,
        string MovieCategories,
        string SeriesCategories,
        string AnimeCategories,
        string OtherCategories,
        int ResultLimit,
        bool AllowLocalHttp);
}
