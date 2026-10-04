using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Infrastructure;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsService(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<PlayerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.SettingsFile))
            {
                return new PlayerSettings();
            }

            await using var stream = File.OpenRead(_paths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<PlayerSettings>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   .ConfigureAwait(false)
                   ?? new PlayerSettings();
        }
        catch (Exception exception)
        {
            _logger.Error("Ayarlar okunamadı; varsayılan ayarlar kullanılacak.", exception);
            return new PlayerSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        PlayerSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporaryFile = _paths.SettingsFile + ".tmp";
            await using (var stream = File.Create(temporaryFile))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryFile, _paths.SettingsFile, true);
        }
        catch (Exception exception)
        {
            _logger.Error("Ayarlar kaydedilemedi.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }
}
