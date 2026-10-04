namespace AltyaziDB.Player.SubtitleSync.Models;

public enum SubtitleSyncVerdict
{
    Synchronized,
    AlreadyAligned,
    Uncertain,
    NoMatch,
    Unavailable
}

public enum SubtitleSyncModelKind
{
    Fixed,
    FrameRate,
    Piecewise
}

public sealed record SubtitleSyncMediaSource(
    string Source,
    IReadOnlyDictionary<string, string>? HttpHeaders = null);

public sealed record SubtitleSyncRequest(
    SubtitleSyncMediaSource Reference,
    string SubtitlePath,
    string OutputPath,
    int MaxOffsetSeconds = 120,
    int QualityMaxOffsetSeconds = 90,
    double SplitPenalty = 7.0,
    int? ReferenceAudioStreamIndex = null);

public sealed record SubtitleSyncProgress(
    int Percent,
    string Stage);

public sealed record SubtitleSyncResult(
    SubtitleSyncVerdict Verdict,
    SubtitleSyncModelKind Model,
    string? OutputPath,
    double OffsetSeconds,
    double FrameRateScaleFactor,
    double Score,
    int SplitCount,
    string Message,
    string Engine = "ffsubsync 0.5.1",
    double VerificationResidualSeconds = double.NaN,
    double VerificationCoverage = 0,
    int VerificationProbes = 0)
{
    public bool CanApply =>
        (Verdict is SubtitleSyncVerdict.Synchronized or SubtitleSyncVerdict.AlreadyAligned) &&
        !string.IsNullOrWhiteSpace(OutputPath) &&
        File.Exists(OutputPath);

    public static SubtitleSyncResult Unavailable(string message) => new(
        SubtitleSyncVerdict.Unavailable,
        SubtitleSyncModelKind.Fixed,
        null,
        0,
        1,
        0,
        0,
        message,
        "Kullanılamıyor");
}
