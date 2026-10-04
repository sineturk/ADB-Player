using System.Text.Json;
using AltyaziDB.Player.Addons.Models;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.Addons.Services;

internal sealed class AddonRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AddonRegistryStore(string filePath, IAppLogger logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AddonRegistration>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<AddonRegistration>();
            }

            await using var stream = File.OpenRead(_filePath);
            var values = await JsonSerializer.DeserializeAsync<List<StoredAddonRegistration>>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            return (values ?? new List<StoredAddonRegistration>())
                .Where(IsValid)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.AddedUtc).First())
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new AddonRegistration(
                    item.Id,
                    item.Name,
                    string.Empty,
                    item.Version,
                    item.IsEnabled,
                    item.AddedUtc,
                    item.Description,
                    item.HasCatalogs,
                    item.SupportsStreams,
                    item.SupportsMetadata,
                    item.AllowLocalHttp))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Error("Eklenti kayıt dosyası okunamadı.", exception);
            return Array.Empty<AddonRegistration>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<AddonRegistration> registrations,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var values = registrations
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new StoredAddonRegistration(
                    item.Id,
                    item.Name,
                    item.Version,
                    item.IsEnabled,
                    item.AddedUtc,
                    item.Description,
                    item.HasCatalogs,
                    item.SupportsStreams,
                    item.SupportsMetadata,
                    item.AllowLocalHttp))
                .ToArray();

            var temporaryPath = _filePath + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, values, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsValid(StoredAddonRegistration registration) =>
        !string.IsNullOrWhiteSpace(registration.Id) &&
        !string.IsNullOrWhiteSpace(registration.Name);

    private sealed record StoredAddonRegistration(
        string Id,
        string Name,
        string Version,
        bool IsEnabled,
        DateTimeOffset AddedUtc,
        string? Description,
        bool HasCatalogs,
        bool SupportsStreams,
        bool SupportsMetadata,
        bool AllowLocalHttp);
}
