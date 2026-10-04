namespace AltyaziDB.Player.AudioSync.Models;

public enum AudioSyncVerdict
{
    Reliable,
    Uncertain,
    NoMatch,
    Unavailable
}

public enum AudioSyncModelKind
{
    Fixed,
    LinearDrift,
    Piecewise
}

public enum AudioSyncApplicationKind
{
    None,
    FixedDelay,
    LiveTempo,
    PreparedTrack,
    Progressive
}


public enum AudioSyncSafetyClass
{
    Pending,
    Reliable,
    NeedsVerification,
    DifferentCut,
    Rejected
}

public sealed record AudioSyncSafetyAssessment(
    AudioSyncSafetyClass Classification,
    bool AllowDirectCorrection,
    bool AllowPreparation,
    bool AllowPreparedTrack,
    string UserMessage,
    string TechnicalReason)
{
    public bool IsSafe => Classification == AudioSyncSafetyClass.Reliable;

    public string Label => Classification switch
    {
        AudioSyncSafetyClass.Reliable => "Güvenli",
        AudioSyncSafetyClass.NeedsVerification => "Doğrulama gerekli",
        AudioSyncSafetyClass.DifferentCut => "Farklı kurgu",
        AudioSyncSafetyClass.Rejected => "Güvenli değil",
        _ => "Bekliyor",
    };
}

public static class AudioSyncSafetyPolicy
{
    // Rev10.11 product rule:
    // Solve what can be verified strongly; diagnose instead of forcing the rest.
    public const double NormalResidualLimitSeconds = 0.080;
    public const double HybridResidualLimitSeconds = 0.080;
    public const double MinimumHybridCoverage = 0.97;
    public const double MinimumHybridConfidence = 0.65;
    public const int MinimumVerificationProbes = 2;

    public static AudioSyncSafetyAssessment AssessAnalysis(
        AudioSyncResult result,
        bool strongHybridRecoveryEvidence)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Verdict == AudioSyncVerdict.Unavailable)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.Rejected,
                false, false, false,
                "Ses senkronu bu kaynak için kullanılamıyor.",
                "analysis_unavailable");
        }

        if (result.CanApplyFixedDelay || result.CanApplyLiveTempo)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.Reliable,
                true,
                result.Model == AudioSyncModelKind.LinearDrift ||
                !string.IsNullOrWhiteSpace(result.SuspectedFpsConversion),
                false,
                "Ses eşleşmesi güvenilir.",
                result.CanApplyFixedDelay ? "reliable_fixed" : "reliable_linear_drift");
        }

        if (result.Verdict == AudioSyncVerdict.NoMatch && !strongHybridRecoveryEvidence)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.Rejected,
                false, false, false,
                "Bu ses kaynağı video ile güvenilir biçimde eşleşmedi. Harici ses uygulanmadı.",
                "no_match_without_structural_evidence");
        }

        var canVerifyOffline =
            result.Model == AudioSyncModelKind.Piecewise ||
            result.Verdict == AudioSyncVerdict.Uncertain ||
            strongHybridRecoveryEvidence ||
            !string.IsNullOrWhiteSpace(result.SuspectedFpsConversion);

        if (canVerifyOffline)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.NeedsVerification,
                false, true, false,
                "Ses kaynağı ek doğrulama gerektiriyor.",
                strongHybridRecoveryEvidence
                    ? "different_cut_candidate_requires_full_verification"
                    : "offline_verification_required");
        }

        return new AudioSyncSafetyAssessment(
            AudioSyncSafetyClass.Rejected,
            false, false, false,
            "Senkron için yeterli güven oluşmadı. Harici ses değiştirilmedi.",
            "analysis_not_safe_to_apply");
    }

    public static AudioSyncSafetyAssessment AssessPrepared(AudioSyncPreparedAudio prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        if (prepared.VerifiedResidualSeconds is not double residual ||
            prepared.VerifiedProbes < MinimumVerificationProbes)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.Rejected,
                false, false, false,
                "Düzeltilmiş ses yeterince doğrulanamadı. Harici ses uygulanmadı.",
                "prepared_missing_verification");
        }

        var residualLimit = prepared.HybridRecovery
            ? HybridResidualLimitSeconds
            : NormalResidualLimitSeconds;

        if (Math.Abs(residual) > residualLimit)
        {
            return new AudioSyncSafetyAssessment(
                prepared.HybridRecovery ? AudioSyncSafetyClass.DifferentCut : AudioSyncSafetyClass.Rejected,
                false, false, false,
                prepared.HybridRecovery
                    ? "Bu ses kaynağı farklı kurgu içeriyor. Tam otomatik senkron güvenilir değil; harici ses uygulanmadı."
                    : $"Düzeltilmiş ses doğrulama eşiğini geçemedi ({Math.Abs(residual) * 1000:0} ms). Mevcut güvenli düzeltme korunuyor.",
                "prepared_residual_outside_strict_gate");
        }

        if (!prepared.HybridRecovery)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.Reliable,
                false, false, true,
                "Doğrulanmış senkronlu ses hazır.",
                "prepared_normal_verified");
        }

        if (prepared.HybridMissingRegions > 0)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.DifferentCut,
                false, false, false,
                "Bu ses kaynağı farklı kurgu içeriyor ve kaynakta eksik sahneler var. Dil karışmasını önlemek için otomatik senkron uygulanmadı.",
                $"hybrid_missing_regions:{prepared.HybridMissingRegions}");
        }

        if (prepared.HybridCoverage < MinimumHybridCoverage ||
            prepared.HybridConfidence < MinimumHybridConfidence)
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.DifferentCut,
                false, false, false,
                $"Farklı kurgu bulundu ancak güvenli kapsama yeterli değil (%{prepared.HybridCoverage * 100:0}). Harici ses otomatik uygulanmadı.",
                $"hybrid_gate coverage={prepared.HybridCoverage:0.000} confidence={prepared.HybridConfidence:0.000}");
        }

        var progressive = prepared.ProgressiveRegions ?? Array.Empty<AudioSyncProgressiveRegion>();
        if (progressive.Any(region =>
                region.MissingSource ||
                !region.Trusted ||
                region.Rate is < 0.90 or > 1.10))
        {
            return new AudioSyncSafetyAssessment(
                AudioSyncSafetyClass.DifferentCut,
                false, false, false,
                "Farklı kurgu içindeki bazı bölgeler kesin doğrulanamadı. Bölgesel dil geçişi yapılmadan orijinal ses korunuyor.",
                "hybrid_progressive_regions_not_uniformly_safe");
        }

        return new AudioSyncSafetyAssessment(
            AudioSyncSafetyClass.Reliable,
            false, false, true,
            "Farklı kurgu tam olarak doğrulandı; güvenli senkronlu ses hazır.",
            "hybrid_strictly_verified_no_missing_regions");
    }
}

public sealed record AudioSyncMediaSource(
    string Source,
    IReadOnlyDictionary<string, string>? HttpHeaders = null);

public sealed record AudioSyncRequest(
    AudioSyncMediaSource Reference,
    AudioSyncMediaSource ExternalAudio,
    double DurationSeconds,
    int MaxSearchSeconds = 45);

public sealed record AudioSyncPoint(
    double ReferenceSeconds,
    double DelaySeconds,
    double Correlation,
    double PeakMargin);

public sealed record AudioSyncRegion(
    double StartSeconds,
    double EndSeconds,
    double DelaySeconds);

public sealed record AudioSyncProgressiveRegion(
    double TargetStartSeconds,
    double TargetEndSeconds,
    double SourceStartSeconds,
    double SourceEndSeconds,
    double Rate,
    double Confidence,
    bool MissingSource,
    double? PlanResidualSeconds,
    double? VerifiedResidualSeconds,
    int VerifiedProbes,
    bool Trusted);

public sealed record AudioSyncPreparedAudio(
    string Path,
    double? VerifiedResidualSeconds,
    int VerifiedProbes,
    double? DriftAppliedMillisecondsPerMinute,
    string? FpsConversionApplied,
    int RegionsApplied,
    string Message,
    bool HybridRecovery = false,
    int HybridMissingRegions = 0,
    double HybridConfidence = 0,
    double HybridCoverage = 0,
    string RecoveryKind = "ast",
    int TimelineAnchors = 0,
    int VisualAnchors = 0,
    int WaveformAnchors = 0,
    int DtwRegions = 0,
    IReadOnlyList<AudioSyncProgressiveRegion>? ProgressiveRegions = null);

public sealed record AudioSyncResult(
    AudioSyncVerdict Verdict,
    AudioSyncModelKind Model,
    double BaseDelaySeconds,
    double DriftSecondsPerSecond,
    double DriftR2,
    double Confidence,
    IReadOnlyList<AudioSyncPoint> Points,
    IReadOnlyList<AudioSyncRegion> Regions,
    string Message,
    string Engine = "C# yedek motoru",
    double RawConfidence = 0,
    int UsedSegments = 0,
    int TotalSegments = 0,
    double? PhatRefinedSeconds = null,
    double PhatSharpness = 0,
    int PhatProbes = 0,
    double LagSpreadSeconds = 0,
    string? SuspectedFpsConversion = null,
    IReadOnlyList<string>? VerdictReasons = null)
{
    // Rev9.9: a changing audio-delay is intentionally forbidden. A fixed
    // model uses one constant delay; drift is corrected by a tempo filter.
    public bool CanApplyFixedDelay => Verdict == AudioSyncVerdict.Reliable &&
                                      Model == AudioSyncModelKind.Fixed &&
                                      string.IsNullOrWhiteSpace(SuspectedFpsConversion);

    public bool CanApplyLiveTempo => Verdict == AudioSyncVerdict.Reliable &&
                                     Model == AudioSyncModelKind.LinearDrift &&
                                     Regions.Count <= 1 &&
                                     string.IsNullOrWhiteSpace(SuspectedFpsConversion);

    public bool NeedsPreparedTrack =>
        Verdict is not AudioSyncVerdict.NoMatch and not AudioSyncVerdict.Unavailable &&
        (Model == AudioSyncModelKind.Piecewise || !string.IsNullOrWhiteSpace(SuspectedFpsConversion));

    // Kept for compatibility with older UI logic. It now means "safe to apply
    // without rewriting a track", not "recompute delay on every seek".
    public bool CanApplyLive => CanApplyFixedDelay || CanApplyLiveTempo;

    public double DriftMillisecondsPerMinute => DriftSecondsPerSecond * 60_000.0;

    // This is the exact factor used by AudioSyncTool FFmpegWrapper:
    // 1 - drift_ms_per_min / 60000.
    public double TempoFactor => Model == AudioSyncModelKind.LinearDrift
        ? Math.Clamp(1.0 - (DriftMillisecondsPerMinute / 60_000.0), 0.5, 2.0)
        : 1.0;

    // AudioSyncTool rescales the t=0 offset after applying the tempo stage.
    public double EffectiveBaseDelaySeconds => TempoFactor > 0
        ? BaseDelaySeconds / TempoFactor
        : BaseDelaySeconds;

    public double DelayAt(double positionSeconds)
    {
        var position = Math.Max(0, positionSeconds);
        if (Model == AudioSyncModelKind.LinearDrift)
        {
            return BaseDelaySeconds + (DriftSecondsPerSecond * position);
        }

        if (Model == AudioSyncModelKind.Piecewise && Regions.Count > 0)
        {
            var region = Regions.LastOrDefault(item => position >= item.StartSeconds && position < item.EndSeconds)
                         ?? Regions[^1];
            return region.DelaySeconds;
        }

        return BaseDelaySeconds;
    }

    public static AudioSyncResult Unavailable(string message) => new(
        AudioSyncVerdict.Unavailable,
        AudioSyncModelKind.Fixed,
        0,
        0,
        0,
        0,
        Array.Empty<AudioSyncPoint>(),
        Array.Empty<AudioSyncRegion>(),
        message,
        Engine: "Kullanılamıyor");
}
