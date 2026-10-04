using System.IO;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.Services;

public sealed record SharedMediaArtworkEntry(
    string? Title,
    string? PosterUrl,
    string? BackdropUrl,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Shared artwork/cache coordinator used by Home, History and Library.
/// It serializes metadata batches so multiple UI surfaces do not resolve the
/// same cold media concurrently.
/// </summary>
public sealed class MediaArtworkService : IDisposable
{
    private readonly IMediaMetadataService _metadataService;
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private Dictionary<string, SharedMediaArtworkEntry>? _cache;
    private bool _disposed;

    public MediaArtworkService(
        IMediaMetadataService metadataService,
        AppPaths paths,
        IAppLogger logger)
    {
        _metadataService = metadataService;
        _paths = paths;
        _logger = logger;
    }

    public async Task<Dictionary<string, SharedMediaArtworkEntry>> ReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, SharedMediaArtworkEntry>(
                _cache!,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async Task UpdateAsync(
        IReadOnlyList<KeyValuePair<string, SharedMediaArtworkEntry>> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0) return;

        ThrowIfDisposed();
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            foreach (var update in updates)
                _cache![update.Key] = update.Value;

            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var temporaryFile = CacheFile + ".tmp";
            await using (var stream = File.Create(temporaryFile))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        _cache,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryFile, CacheFile, true);
        }
        catch (Exception exception) when (
            exception is IOException
            or JsonException
            or UnauthorizedAccessException)
        {
            _logger.Warning($"Yerel poster önbelleği kaydedilemedi: {exception.Message}");
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async Task<IReadOnlyList<MediaMetadataResult>> ResolveBatchAsync(
        IReadOnlyList<MediaIdentity> identities,
        CancellationToken cancellationToken = default)
    {
        if (identities.Count == 0)
            return Array.Empty<MediaMetadataResult>();

        ThrowIfDisposed();
        await _resolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _metadataService
                .ResolveBatchAsync(identities, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    public async Task<MediaMetadataResult> ResolveDetailAsync(
        MediaIdentity identity,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _resolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _metadataService
                .ResolveAsync(
                    identity,
                    clientId,
                    cancellationToken,
                    includeDetail: true)
                .ConfigureAwait(false);
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null) return;

        Dictionary<string, SharedMediaArtworkEntry>? loaded = null;
        var cacheFile = File.Exists(CacheFile)
            ? CacheFile
            : File.Exists(LegacyContinueWatchingCacheFile)
                ? LegacyContinueWatchingCacheFile
                : null;

        if (!string.IsNullOrWhiteSpace(cacheFile))
        {
            try
            {
                await using var stream = File.OpenRead(cacheFile);
                loaded = await JsonSerializer.DeserializeAsync<
                        Dictionary<string, SharedMediaArtworkEntry>>(
                        stream,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                or JsonException
                or UnauthorizedAccessException)
            {
                _logger.Warning($"Yerel poster önbelleği okunamadı: {exception.Message}");
            }
        }

        _cache = new Dictionary<string, SharedMediaArtworkEntry>(
            loaded ?? new Dictionary<string, SharedMediaArtworkEntry>(),
            StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        foreach (var key in _cache
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _cache.Remove(key);
        }
    }

    private string CacheFile =>
        Path.Combine(_paths.RemoteCacheDirectory, "media-artwork-v1.json");

    private string LegacyContinueWatchingCacheFile =>
        Path.Combine(_paths.RemoteCacheDirectory, "continue-watching-artwork-v2.json");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resolveGate.Dispose();
        _cacheGate.Dispose();
    }
}
