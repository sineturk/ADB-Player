using System.Text.Json;
using AltyaziDB.Player.Connections.Models;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.Connections.Services;

internal sealed class ConnectionRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionRegistryStore(string filePath, IAppLogger logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<ConnectionProfile>();
            }

            await using var stream = File.OpenRead(_filePath);
            var values = await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            return (values ?? new List<ConnectionProfile>())
                .Where(IsValid)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedUtc).First())
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Error("Harici bağlantı kayıt dosyası okunamadı.", exception);
            return Array.Empty<ConnectionProfile>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        profiles.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
                        JsonOptions,
                        cancellationToken)
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

    private static bool IsValid(ConnectionProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Id) &&
        !string.IsNullOrWhiteSpace(profile.Name) &&
        Guid.TryParse(profile.Id, out _);
}
