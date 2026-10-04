using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface IResumeStore
{
    Task<ResumeEntry?> GetAsync(string source, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResumeEntry>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(
        string source,
        double positionSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default);
    Task RemoveAsync(string source, CancellationToken cancellationToken = default);
}
