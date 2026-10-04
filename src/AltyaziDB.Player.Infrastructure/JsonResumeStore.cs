using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Infrastructure;

public sealed class JsonResumeStore : IResumeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonResumeStore(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<ResumeEntry?> GetAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return entries.TryGetValue(source, out var entry) ? entry : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ResumeEntry>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return entries.Values
                .OrderByDescending(entry => entry.UpdatedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string source,
        double positionSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);

            var completed = durationSeconds > 0 && durationSeconds - positionSeconds < 45;
            if (positionSeconds < 5 || completed)
            {
                entries.Remove(source);
            }
            else
            {
                entries[source] = new ResumeEntry(
                    source,
                    positionSeconds,
                    durationSeconds,
                    DateTimeOffset.UtcNow);
            }

            await SaveUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error("İzleme konumu kaydedilemedi.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (entries.Remove(source))
            {
                await SaveUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, ResumeEntry>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ResumeFile))
        {
            return new Dictionary<string, ResumeEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(_paths.ResumeFile);
            var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, ResumeEntry>>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            return entries is null
                ? new Dictionary<string, ResumeEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ResumeEntry>(entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            _logger.Error("İzleme geçmişi okunamadı; yeni dosya oluşturulacak.", exception);
            return new Dictionary<string, ResumeEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveUnsafeAsync(
        Dictionary<string, ResumeEntry> entries,
        CancellationToken cancellationToken)
    {
        var temporaryFile = _paths.ResumeFile + ".tmp";
        await using (var stream = File.Create(temporaryFile))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    entries,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryFile, _paths.ResumeFile, true);
    }
}
