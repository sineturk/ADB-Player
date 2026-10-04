using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AltyaziDB.Player.Addons.Models;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.Addons.Services;

public sealed class AddonCatalogService : IAddonCatalogService
{
    private static readonly TimeSpan CatalogCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManifestCacheDuration = TimeSpan.FromMinutes(15);

    private const string SecretPrefix = "addon.manifest.";

    private readonly AddonRegistryStore _registry;
    private readonly StremioAddonClient _client;
    private readonly ISecretStore _secrets;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ManifestCacheEntry> _manifestCache =
        new(StringComparer.OrdinalIgnoreCase);

    private AddonCatalogSnapshot? _catalogCache;
    private bool _disposed;

    public AddonCatalogService(
        string registryFilePath,
        ISecretStore secrets,
        IAppLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryFilePath);
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registry = new AddonRegistryStore(registryFilePath, logger);
        _client = new StremioAddonClient(logger);
    }

    public Task<IReadOnlyList<AddonRegistration>> GetAddonsAsync(CancellationToken cancellationToken = default) =>
        LoadHydratedRegistrationsAsync(cancellationToken);

    public async Task<AddonRegistration> AddAddonAsync(
        string manifestUrl,
        bool allowLocalHttp,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var manifest = await _client.ReadManifestAsync(manifestUrl, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        var registration = manifest.Registration with { AllowLocalHttp = allowLocalHttp };

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registrations = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var existing = registrations.FirstOrDefault(item =>
                item.Id.Equals(registration.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                throw new InvalidOperationException($"{existing.Name} eklentisi zaten kurulu.");
            }

            await _secrets.SetAsync(SecretKey(registration.Id), registration.ManifestUrl, cancellationToken).ConfigureAwait(false);
            registrations.Add(registration);
            try
            {
                await _registry.SaveAsync(registrations, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _secrets.RemoveAsync(SecretKey(registration.Id), cancellationToken).ConfigureAwait(false);
                throw;
            }

            _manifestCache[registration.Id] = new ManifestCacheEntry(
                registration,
                manifest.Catalogs,
                manifest.StreamResource,
                manifest.MetadataResource,
                DateTimeOffset.UtcNow);
            InvalidateCatalogCache();
            _logger.Info($"Eklenti kuruldu: {registration.Name} ({registration.Id})");
            return registration;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<AddonRegistration> RestoreAddonAsync(
        string manifestUrl,
        bool allowLocalHttp,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var manifest = await _client.ReadManifestAsync(manifestUrl, allowLocalHttp, cancellationToken).ConfigureAwait(false);
        var normalized = manifest.Registration with
        {
            IsEnabled = isEnabled,
            AllowLocalHttp = allowLocalHttp
        };

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registrations = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = registrations.FindIndex(item =>
                item.Id.Equals(normalized.Id, StringComparison.OrdinalIgnoreCase));
            var stored = index >= 0
                ? normalized with { AddedUtc = registrations[index].AddedUtc }
                : normalized;

            var secretKey = SecretKey(stored.Id);
            var previousSecret = await _secrets.GetAsync(secretKey, cancellationToken).ConfigureAwait(false);
            await _secrets.SetAsync(secretKey, stored.ManifestUrl, cancellationToken).ConfigureAwait(false);

            if (index >= 0) registrations[index] = stored;
            else registrations.Add(stored);

            try
            {
                await _registry.SaveAsync(registrations, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (string.IsNullOrWhiteSpace(previousSecret))
                    await _secrets.RemoveAsync(secretKey, cancellationToken).ConfigureAwait(false);
                else
                    await _secrets.SetAsync(secretKey, previousSecret, cancellationToken).ConfigureAwait(false);
                throw;
            }

            _manifestCache[stored.Id] = new ManifestCacheEntry(
                stored,
                manifest.Catalogs,
                manifest.StreamResource,
                manifest.MetadataResource,
                DateTimeOffset.UtcNow);
            InvalidateCatalogCache();
            _logger.Info($"Eklenti profilden geri yüklendi: {stored.Name} ({stored.Id})");
            return stored;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task RemoveAddonAsync(string addonId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registrations = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var removed = registrations.RemoveAll(item => item.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return;
            }

            await _registry.SaveAsync(registrations, cancellationToken).ConfigureAwait(false);
            await _secrets.RemoveAsync(SecretKey(addonId), cancellationToken).ConfigureAwait(false);
            _manifestCache.TryRemove(addonId, out _);
            InvalidateCatalogCache();
            _logger.Info($"Katalog eklentisi kaldırıldı: {addonId}");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SetAddonEnabledAsync(
        string addonId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registrations = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = registrations.FindIndex(item => item.Id.Equals(addonId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            registrations[index] = registrations[index] with { IsEnabled = isEnabled };
            await _registry.SaveAsync(registrations, cancellationToken).ConfigureAwait(false);
            InvalidateCatalogCache();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<AddonCatalogSnapshot> GetLatestAsync(
        CatalogContentType contentType = CatalogContentType.All,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _catalogCache is not null &&
                DateTimeOffset.UtcNow - _catalogCache.LoadedUtc <= CatalogCacheDuration)
            {
                return FilterSnapshot(_catalogCache, contentType);
            }

            var addons = await LoadHydratedRegistrationsAsync(cancellationToken).ConfigureAwait(false);
            var enabled = addons.Where(item => item.IsEnabled).ToArray();
            if (enabled.Length == 0)
            {
                _catalogCache = new AddonCatalogSnapshot(addons, Array.Empty<CatalogItem>(), DateTimeOffset.UtcNow, Array.Empty<string>());
                return FilterSnapshot(_catalogCache, contentType);
            }

            var items = new ConcurrentBag<CatalogItem>();
            var warnings = new ConcurrentBag<string>();
            using var concurrency = new SemaphoreSlim(4, 4);
            var tasks = enabled.Select(async registration =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var runtime = await GetManifestAsync(registration, forceRefresh, cancellationToken).ConfigureAwait(false);
                    var supportedCatalogs = runtime.Catalogs
                        .Take(24)
                        .ToArray();

                    foreach (var catalog in supportedCatalogs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var catalogItems = await _client.GetCatalogAsync(registration, catalog, registration.AllowLocalHttp, cancellationToken)
                                .ConfigureAwait(false);
                            foreach (var item in catalogItems)
                            {
                                items.Add(item);
                            }
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            _logger.Warning($"{registration.Name} kataloğu okunamadı ({catalog.Name}): {exception.Message}");
                            warnings.Add($"{registration.Name} · {catalog.Name}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.Warning($"{registration.Name} manifesti okunamadı: {exception.Message}");
                    warnings.Add($"{registration.Name}: {exception.Message}");
                }
                finally
                {
                    concurrency.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            var merged = items
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(MergeGroup)
                .OrderByDescending(item => item.ReleasedUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.Year ?? 0)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(240)
                .ToArray();

            _catalogCache = new AddonCatalogSnapshot(
                addons,
                merged,
                DateTimeOffset.UtcNow,
                warnings.Distinct(StringComparer.CurrentCultureIgnoreCase).Take(20).ToArray());
            return FilterSnapshot(_catalogCache, contentType);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    public async Task<IReadOnlyList<CatalogEpisode>> GetEpisodesAsync(
        CatalogItem item,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);

        var registrations = (await LoadHydratedRegistrationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(addon => addon.IsEnabled)
            .OrderByDescending(addon => addon.Id.Equals(item.OriginAddonId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var registration in registrations)
        {
            try
            {
                var runtime = await GetManifestAsync(registration, false, cancellationToken).ConfigureAwait(false);
                if (!runtime.MetadataResource.SupportsRequest(item.SourceType, item.ExternalId))
                {
                    continue;
                }

                var episodes = await _client.GetEpisodesAsync(registration, item, registration.AllowLocalHttp, cancellationToken).ConfigureAwait(false);
                if (episodes.Count > 0)
                {
                    return episodes;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warning($"{registration.Name} bölüm bilgileri alınamadı: {exception.Message}");
            }
        }

        return Array.Empty<CatalogEpisode>();
    }

    public async Task<IReadOnlyList<CatalogStream>> GetStreamsAsync(
        CatalogItem item,
        CatalogEpisode? episode = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);

        var registrations = (await LoadHydratedRegistrationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(addon => addon.IsEnabled)
            .ToArray();
        var requestId = episode?.ExternalId ?? item.ExternalId;

        var streams = new ConcurrentBag<CatalogStream>();
        using var concurrency = new SemaphoreSlim(4, 4);
        var tasks = registrations.Select(async registration =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var runtime = await GetManifestAsync(registration, false, cancellationToken).ConfigureAwait(false);
                if (!runtime.StreamResource.SupportsRequest(item.SourceType, requestId))
                {
                    return;
                }

                var values = await _client.GetStreamsAsync(registration, item, episode, registration.AllowLocalHttp, cancellationToken).ConfigureAwait(false);
                foreach (var stream in values)
                {
                    streams.Add(stream);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warning($"{registration.Name} akış seçenekleri alınamadı: {exception.Message}");
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return streams
            .GroupBy(stream => stream.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(stream => stream.Seeders ?? 0)
                .ThenByDescending(stream => QualityRank(stream.Quality))
                .First())
            .OrderByDescending(stream => stream.Kind == CatalogStreamKind.Direct)
            .ThenByDescending(stream => QualityRank(stream.Quality))
            .ThenByDescending(stream => stream.Seeders ?? 0)
            .ThenBy(stream => stream.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<string> DownloadTorrentAsync(
        CatalogStream stream,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var registration = (await LoadHydratedRegistrationsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id.Equals(stream.AddonId, StringComparison.OrdinalIgnoreCase));
        if (registration is null)
        {
            throw new InvalidOperationException("Akışın eklenti kaydı bulunamadı.");
        }

        return await _client.DownloadTorrentAsync(
                stream,
                targetDirectory,
                registration.AllowLocalHttp,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ManifestCacheEntry> GetManifestAsync(
        AddonRegistration registration,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && _manifestCache.TryGetValue(registration.Id, out var cached) &&
            DateTimeOffset.UtcNow - cached.LoadedUtc <= ManifestCacheDuration &&
            cached.Registration.ManifestUrl.Equals(registration.ManifestUrl, StringComparison.OrdinalIgnoreCase))
        {
            return cached;
        }

        var manifest = await _client.ReadManifestAsync(
                registration.ManifestUrl,
                registration.AllowLocalHttp,
                cancellationToken)
            .ConfigureAwait(false);
        var normalizedRegistration = manifest.Registration with
        {
            IsEnabled = registration.IsEnabled,
            AddedUtc = registration.AddedUtc,
            AllowLocalHttp = registration.AllowLocalHttp
        };
        var entry = new ManifestCacheEntry(
            normalizedRegistration,
            manifest.Catalogs,
            manifest.StreamResource,
            manifest.MetadataResource,
            DateTimeOffset.UtcNow);
        _manifestCache[registration.Id] = entry;
        return entry;
    }

    private async Task<IReadOnlyList<AddonRegistration>> LoadHydratedRegistrationsAsync(
        CancellationToken cancellationToken)
    {
        var stored = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var hydrated = new List<AddonRegistration>(stored.Count);
        foreach (var registration in stored)
        {
            var manifestUrl = await _secrets.GetAsync(SecretKey(registration.Id), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                _logger.Warning($"{registration.Name} eklentisinin şifreli adresi bulunamadı.");
                continue;
            }

            hydrated.Add(registration with { ManifestUrl = manifestUrl });
        }

        return hydrated;
    }

    private static string SecretKey(string addonId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(addonId.Trim().ToLowerInvariant()));
        return SecretPrefix + Convert.ToHexString(hash);
    }

    private static AddonCatalogSnapshot FilterSnapshot(
        AddonCatalogSnapshot snapshot,
        CatalogContentType contentType)
    {
        if (contentType == CatalogContentType.All)
        {
            return snapshot;
        }

        return snapshot with
        {
            Items = snapshot.Items.Where(item => item.ContentType == contentType).ToArray()
        };
    }

    private static CatalogItem MergeGroup(IGrouping<string, CatalogItem> group)
    {
        var ranked = group
            .OrderByDescending(item => ScoreItem(item))
            .ThenByDescending(item => item.ReleasedUtc ?? DateTimeOffset.MinValue)
            .ToArray();
        var first = ranked[0];
        var genres = ranked.SelectMany(item => item.Genres)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToArray();

        return first with
        {
            PosterUrl = ranked.Select(item => item.PosterUrl).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            BackgroundUrl = ranked.Select(item => item.BackgroundUrl).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Description = ranked.Select(item => item.Description).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Rating = ranked.Select(item => item.Rating).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            ReleasedUtc = ranked.Select(item => item.ReleasedUtc).FirstOrDefault(value => value is not null),
            Genres = genres
        };
    }

    private static int ScoreItem(CatalogItem item)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(item.PosterUrl)) score += 4;
        if (!string.IsNullOrWhiteSpace(item.Description)) score += 3;
        if (!string.IsNullOrWhiteSpace(item.BackgroundUrl)) score += 1;
        if (!string.IsNullOrWhiteSpace(item.Rating)) score += 1;
        if (item.ReleasedUtc is not null) score += 2;
        return score;
    }

    private static int QualityRank(string? quality) => quality?.ToUpperInvariant() switch
    {
        "2160P" or "4K" or "UHD" => 4,
        "1080P" => 3,
        "720P" => 2,
        "480P" => 1,
        _ => 0
    };

    private void InvalidateCatalogCache() => _catalogCache = null;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
        _mutationGate.Dispose();
        _catalogGate.Dispose();
    }

    private sealed record ManifestCacheEntry(
        AddonRegistration Registration,
        IReadOnlyList<AddonCatalogDescriptor> Catalogs,
        AddonResourceRule StreamResource,
        AddonResourceRule MetadataResource,
        DateTimeOffset LoadedUtc);
}
