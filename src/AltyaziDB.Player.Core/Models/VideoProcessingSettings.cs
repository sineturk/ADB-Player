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
    string TargetColorspaceHint = "auto",
    string TargetColorspaceHintMode = "target",
    string TargetTrc = "auto",
    string TargetPeak = "auto",
    string D3D11OutputColorSpace = "auto",
    string D3D11OutputFormat = "auto");
