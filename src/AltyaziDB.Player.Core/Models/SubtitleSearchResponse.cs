namespace AltyaziDB.Player.Core.Models;

public sealed record SubtitleSearchResponse(
    IReadOnlyList<SubtitleSearchItem> Results,
    SubtitleMediaCandidate? Candidate,
    SubtitleSearchPagination Pagination,
    IReadOnlyList<string> AvailableVersionGroups,
    string? SearchMethod = null);
