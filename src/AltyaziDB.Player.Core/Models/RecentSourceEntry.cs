namespace AltyaziDB.Player.Core.Models;

public sealed record RecentSourceEntry(
    string Source,
    string Title,
    DateTimeOffset LastOpenedUtc,
    bool IsLocal);
