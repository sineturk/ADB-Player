namespace AltyaziDB.Player.Core.Models;

public sealed record ResumeEntry(
    string Source,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset UpdatedAtUtc);
