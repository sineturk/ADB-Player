using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

/// <summary>
/// Supplies short-lived first-party credentials for AltyaziDB Player-only APIs.
/// Implementations must not expose or persist a long-lived application master key.
/// </summary>
public interface IPlayerApiAuthProvider
{
    Task<PlayerApiAuthContext?> GetPlayerApiAuthAsync(
        CancellationToken cancellationToken = default);
}
