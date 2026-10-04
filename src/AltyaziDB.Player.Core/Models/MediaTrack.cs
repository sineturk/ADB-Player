namespace AltyaziDB.Player.Core.Models;

public sealed record MediaTrack(
    long Id,
    TrackType Type,
    string? Title,
    string? Language,
    string? Codec,
    bool IsSelected,
    bool IsExternal,
    bool IsDefault = false)
{
    public bool IsDisabled => Type == TrackType.Subtitle && Id < 0;

    public string DisplayName
    {
        get
        {
            if (IsDisabled)
            {
                return "Kapalı";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title))
            {
                parts.Add(Title.Trim());
            }

            if (!string.IsNullOrWhiteSpace(Language))
            {
                parts.Add(Language.Trim().ToUpperInvariant());
            }

            if (!string.IsNullOrWhiteSpace(Codec))
            {
                parts.Add(Codec.Trim().ToUpperInvariant());
            }

            if (parts.Count == 0)
            {
                parts.Add($"Parça {Id}");
            }

            if (IsExternal)
            {
                parts.Add("Harici");
            }

            return string.Join(" · ", parts);
        }
    }

    public static MediaTrack DisabledSubtitle() =>
        new(-1, TrackType.Subtitle, "Kapalı", null, null, true, false);
}
