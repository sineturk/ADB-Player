namespace AltyaziDB.Player.Core.Models;

public sealed record CloudAddonPreference(
    string AddonId,
    bool IsEnabled,
    string? ManifestUrl = null,
    bool AllowLocalHttp = false);

public sealed record CloudUserPreferences(
    string UiLanguage,
    string SubtitleLanguage,
    bool AutoPlayNext,
    bool AutoSubtitleSync,
    int SubtitleSyncMaxOffsetSeconds,
    int SeekShortSeconds,
    int SeekMediumSeconds,
    int SeekLongSeconds,
    double PrimarySubtitleScale,
    double SecondarySubtitleScale,
    double PrimarySubtitlePosition,
    double SecondarySubtitlePosition,
    bool PreservePrimaryAssStyle,
    bool PreserveSecondaryAssStyle,
    int TimeDisplayPrecision,
    IReadOnlyList<CloudAddonPreference> Addons,
    DateTimeOffset UpdatedAt,
    bool HasAddonProfiles = false);

public sealed record CloudPreferencesResult(
    bool Success,
    CloudUserPreferences? Preferences,
    bool WasApplied,
    string? ErrorMessage = null)
{
    public static CloudPreferencesResult Ok(
        CloudUserPreferences? preferences,
        bool wasApplied = true) =>
        new(true, preferences, wasApplied);

    public static CloudPreferencesResult Fail(string message) =>
        new(false, null, false, message);
}
