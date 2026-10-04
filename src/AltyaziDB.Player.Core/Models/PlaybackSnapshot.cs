namespace AltyaziDB.Player.Core.Models;

public sealed record PlaybackSnapshot(
    string? Source,
    string Title,
    double PositionSeconds,
    double DurationSeconds,
    double Volume,
    double Speed,
    bool IsPaused,
    bool IsIdle,
    bool IsBuffering,
    bool HasVideo,
    bool HasAudio)
{
    public static PlaybackSnapshot Empty { get; } = new(
        null,
        "AltyazıDB Player",
        0,
        0,
        80,
        1,
        true,
        true,
        false,
        false,
        false);
}
