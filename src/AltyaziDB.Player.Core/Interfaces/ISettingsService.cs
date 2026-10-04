using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface ISettingsService
{
    Task<PlayerSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PlayerSettings settings, CancellationToken cancellationToken = default);
}
