using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AltyaziDB.Player.AudioSync.Models;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.AudioSync.Services;

internal sealed class AudioSyncToolWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IAppLogger _logger;
    private readonly string? _pythonPath;
    private readonly string? _workerPath;
    private readonly string? _sourcePath;
    private readonly string? _ffmpegDirectory;

    public AudioSyncToolWorkerClient(IAppLogger logger)
    {
        _logger = logger;
        var root = ResolveRuntimeRoot();
        if (root is null) return;

        var python = Path.Combine(root, "python", "python.exe");
        var worker = Path.Combine(root, "player_worker.py");
        var source = Path.Combine(root, "source");
        if (File.Exists(python) && File.Exists(worker) && Directory.Exists(Path.Combine(source, "audio_sync")))
        {
            _pythonPath = python;
            _workerPath = worker;
            _sourcePath = source;
            _ffmpegDirectory = ResolveFfmpegDirectory();
        }
    }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_pythonPath) &&
        !string.IsNullOrWhiteSpace(_workerPath) &&
        !string.IsNullOrWhiteSpace(_sourcePath);

    public bool CanRunFullSync => IsAvailable &&
                                  !string.IsNullOrWhiteSpace(_ffmpegDirectory) &&
                                  File.Exists(Path.Combine(_ffmpegDirectory, "ffmpeg.exe")) &&
                                  File.Exists(Path.Combine(_ffmpegDirectory, "ffprobe.exe"));

    public bool CanRunLiveProgressive => CanRunFullSync;

    public string EngineName => IsAvailable ? "AudioSyncTool 2.5" : "Hızlı C# yedek motoru";

    public async Task<AudioSyncToolWorkerResult?> AnalyzeAsync(
        string referencePcm,
        string externalPcm,
        int sampleRate,
        double skipIntroSeconds,
        int segments,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable || _pythonPath is null || _workerPath is null || _sourcePath is null)
        {
            return null;
        }

        using var process = CreateProcess();
        var args = process.StartInfo.ArgumentList;
        args.Add(_workerPath);
        args.Add("--reference");
        args.Add(referencePcm);
        args.Add("--external");
        args.Add(externalPcm);
        args.Add("--rate");
        args.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("--skip-intro");
        args.Add(skipIntroSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        args.Add("--segments");
        args.Add(segments.ToString(CultureInfo.InvariantCulture));

        StartOrThrow(process);
        using var registration = RegisterCancellation(process, cancellationToken);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        var json = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "AudioSyncTool worker geçerli JSON sonucu döndürmedi."
                    : TrimDiagnostic(stderr));
        }

        var result = JsonSerializer.Deserialize<AudioSyncToolWorkerResult>(json, JsonOptions);
        if (result is null)
        {
            throw new InvalidOperationException("AudioSyncTool worker sonucu okunamadı.");
        }

        if (!result.Ok || process.ExitCode != 0)
        {
            var detail = !string.IsNullOrWhiteSpace(result.Error)
                ? result.Error
                : (!string.IsNullOrWhiteSpace(stderr) ? TrimDiagnostic(stderr) : "AudioSyncTool analizi başarısız oldu.");
            throw new InvalidOperationException(detail);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _logger.Info($"AudioSyncTool worker diagnostic: {TrimDiagnostic(stderr)}");
        }

        return result;
    }

    public async Task<AudioSyncToolFullSyncResult> SynchronizeAsync(
        string referencePath,
        string externalAudioPath,
        string outputPath,
        double skipIntroSeconds,
        int segments,
        AudioSyncResult? analysisHint,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanRunFullSync || _pythonPath is null || _workerPath is null || _sourcePath is null)
        {
            throw new InvalidOperationException("AudioSyncTool tam düzeltme motoru veya FFmpeg kullanılamıyor.");
        }
        if (!File.Exists(referencePath) || !File.Exists(externalAudioPath))
        {
            throw new InvalidOperationException("Tam senkron düzeltmesi için video ve harici sesin yerel dosya olması gerekir.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var process = CreateProcess();
        var args = process.StartInfo.ArgumentList;
        args.Add(_workerPath);
        args.Add("--sync-reference");
        args.Add(Path.GetFullPath(referencePath));
        args.Add("--sync-external");
        args.Add(Path.GetFullPath(externalAudioPath));
        args.Add("--sync-output");
        args.Add(Path.GetFullPath(outputPath));
        args.Add("--skip-intro");
        args.Add(skipIntroSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        args.Add("--segments");
        args.Add(segments.ToString(CultureInfo.InvariantCulture));

        if (analysisHint is not null)
        {
            args.Add("--hint-delay-ms");
            args.Add((analysisHint.BaseDelaySeconds * 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("--hint-confidence");
            args.Add(analysisHint.RawConfidence.ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("--hint-regions-json");
            args.Add(JsonSerializer.Serialize(analysisHint.Regions.Select(region => new
            {
                start_sec = region.StartSeconds,
                end_sec = region.EndSeconds,
                lag_ms = region.DelaySeconds * 1000.0,
            })));
        }

        StartOrThrow(process);
        using var registration = RegisterCancellation(process, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        AudioSyncToolFullSyncResult? completed = null;

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            line = line.Trim();
            if (!line.StartsWith('{')) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventName = root.TryGetProperty("event", out var eventElement)
                    ? eventElement.GetString()
                    : null;
                if (string.Equals(eventName, "progress", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("progress", out var progressElement) &&
                    progressElement.TryGetInt32(out var value))
                {
                    progress?.Report(Math.Clamp(value, 0, 100));
                    continue;
                }
                if (string.Equals(eventName, "log", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("message", out var messageElement))
                {
                    var message = messageElement.GetString();
                    if (!string.IsNullOrWhiteSpace(message)) _logger.Info($"AudioSyncTool full-sync: {message}");
                    continue;
                }

                var parsed = JsonSerializer.Deserialize<AudioSyncToolFullSyncResult>(line, JsonOptions);
                if (parsed is not null) completed = parsed;
            }
            catch (JsonException exception)
            {
                _logger.Info($"AudioSyncTool full-sync JSON satırı okunamadı: {exception.Message}");
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (completed is null)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "AudioSyncTool tam düzeltme sonucu döndürmedi."
                    : TrimDiagnostic(stderr));
        }
        if (!completed.Ok || process.ExitCode != 0)
        {
            var detail = !string.IsNullOrWhiteSpace(completed.Error)
                ? completed.Error
                : (!string.IsNullOrWhiteSpace(stderr) ? TrimDiagnostic(stderr) : "AudioSyncTool tam düzeltmesi başarısız oldu.");
            throw new InvalidOperationException(detail);
        }
        var returnedOutputPath = string.IsNullOrWhiteSpace(completed.OutputPath)
            ? null
            : Path.GetFullPath(completed.OutputPath);
        var requestedOutputPath = Path.GetFullPath(outputPath);
        string? existingOutputPath = null;

        for (var attempt = 0; attempt < 8 && existingOutputPath is null; attempt++)
        {
            if (!string.IsNullOrWhiteSpace(returnedOutputPath) && File.Exists(returnedOutputPath))
                existingOutputPath = returnedOutputPath;
            else if (File.Exists(requestedOutputPath))
                existingOutputPath = requestedOutputPath;

            if (existingOutputPath is null)
                await Task.Delay(125, cancellationToken).ConfigureAwait(false);
        }

        if (existingOutputPath is null)
        {
            throw new InvalidOperationException(
                $"AudioSyncTool doğrulanmış senkron ses dosyasını oluşturmadı. " +
                $"Worker yolu: {returnedOutputPath ?? "(yok)"} · İstenen yol: {requestedOutputPath}");
        }

        if (!string.Equals(completed.OutputPath, existingOutputPath, StringComparison.OrdinalIgnoreCase))
            completed = completed with { OutputPath = existingOutputPath };

        progress?.Report(100);
        return completed;
    }

    public async Task<AudioSyncToolProgressiveRegion?> AnalyzeLiveProgressiveRegionAsync(
        string referencePath,
        string externalAudioPath,
        AudioSyncToolProgressiveRegion seedRegion,
        double targetPositionSeconds,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanRunLiveProgressive || _pythonPath is null || _workerPath is null || _sourcePath is null)
        {
            return null;
        }
        if (!File.Exists(referencePath) || !File.Exists(externalAudioPath))
        {
            return null;
        }

        using var process = CreateProcess();
        var args = process.StartInfo.ArgumentList;
        args.Add(_workerPath);
        args.Add("--live-reference");
        args.Add(Path.GetFullPath(referencePath));
        args.Add("--live-external");
        args.Add(Path.GetFullPath(externalAudioPath));
        args.Add("--live-position");
        args.Add(targetPositionSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        args.Add("--live-region-json");
        args.Add(JsonSerializer.Serialize(new
        {
            target_start_sec = seedRegion.TargetStartSec,
            target_end_sec = seedRegion.TargetEndSec,
            source_start_sec = seedRegion.SourceStartSec,
            source_end_sec = seedRegion.SourceEndSec,
            rate = seedRegion.Rate,
            confidence = seedRegion.Confidence,
            missing_source = seedRegion.MissingSource,
            plan_residual_ms = seedRegion.PlanResidualMs,
            verified_residual_ms = seedRegion.VerifiedResidualMs,
            verified_probes = seedRegion.VerifiedProbes,
            trusted = seedRegion.Trusted,
        }));

        StartOrThrow(process);
        using var registration = RegisterCancellation(process, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        AudioSyncToolLiveProgressiveResult? completed = null;

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            line = line.Trim();
            if (!line.StartsWith('{')) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventName = root.TryGetProperty("event", out var eventElement)
                    ? eventElement.GetString()
                    : null;
                if (string.Equals(eventName, "progress", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("progress", out var progressElement) &&
                    progressElement.TryGetInt32(out var value))
                {
                    progress?.Report(Math.Clamp(value, 0, 100));
                    continue;
                }
                if (string.Equals(eventName, "log", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("message", out var messageElement))
                {
                    var message = messageElement.GetString();
                    if (!string.IsNullOrWhiteSpace(message))
                        _logger.Info($"AudioSyncTool live-progressive: {message}");
                    continue;
                }

                var parsed = JsonSerializer.Deserialize<AudioSyncToolLiveProgressiveResult>(line, JsonOptions);
                if (parsed is not null) completed = parsed;
            }
            catch (JsonException exception)
            {
                _logger.Info($"AudioSyncTool live-progressive JSON satırı okunamadı: {exception.Message}");
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (completed is null)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "Live Progressive worker sonucu döndürmedi."
                    : TrimDiagnostic(stderr));
        }
        if (!completed.Ok || process.ExitCode != 0)
        {
            var detail = !string.IsNullOrWhiteSpace(completed.Error)
                ? completed.Error
                : (!string.IsNullOrWhiteSpace(stderr) ? TrimDiagnostic(stderr) : "Live Progressive analiz başarısız oldu.");
            throw new InvalidOperationException(detail);
        }

        return completed.Region;
    }

    private Process CreateProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _pythonPath!,
                WorkingDirectory = Path.GetDirectoryName(_workerPath!)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.Environment["PYTHONUTF8"] = "1";
        process.StartInfo.Environment["PYTHONNOUSERSITE"] = "1";
        process.StartInfo.Environment["PYTHONPATH"] = _sourcePath!;
        if (!string.IsNullOrWhiteSpace(_ffmpegDirectory))
        {
            var existing = process.StartInfo.Environment.TryGetValue("PATH", out var value)
                ? value
                : Environment.GetEnvironmentVariable("PATH");
            process.StartInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(existing)
                ? _ffmpegDirectory
                : _ffmpegDirectory + Path.PathSeparator + existing;
        }
        return process;
    }

    private static void StartOrThrow(Process process)
    {
        if (!process.Start()) throw new InvalidOperationException("AudioSyncTool worker başlatılamadı.");
    }

    private static CancellationTokenRegistration RegisterCancellation(Process process, CancellationToken cancellationToken)
    {
        return cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Worker may already have exited.
            }
        });
    }

    private static string? ResolveRuntimeRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", "audiosynctool"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "native", "audiosynctool")),
        };
        return candidates.FirstOrDefault(path => Directory.Exists(path));
    }

    private static string? ResolveFfmpegDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "bin"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "native", "ffmpeg", "bin")),
        };
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "ffmpeg.exe")));
    }

    private static string TrimDiagnostic(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 500 ? line : line[..500] + "…";
    }
}

internal sealed record AudioSyncToolWorkerResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("engine")] string? Engine,
    [property: JsonPropertyName("engine_version")] string? EngineVersion,
    [property: JsonPropertyName("worker_version")] string? WorkerVersion,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("delay_ms")] double DelayMs,
    [property: JsonPropertyName("coarse_ms")] double CoarseMs,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("used_segments")] int UsedSegments,
    [property: JsonPropertyName("total_segments")] int TotalSegments,
    [property: JsonPropertyName("drift_ms_per_min")] double? DriftMsPerMin,
    [property: JsonPropertyName("drift_intercept_ms")] double? DriftInterceptMs,
    [property: JsonPropertyName("drift_r2")] double? DriftR2,
    [property: JsonPropertyName("drift_span_sec")] double DriftSpanSec,
    [property: JsonPropertyName("lag_spread_ms")] double LagSpreadMs,
    [property: JsonPropertyName("suspected_fps_conversion")] string? SuspectedFpsConversion,
    [property: JsonPropertyName("phat_refined_ms")] double? PhatRefinedMs,
    [property: JsonPropertyName("phat_sharpness")] double PhatSharpness,
    [property: JsonPropertyName("phat_probes")] int PhatProbes,
    [property: JsonPropertyName("verdict_reasons")] string[]? VerdictReasons,
    [property: JsonPropertyName("regions")] AudioSyncToolWorkerRegion[]? Regions,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record AudioSyncToolWorkerRegion(
    [property: JsonPropertyName("start_sec")] double StartSec,
    [property: JsonPropertyName("end_sec")] double EndSec,
    [property: JsonPropertyName("lag_ms")] double LagMs,
    [property: JsonPropertyName("window_count")] int WindowCount,
    [property: JsonPropertyName("confidence")] double Confidence);

internal sealed record AudioSyncToolFullSyncResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("engine")] string? Engine,
    [property: JsonPropertyName("engine_version")] string? EngineVersion,
    [property: JsonPropertyName("worker_version")] string? WorkerVersion,
    [property: JsonPropertyName("output_path")] string? OutputPath,
    [property: JsonPropertyName("verified_residual_ms")] double? VerifiedResidualMs,
    [property: JsonPropertyName("verified_probes")] int VerifiedProbes,
    [property: JsonPropertyName("drift_applied_ms_per_min")] double? DriftAppliedMsPerMin,
    [property: JsonPropertyName("fps_conversion_applied")] string? FpsConversionApplied,
    [property: JsonPropertyName("regions_applied")] int RegionsApplied,
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("delay_ms")] double DelayMs,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("hybrid_recovery")] bool HybridRecovery,
    [property: JsonPropertyName("hybrid_missing_regions")] int HybridMissingRegions,
    [property: JsonPropertyName("hybrid_confidence")] double HybridConfidence,
    [property: JsonPropertyName("hybrid_coverage")] double HybridCoverage,
    [property: JsonPropertyName("recovery_kind")] string? RecoveryKind,
    [property: JsonPropertyName("timeline_anchors")] int TimelineAnchors,
    [property: JsonPropertyName("visual_anchors")] int VisualAnchors,
    [property: JsonPropertyName("waveform_anchors")] int WaveformAnchors,
    [property: JsonPropertyName("dtw_regions")] int DtwRegions,
    [property: JsonPropertyName("progressive_regions")] AudioSyncToolProgressiveRegion[]? ProgressiveRegions,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record AudioSyncToolProgressiveRegion(
    [property: JsonPropertyName("target_start_sec")] double TargetStartSec,
    [property: JsonPropertyName("target_end_sec")] double TargetEndSec,
    [property: JsonPropertyName("source_start_sec")] double SourceStartSec,
    [property: JsonPropertyName("source_end_sec")] double SourceEndSec,
    [property: JsonPropertyName("rate")] double Rate,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("missing_source")] bool MissingSource,
    [property: JsonPropertyName("plan_residual_ms")] double? PlanResidualMs,
    [property: JsonPropertyName("verified_residual_ms")] double? VerifiedResidualMs,
    [property: JsonPropertyName("verified_probes")] int VerifiedProbes,
    [property: JsonPropertyName("trusted")] bool Trusted);

internal sealed record AudioSyncToolLiveProgressiveResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("worker_version")] string? WorkerVersion,
    [property: JsonPropertyName("region")] AudioSyncToolProgressiveRegion? Region,
    [property: JsonPropertyName("error")] string? Error);
