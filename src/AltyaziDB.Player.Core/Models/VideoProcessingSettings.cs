namespace AltyaziDB.Player.Core.Models;

public sealed record VideoProcessingSettings(
    string ProfileCode,
    bool DebandEnabled,
    int DebandIterations,
    double DebandThreshold,
    double DebandRange,
    double DebandGrain,
    string ToneMapping,
    string GamutMappingMode,
    bool TargetColorspaceHint = true,
    string TargetColorspaceHintMode = "target",
    string TargetTrc = "auto",
    string TargetPeak = "auto");
