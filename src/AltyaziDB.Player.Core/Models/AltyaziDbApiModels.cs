namespace AltyaziDB.Player.Core.Models;

public sealed record ApiRateWindow(int Limit, int Remaining)
{
    public string Label => $"{Remaining:N0}/{Limit:N0}";
}

public sealed record AltyaziDbAccountStatus(
    string Username,
    string Role,
    ApiRateWindow Minute,
    ApiRateWindow Hour,
    ApiRateWindow Day)
{
    public string Summary => $"{Username} · {Role} · Dakika {Minute.Label} · Saat {Hour.Label} · Gün {Day.Label}";
}

public sealed record SubtitleSearchPagination(
    int CurrentPage,
    int TotalPages,
    int TotalRecords,
    int Limit)
{
    public static SubtitleSearchPagination Empty { get; } = new(1, 1, 0, 20);
}

public sealed record SubtitleReleaseVersion(string Group, string File);
