namespace AltyaziDB.Player.Core.Models;

public sealed class SavedMediaAttachmentState
{
    public string MediaSource { get; set; } = string.Empty;
    public long? MediaLengthBytes { get; set; }
    public long? MediaLastWriteUtcTicks { get; set; }

    public string? PrimarySubtitleSource { get; set; }
    public string? PrimarySubtitlePlaybackPath { get; set; }
    public long? PrimarySubtitleLengthBytes { get; set; }
    public long? PrimarySubtitleLastWriteUtcTicks { get; set; }
    public bool PrimarySubtitleSyncFinalized { get; set; }

    public string? SecondarySubtitleSource { get; set; }
    public long? SecondarySubtitleLengthBytes { get; set; }
    public long? SecondarySubtitleLastWriteUtcTicks { get; set; }

    public string? ExternalAudioSource { get; set; }
    public string? ExternalAudioPlaybackPath { get; set; }
    public long? ExternalAudioLengthBytes { get; set; }
    public long? ExternalAudioLastWriteUtcTicks { get; set; }
    public bool AudioSyncFinalized { get; set; }
    public string AudioSyncVerdict { get; set; } = "Unavailable";
    public string AudioSyncModel { get; set; } = "Fixed";
    public string AudioSyncApplication { get; set; } = "None";
    public double AudioBaseDelaySeconds { get; set; }
    public double AudioDriftSecondsPerSecond { get; set; }
    public double AudioConfidence { get; set; }
    public double AudioAppliedDelaySeconds { get; set; }
    public double AudioTempoFactor { get; set; } = 1.0;
    public bool AudioHybridRecovery { get; set; }
    public int AudioHybridMissingRegions { get; set; }
    public double AudioHybridConfidence { get; set; }
    public double AudioHybridCoverage { get; set; }
    public string AudioRecoveryKind { get; set; } = "ast";
    public int AudioTimelineAnchors { get; set; }
    public int AudioVisualAnchors { get; set; }
    public int AudioWaveformAnchors { get; set; }
    public int AudioDtwRegions { get; set; }
    public double? AudioVerifiedResidualSeconds { get; set; }
    public int AudioVerifiedProbes { get; set; }
    public List<SavedAudioProgressiveRegionState> AudioProgressiveRegions { get; set; } = new();

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SavedAudioProgressiveRegionState
{
    public double TargetStartSeconds { get; set; }
    public double TargetEndSeconds { get; set; }
    public double SourceStartSeconds { get; set; }
    public double SourceEndSeconds { get; set; }
    public double Rate { get; set; } = 1.0;
    public double Confidence { get; set; }
    public bool MissingSource { get; set; }
    public double? PlanResidualSeconds { get; set; }
    public double? VerifiedResidualSeconds { get; set; }
    public int VerifiedProbes { get; set; }
    public bool Trusted { get; set; }
}
