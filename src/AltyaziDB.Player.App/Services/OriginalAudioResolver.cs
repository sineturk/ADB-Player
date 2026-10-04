using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.Services;

public sealed record OriginalAudioSelection(
    int StreamIndex,
    MediaTrack Track,
    string Reason);

public static class OriginalAudioResolver
{
    private static readonly string[] OriginalMarkers =
    [
        "original", "original audio", "original language", "orig", "ov", "o.v.", "main"
    ];

    private static readonly string[] DubMarkers =
    [
        "dub", "dubbed", "dublaj", "turkish dub", "türkçe dublaj", "turkce dublaj",
        "voice over", "voice-over"
    ];

    private static readonly string[] CommentaryMarkers =
    [
        "commentary", "yorum", "director commentary", "audio description",
        "descriptive", "visually impaired", "hearing impaired"
    ];

    private static readonly HashSet<string> TurkishCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tr", "tur", "turkish", "türkçe", "turkce"
    };

    public static OriginalAudioSelection? Resolve(
        IReadOnlyList<MediaTrack> tracks,
        MediaTrack? currentlySelected = null)
    {
        var embedded = tracks
            .Where(track => track.Type == TrackType.Audio && !track.IsExternal)
            .ToArray();
        if (embedded.Length == 0) return null;

        var hasTurkish = embedded.Any(track => IsTurkish(track.Language));
        var hasNonTurkish = embedded.Any(track =>
            !string.IsNullOrWhiteSpace(track.Language) && !IsTurkish(track.Language));

        var ranked = embedded
            .Select((track, index) =>
            {
                var title = (track.Title ?? string.Empty).Trim();
                var normalizedTitle = title.ToLowerInvariant();
                var score = 0;
                var reasons = new List<string>();

                if (ContainsMarker(normalizedTitle, OriginalMarkers))
                {
                    score += 140;
                    reasons.Add("original etiketi");
                }

                if (ContainsMarker(normalizedTitle, CommentaryMarkers))
                {
                    score -= 180;
                    reasons.Add("yorum/açıklama parçası");
                }

                if (ContainsMarker(normalizedTitle, DubMarkers))
                {
                    score -= 120;
                    reasons.Add("dublaj etiketi");
                }

                if (track.IsDefault)
                {
                    score += 28;
                    reasons.Add("varsayılan gömülü ses");
                }

                if (track.IsSelected && currentlySelected?.Id == track.Id)
                {
                    // Selection is only a weak hint. A user may be listening to a
                    // Turkish dub while subtitle sync must still follow original audio.
                    score += 4;
                }

                if (hasTurkish && hasNonTurkish)
                {
                    if (IsTurkish(track.Language))
                    {
                        score -= 70;
                        reasons.Add("çoklu seste Türkçe/dublaj adayı");
                    }
                    else if (!string.IsNullOrWhiteSpace(track.Language))
                    {
                        score += 45;
                        reasons.Add("çoklu seste Türkçe dışı özgün ses adayı");
                    }
                }

                // Preserve container order as the final tie-breaker. In most releases
                // the first embedded audio is the original mix.
                score -= index;

                return new
                {
                    Index = index,
                    Track = track,
                    Score = score,
                    Reason = reasons.Count == 0 ? "ilk gömülü ses" : string.Join(", ", reasons)
                };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .First();

        return new OriginalAudioSelection(ranked.Index, ranked.Track, ranked.Reason);
    }

    private static bool IsTurkish(string? language) =>
        !string.IsNullOrWhiteSpace(language) &&
        TurkishCodes.Contains(language.Trim());

    private static bool ContainsMarker(string value, IEnumerable<string> markers) =>
        markers.Any(marker =>
            value.Equals(marker, StringComparison.OrdinalIgnoreCase) ||
            value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
