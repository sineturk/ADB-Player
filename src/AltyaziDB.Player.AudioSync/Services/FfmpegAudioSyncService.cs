using System.Diagnostics;
using System.Globalization;
using AltyaziDB.Player.AudioSync.Abstractions;
using AltyaziDB.Player.AudioSync.Models;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.AudioSync.Services;

public sealed class FfmpegAudioSyncService : IAudioSyncService
{
    private const int CoarseSampleRate = 8000;
    private const int CoarseFeatureRate = 100;
    private const int FineSampleRate = 16000;
    private const int AudioSyncToolSampleRate = 16000;
    private const int FineFeatureRate = 400;
    private const double CoarseWindowSeconds = 28.0;
    private const double FineWindowSeconds = 10.0;
    private const double FineSearchSeconds = 1.5;

    private readonly IAppLogger _logger;
    private readonly string? _ffmpegPath;
    private readonly AudioSyncToolWorkerClient _worker;

    public FfmpegAudioSyncService(IAppLogger logger)
    {
        _logger = logger;
        _ffmpegPath = ResolveExecutable("ffmpeg.exe");
        _worker = new AudioSyncToolWorkerClient(logger);
        IsAvailable = !string.IsNullOrWhiteSpace(_ffmpegPath);
        EngineName = _worker.EngineName;
        AvailabilityMessage = IsAvailable
            ? (_worker.IsAvailable
                ? "AudioSyncTool 2.5 otomatik ses senkronu hazır."
                : "AudioSyncTool worker bulunamadı; hızlı C# yedek motoru kullanılacak.")
            : "Otomatik ses senkronu için FFmpeg bileşeni bulunamadı.";
    }

    public bool IsAvailable { get; }
    public bool SupportsFullSync => _worker.CanRunFullSync;
    public bool SupportsLiveProgressiveSync => _worker.CanRunLiveProgressive;
    public string AvailabilityMessage { get; }
    public string EngineName { get; }

    public async Task<AudioSyncResult> AnalyzeAsync(
        AudioSyncRequest request,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable || string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return AudioSyncResult.Unavailable(AvailabilityMessage);
        }

        if (_worker.IsAvailable)
        {
            try
            {
                var workerResult = await AnalyzeWithAudioSyncToolAsync(request, progress, cancellationToken).ConfigureAwait(false);
                if (workerResult is not null) return workerResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error("AudioSyncTool 2.5 analizi başarısız oldu; hızlı yedek motor deneniyor.", exception);
                progress?.Report(4);
            }
        }

        return await AnalyzeFastFallbackAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioSyncPreparedAudio?> PrepareCorrectedAudioAsync(
        AudioSyncRequest request,
        string outputDirectory,
        AudioSyncResult? analysisHint = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!SupportsFullSync)
        {
            throw new InvalidOperationException("AudioSyncTool tam düzeltme motoru kullanılamıyor.");
        }
        if (!File.Exists(request.Reference.Source) || !File.Exists(request.ExternalAudio.Source))
        {
            throw new InvalidOperationException("Tam AST düzeltmesi için video ve harici sesin yerel dosya olması gerekir.");
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(
            outputDirectory,
            $"audiosync-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.flac");

        var result = await _worker.SynchronizeAsync(
            request.Reference.Source,
            request.ExternalAudio.Source,
            outputPath,
            skipIntroSeconds: 120.0,
            segments: 12,
            analysisHint,
            progress,
            cancellationToken).ConfigureAwait(false);

        double? residualSeconds = result.VerifiedResidualMs is null
            ? null
            : result.VerifiedResidualMs.Value / 1000.0;
        var verification = residualSeconds is null
            ? "çıktı doğrulaması yapılamadı"
            : $"kalan fark {result.VerifiedResidualMs:+0.0;-0.0;0.0} ms / {result.VerifiedProbes} probe";
        var details = new List<string> { verification };
        if (result.DriftAppliedMsPerMin is not null)
            details.Add($"drift {result.DriftAppliedMsPerMin:+0.00;-0.00;0.00} ms/dk");
        if (!string.IsNullOrWhiteSpace(result.FpsConversionApplied))
            details.Add($"FPS {result.FpsConversionApplied}");
        if (result.RegionsApplied > 0)
            details.Add($"{result.RegionsApplied} kurgu bölgesi");
        if (result.HybridRecovery)
        {
            var recoveryLabel = result.RecoveryKind?.ToLowerInvariant() switch
            {
                "multimodal" => "Multi-Modal Timeline Sync",
                "audio_timeline" => "Waveform Timeline Sync",
                "visual" => "Visual Timeline Recovery",
                _ => "Hybrid Edit Recovery",
            };
            details.Add($"{recoveryLabel} %{result.HybridCoverage * 100:0}");
            if (result.TimelineAnchors > 0)
                details.Add($"{result.TimelineAnchors} timeline anchor");
            if (result.VisualAnchors > 0)
                details.Add($"{result.VisualAnchors} görsel");
            if (result.WaveformAnchors > 0)
                details.Add($"{result.WaveformAnchors} waveform");
            if (result.DtwRegions > 0)
                details.Add($"{result.DtwRegions} DTW bölgesi");
            if (result.HybridMissingRegions > 0)
                details.Add($"{result.HybridMissingRegions} eksik sahne/orijinal ses");
        }

        var progressiveRegions = (result.ProgressiveRegions ?? Array.Empty<AudioSyncToolProgressiveRegion>())
            .Select(region => new AudioSyncProgressiveRegion(
                region.TargetStartSec,
                region.TargetEndSec,
                region.SourceStartSec,
                region.SourceEndSec,
                region.Rate,
                region.Confidence,
                region.MissingSource,
                region.PlanResidualMs is null ? null : region.PlanResidualMs.Value / 1000.0,
                region.VerifiedResidualMs is null ? null : region.VerifiedResidualMs.Value / 1000.0,
                region.VerifiedProbes,
                region.Trusted))
            .ToArray();
        var progressiveTrusted = progressiveRegions.Count(region => region.Trusted);
        if (progressiveRegions.Length > 0)
            details.Add($"Progressive {progressiveTrusted}/{progressiveRegions.Count(region => !region.MissingSource)} doğrulanmış bölge");

        var messagePrefix = result.RecoveryKind?.ToLowerInvariant() switch
        {
            "multimodal" => "AST Multi-Modal Timeline Sync hazır",
            "audio_timeline" => "AST Waveform Timeline Sync hazır",
            "visual" => "AST Visual Timeline Recovery hazır",
            _ when result.HybridRecovery => "AST Hybrid Edit Recovery hazır",
            _ => "AudioSyncTool tam düzeltme hazır",
        };

        return new AudioSyncPreparedAudio(
            result.OutputPath!,
            residualSeconds,
            result.VerifiedProbes,
            result.DriftAppliedMsPerMin,
            result.FpsConversionApplied,
            result.RegionsApplied,
            $"{messagePrefix} · {string.Join(" · ", details)}",
            result.HybridRecovery,
            result.HybridMissingRegions,
            result.HybridConfidence,
            result.HybridCoverage,
            result.RecoveryKind ?? "ast",
            result.TimelineAnchors,
            result.VisualAnchors,
            result.WaveformAnchors,
            result.DtwRegions,
            progressiveRegions);
    }

    public async Task<AudioSyncProgressiveRegion?> AnalyzeLiveProgressiveRegionAsync(
        AudioSyncRequest request,
        AudioSyncProgressiveRegion seedRegion,
        double targetPositionSeconds,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(seedRegion);

        if (!SupportsLiveProgressiveSync ||
            !File.Exists(request.Reference.Source) ||
            !File.Exists(request.ExternalAudio.Source) ||
            seedRegion.MissingSource)
        {
            return null;
        }

        var workerSeed = new AudioSyncToolProgressiveRegion(
            seedRegion.TargetStartSeconds,
            seedRegion.TargetEndSeconds,
            seedRegion.SourceStartSeconds,
            seedRegion.SourceEndSeconds,
            seedRegion.Rate,
            seedRegion.Confidence,
            seedRegion.MissingSource,
            seedRegion.PlanResidualSeconds is null ? null : seedRegion.PlanResidualSeconds.Value * 1000.0,
            seedRegion.VerifiedResidualSeconds is null ? null : seedRegion.VerifiedResidualSeconds.Value * 1000.0,
            seedRegion.VerifiedProbes,
            seedRegion.Trusted);

        var result = await _worker.AnalyzeLiveProgressiveRegionAsync(
            request.Reference.Source,
            request.ExternalAudio.Source,
            workerSeed,
            targetPositionSeconds,
            progress,
            cancellationToken).ConfigureAwait(false);

        return result is null
            ? null
            : new AudioSyncProgressiveRegion(
                result.TargetStartSec,
                result.TargetEndSec,
                result.SourceStartSec,
                result.SourceEndSec,
                result.Rate,
                result.Confidence,
                result.MissingSource,
                result.PlanResidualMs is null ? null : result.PlanResidualMs.Value / 1000.0,
                result.VerifiedResidualMs is null ? null : result.VerifiedResidualMs.Value / 1000.0,
                result.VerifiedProbes,
                result.Trusted);
    }

    private async Task<AudioSyncResult> AnalyzeFastFallbackAsync(
        AudioSyncRequest request,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable || string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return AudioSyncResult.Unavailable(AvailabilityMessage);
        }

        if (string.IsNullOrWhiteSpace(request.Reference.Source) ||
            string.IsNullOrWhiteSpace(request.ExternalAudio.Source))
        {
            return AudioSyncResult.Unavailable("Senkron için video ve harici ses kaynağı gerekli.");
        }

        var duration = Math.Max(0, request.DurationSeconds);
        if (duration < 45)
        {
            return new AudioSyncResult(
                AudioSyncVerdict.Uncertain,
                AudioSyncModelKind.Fixed,
                0, 0, 0, 0,
                Array.Empty<AudioSyncPoint>(),
                Array.Empty<AudioSyncRegion>(),
                "İçerik otomatik senkron analizi için çok kısa.");
        }

        try
        {
            progress?.Report(2);
            var searchSeconds = Math.Clamp(request.MaxSearchSeconds, 5, 120);
            var initialPositions = BuildInitialPositions(duration);
            var points = new List<AudioSyncPoint>();

            for (var i = 0; i < initialPositions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var point = await AnalyzePointAsync(
                    request,
                    initialPositions[i],
                    searchSeconds,
                    cancellationToken).ConfigureAwait(false);
                if (point is not null)
                {
                    points.Add(point);
                }
                progress?.Report(8 + (int)Math.Round((i + 1) * 48.0 / initialPositions.Count));
            }

            if (points.Count < 2)
            {
                return CreateNoMatch(points, "Sesler arasında güvenilir ortak zaman noktası bulunamadı.");
            }

            var firstAssessment = Assess(points, duration);
            if (firstAssessment.Verdict == AudioSyncVerdict.Reliable && firstAssessment.Model == AudioSyncModelKind.Fixed)
            {
                progress?.Report(100);
                return firstAssessment;
            }

            // Drift veya kurgu farkı şüphesinde iki ek noktayı ölç. Böylece tek
            // hatalı pencerenin bütün film için karar vermesi önlenir.
            var extraPositions = BuildExtraPositions(duration)
                .Where(position => points.All(point => Math.Abs(point.ReferenceSeconds - position) > 20))
                .ToArray();

            for (var i = 0; i < extraPositions.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var point = await AnalyzePointAsync(
                    request,
                    extraPositions[i],
                    searchSeconds,
                    cancellationToken).ConfigureAwait(false);
                if (point is not null)
                {
                    points.Add(point);
                }
                progress?.Report(62 + (int)Math.Round((i + 1) * 30.0 / Math.Max(1, extraPositions.Length)));
            }

            progress?.Report(96);
            var result = Assess(points, duration);
            progress?.Report(100);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Otomatik ses senkron analizi başarısız oldu.", exception);
            return new AudioSyncResult(
                AudioSyncVerdict.Uncertain,
                AudioSyncModelKind.Fixed,
                0, 0, 0, 0,
                Array.Empty<AudioSyncPoint>(),
                Array.Empty<AudioSyncRegion>(),
                "Otomatik ses senkronu tamamlanamadı.");
        }
    }

    private async Task<AudioSyncResult?> AnalyzeWithAudioSyncToolAsync(
        AudioSyncRequest request,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!_worker.IsAvailable || string.IsNullOrWhiteSpace(_ffmpegPath)) return null;
        if (string.IsNullOrWhiteSpace(request.Reference.Source) ||
            string.IsNullOrWhiteSpace(request.ExternalAudio.Source))
        {
            return AudioSyncResult.Unavailable("Senkron için video ve harici ses kaynağı gerekli.");
        }

        var duration = Math.Max(0, request.DurationSeconds);
        if (duration < 45)
        {
            return new AudioSyncResult(
                AudioSyncVerdict.Uncertain,
                AudioSyncModelKind.Fixed,
                0, 0, 0, 0,
                Array.Empty<AudioSyncPoint>(),
                Array.Empty<AudioSyncRegion>(),
                "İçerik AudioSyncTool analizi için çok kısa.");
        }

        var workRoot = Path.Combine(
            Path.GetTempPath(),
            "AltyaziDB-Player",
            "audio-sync",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        var referencePcm = Path.Combine(workRoot, "reference.s16le");
        var externalPcm = Path.Combine(workRoot, "external.s16le");

        try
        {
            progress?.Report(3);
            await DecodeToPcmFileAsync(
                request.Reference,
                referencePcm,
                AudioSyncToolSampleRate,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(24);

            await DecodeToPcmFileAsync(
                request.ExternalAudio,
                externalPcm,
                AudioSyncToolSampleRate,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(46);

            var skipIntro = duration >= 360 ? 120.0 : Math.Min(30.0, duration * 0.08);
            var segments = duration >= 1800 ? 12 : 8;
            var worker = await _worker.AnalyzeAsync(
                referencePcm,
                externalPcm,
                AudioSyncToolSampleRate,
                skipIntro,
                segments,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(94);
            if (worker is null) return null;

            var result = ConvertAudioSyncToolResult(worker, duration);
            _logger.Info(
                $"AudioSyncTool sonucu: verdict={result.Verdict}, model={result.Model}, " +
                $"delay={result.BaseDelaySeconds * 1000:+0.0;-0.0;0.0} ms, " +
                $"drift={result.DriftMillisecondsPerMinute:+0.00;-0.00;0.00} ms/dk, " +
                $"confidence={result.RawConfidence:0.00}, phat={result.PhatProbes}, " +
                $"spread={result.LagSpreadSeconds * 1000:0.0} ms.");
            progress?.Report(100);
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
            }
            catch (Exception cleanupError)
            {
                _logger.Info($"AudioSyncTool geçici dosyaları temizlenemedi: {cleanupError.Message}");
            }
        }
    }

    private async Task DecodeToPcmFileAsync(
        AudioSyncMediaSource source,
        string outputPath,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            throw new InvalidOperationException("FFmpeg bulunamadı.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        var args = process.StartInfo.ArgumentList;
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");
        args.Add("-nostdin");
        var headers = BuildFfmpegHeaders(source.HttpHeaders);
        if (!string.IsNullOrWhiteSpace(headers))
        {
            args.Add("-headers");
            args.Add(headers);
        }
        args.Add("-i");
        args.Add(source.Source);
        args.Add("-map");
        args.Add("0:a:0");
        args.Add("-vn");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-ac");
        args.Add("1");
        args.Add("-ar");
        args.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-c:a");
        args.Add("pcm_s16le");
        args.Add("-f");
        args.Add("s16le");
        args.Add("-y");
        args.Add(outputPath);

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg AudioSyncTool PCM çözümleyicisi başlatılamadı.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Cancellation path: process may already have exited.
            }
        });

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "FFmpeg AudioSyncTool için PCM çıkaramadı."
                    : TrimDiagnostic(error));
        }

        var info = new FileInfo(outputPath);
        if (!info.Exists || info.Length < sampleRate * 2L * 10L)
        {
            throw new InvalidOperationException("AudioSyncTool için yeterli PCM ses üretilemedi.");
        }
    }

    private static AudioSyncResult ConvertAudioSyncToolResult(
        AudioSyncToolWorkerResult worker,
        double duration)
    {
        var verdict = (worker.Verdict ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "reliable" => AudioSyncVerdict.Reliable,
            "no_match" => AudioSyncVerdict.NoMatch,
            "nomatch" => AudioSyncVerdict.NoMatch,
            _ => AudioSyncVerdict.Uncertain,
        };

        var regions = (worker.Regions ?? Array.Empty<AudioSyncToolWorkerRegion>())
            .Select(region => new AudioSyncRegion(
                Math.Max(0, region.StartSec),
                Math.Max(region.StartSec, region.EndSec > 0 ? region.EndSec : duration),
                region.LagMs / 1000.0))
            .ToArray();

        var driftMsPerMin = worker.DriftMsPerMin ?? 0.0;
        var driftR2 = Math.Clamp(worker.DriftR2 ?? 0.0, 0.0, 1.0);
        var model = regions.Length > 1
            ? AudioSyncModelKind.Piecewise
            : Math.Abs(driftMsPerMin) >= 0.8 && driftR2 >= 0.80
                ? AudioSyncModelKind.LinearDrift
                : AudioSyncModelKind.Fixed;

        var baseDelayMs = model == AudioSyncModelKind.LinearDrift && worker.DriftInterceptMs is not null
            ? worker.DriftInterceptMs.Value
            : worker.PhatRefinedMs ?? worker.DelayMs;
        var driftSecondsPerSecond = model == AudioSyncModelKind.LinearDrift
            ? driftMsPerMin / 60_000.0
            : 0.0;
        var normalizedConfidence = Math.Clamp(worker.Confidence / 10.0, 0.0, 1.0);

        var fpsSuffix = string.IsNullOrWhiteSpace(worker.SuspectedFpsConversion)
            ? string.Empty
            : $" · FPS farkı: {worker.SuspectedFpsConversion}";
        var phatSuffix = worker.PhatProbes > 0
            ? $" · GCC-PHAT {worker.PhatProbes} nokta"
            : string.Empty;

        string message;
        if (verdict == AudioSyncVerdict.NoMatch)
        {
            message = "AudioSyncTool: video ile harici ses eşleşmedi; otomatik gecikme uygulanmadı.";
        }
        else if (model == AudioSyncModelKind.Piecewise)
        {
            message = $"AudioSyncTool: farklı kurgu/ofset sıçraması algılandı ({regions.Length} bölge); canlı düzeltme uygulanmadı.{fpsSuffix}";
        }
        else if (verdict != AudioSyncVerdict.Reliable)
        {
            message = $"AudioSyncTool sonucu şüpheli; ses değiştirilmedi. Ölçüm {worker.DelayMs / 1000.0:+0.000;-0.000;0.000} sn.{phatSuffix}{fpsSuffix}";
        }
        else if (model == AudioSyncModelKind.LinearDrift)
        {
            message = $"AudioSyncTool 2.5: {baseDelayMs / 1000.0:+0.000;-0.000;0.000} sn başlangıç ofseti, {driftMsPerMin:+0.00;-0.00;0.00} ms/dk drift canlı düzeltiliyor.{phatSuffix}{fpsSuffix}";
        }
        else
        {
            message = $"AudioSyncTool 2.5: ses otomatik senkronize edildi ({baseDelayMs / 1000.0:+0.000;-0.000;0.000} sn). Güven {worker.Confidence:0.00}.{phatSuffix}{fpsSuffix}";
        }

        return new AudioSyncResult(
            verdict,
            model,
            baseDelayMs / 1000.0,
            driftSecondsPerSecond,
            driftR2,
            normalizedConfidence,
            Array.Empty<AudioSyncPoint>(),
            regions,
            message,
            Engine: string.IsNullOrWhiteSpace(worker.EngineVersion)
                ? "AudioSyncTool"
                : $"AudioSyncTool {worker.EngineVersion}",
            RawConfidence: worker.Confidence,
            UsedSegments: worker.UsedSegments,
            TotalSegments: worker.TotalSegments,
            PhatRefinedSeconds: worker.PhatRefinedMs is null ? null : worker.PhatRefinedMs.Value / 1000.0,
            PhatSharpness: worker.PhatSharpness,
            PhatProbes: worker.PhatProbes,
            LagSpreadSeconds: worker.LagSpreadMs / 1000.0,
            SuspectedFpsConversion: worker.SuspectedFpsConversion,
            VerdictReasons: worker.VerdictReasons ?? Array.Empty<string>());
    }

    private async Task<AudioSyncPoint?> AnalyzePointAsync(
        AudioSyncRequest request,
        double referenceStart,
        int searchSeconds,
        CancellationToken cancellationToken)
    {
        var coarseReference = await DecodeWindowAsync(
            request.Reference,
            referenceStart,
            CoarseWindowSeconds,
            CoarseSampleRate,
            cancellationToken).ConfigureAwait(false);

        var candidateStart = Math.Max(0, referenceStart - searchSeconds);
        var candidateEnd = Math.Min(
            Math.Max(request.DurationSeconds + searchSeconds, referenceStart + CoarseWindowSeconds + searchSeconds),
            referenceStart + CoarseWindowSeconds + (searchSeconds * 2.0));
        var candidateDuration = Math.Max(CoarseWindowSeconds + 2, candidateEnd - candidateStart);

        var coarseCandidate = await DecodeWindowAsync(
            request.ExternalAudio,
            candidateStart,
            candidateDuration,
            CoarseSampleRate,
            cancellationToken).ConfigureAwait(false);

        var referenceFeatures = BuildFeatures(coarseReference, CoarseSampleRate, CoarseFeatureRate);
        var candidateFeatures = BuildFeatures(coarseCandidate, CoarseSampleRate, CoarseFeatureRate);
        var coarse = FindBestAlignment(referenceFeatures, candidateFeatures, CoarseFeatureRate, exclusionSeconds: 0.75);
        if (coarse is null || coarse.Value.Correlation < 0.10 || coarse.Value.PeakMargin < 0.006)
        {
            return null;
        }

        var alignedExternalStart = candidateStart + coarse.Value.OffsetSeconds;
        var coarseDelay = referenceStart - alignedExternalStart;

        // Kaba sonucu ham sesin daha yoğun örneklenmiş özelliğiyle ±1.5 sn içinde
        // hassaslaştır. Bu ikinci aşama AudioSyncTool'daki coarse/fine yaklaşımını
        // Player'a bağımlılıksız biçimde uygular.
        var fineReferenceStart = Math.Min(
            Math.Max(referenceStart + 8, 0),
            Math.Max(0, request.DurationSeconds - FineWindowSeconds - 1));
        var expectedExternalStart = Math.Max(0, fineReferenceStart - coarseDelay);
        var fineCandidateStart = Math.Max(0, expectedExternalStart - FineSearchSeconds);

        var fineReference = await DecodeWindowAsync(
            request.Reference,
            fineReferenceStart,
            FineWindowSeconds,
            FineSampleRate,
            cancellationToken).ConfigureAwait(false);
        var fineCandidate = await DecodeWindowAsync(
            request.ExternalAudio,
            fineCandidateStart,
            FineWindowSeconds + (FineSearchSeconds * 2),
            FineSampleRate,
            cancellationToken).ConfigureAwait(false);

        var fineRefFeatures = BuildFeatures(fineReference, FineSampleRate, FineFeatureRate);
        var fineCandidateFeatures = BuildFeatures(fineCandidate, FineSampleRate, FineFeatureRate);
        var fine = FindBestAlignment(fineRefFeatures, fineCandidateFeatures, FineFeatureRate, exclusionSeconds: 0.18);

        if (fine is not null && fine.Value.Correlation >= 0.09 && fine.Value.PeakMargin >= 0.004)
        {
            var fineExternalStart = fineCandidateStart + fine.Value.OffsetSeconds;
            var fineDelay = fineReferenceStart - fineExternalStart;
            if (Math.Abs(fineDelay - coarseDelay) <= 1.25)
            {
                return new AudioSyncPoint(
                    referenceStart,
                    fineDelay,
                    Math.Max(coarse.Value.Correlation, fine.Value.Correlation),
                    Math.Max(coarse.Value.PeakMargin, fine.Value.PeakMargin));
            }
        }

        return new AudioSyncPoint(
            referenceStart,
            coarseDelay,
            coarse.Value.Correlation,
            coarse.Value.PeakMargin);
    }

    private async Task<short[]> DecodeWindowAsync(
        AudioSyncMediaSource source,
        double startSeconds,
        double durationSeconds,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            throw new InvalidOperationException("FFmpeg bulunamadı.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        var args = process.StartInfo.ArgumentList;
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");
        args.Add("-nostdin");
        args.Add("-ss");
        args.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        var headers = BuildFfmpegHeaders(source.HttpHeaders);
        if (!string.IsNullOrWhiteSpace(headers))
        {
            args.Add("-headers");
            args.Add(headers);
        }

        args.Add("-i");
        args.Add(source.Source);
        args.Add("-t");
        args.Add(durationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        args.Add("-map");
        args.Add("0:a:0");
        args.Add("-vn");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-ac");
        args.Add("1");
        args.Add("-ar");
        args.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-c:a");
        args.Add("pcm_s16le");
        args.Add("-f");
        args.Add("s16le");
        args.Add("pipe:1");

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg başlatılamadı.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Cancellation path: process may already have exited.
            }
        });

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "FFmpeg ses örneği çıkaramadı." : TrimDiagnostic(error));
        }

        var bytes = output.ToArray();
        if (bytes.Length < sampleRate * 2)
        {
            throw new InvalidOperationException("Senkron analizi için yeterli ses örneği alınamadı.");
        }

        var sampleCount = bytes.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * 2);
        return samples;
    }

    private static double[] BuildFeatures(short[] samples, int sampleRate, int featureRate)
    {
        var block = Math.Max(1, sampleRate / featureRate);
        var count = samples.Length / block;
        if (count < 10) return Array.Empty<double>();

        var features = new double[count];
        short previous = samples[0];
        for (var frame = 0; frame < count; frame++)
        {
            var start = frame * block;
            var end = Math.Min(samples.Length, start + block);
            double abs = 0;
            double diff = 0;
            for (var i = start; i < end; i++)
            {
                var current = samples[i];
                abs += Math.Abs((double)current);
                diff += Math.Abs((double)current - previous);
                previous = current;
            }

            var length = Math.Max(1, end - start);
            var energy = Math.Log(1.0 + ((abs / length) / 320.0));
            var transient = Math.Log(1.0 + ((diff / length) / 250.0));
            features[frame] = (energy * 0.55) + (transient * 0.45);
        }

        // Slow loudness changes are less useful than attacks, cuts, speech onsets
        // and effects. Differencing improves language-independent alignment.
        var derivative = new double[features.Length];
        derivative[0] = 0;
        for (var i = 1; i < features.Length; i++)
        {
            derivative[i] = Math.Max(0, features[i] - features[i - 1]);
        }

        NormalizeInPlace(derivative);
        return derivative;
    }

    private static AlignmentCandidate? FindBestAlignment(
        double[] reference,
        double[] candidate,
        int featureRate,
        double exclusionSeconds)
    {
        if (reference.Length < 20 || candidate.Length < reference.Length + 2)
        {
            return null;
        }

        var refMean = reference.Average();
        var refCentered = new double[reference.Length];
        double refEnergy = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            var value = reference[i] - refMean;
            refCentered[i] = value;
            refEnergy += value * value;
        }
        if (refEnergy < 1e-8) return null;

        var prefix = new double[candidate.Length + 1];
        var prefixSq = new double[candidate.Length + 1];
        for (var i = 0; i < candidate.Length; i++)
        {
            prefix[i + 1] = prefix[i] + candidate[i];
            prefixSq[i + 1] = prefixSq[i] + (candidate[i] * candidate[i]);
        }

        var scores = new double[candidate.Length - reference.Length + 1];
        var bestScore = double.NegativeInfinity;
        var bestIndex = 0;
        for (var lag = 0; lag < scores.Length; lag++)
        {
            var sum = prefix[lag + reference.Length] - prefix[lag];
            var sumSq = prefixSq[lag + reference.Length] - prefixSq[lag];
            var candidateEnergy = sumSq - ((sum * sum) / reference.Length);
            if (candidateEnergy <= 1e-10)
            {
                scores[lag] = -1;
                continue;
            }

            double dot = 0;
            for (var i = 0; i < reference.Length; i++)
            {
                dot += refCentered[i] * candidate[lag + i];
            }

            var score = dot / Math.Sqrt(refEnergy * candidateEnergy);
            scores[lag] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = lag;
            }
        }

        var exclusion = Math.Max(1, (int)Math.Round(exclusionSeconds * featureRate));
        var secondBest = double.NegativeInfinity;
        for (var i = 0; i < scores.Length; i++)
        {
            if (Math.Abs(i - bestIndex) <= exclusion) continue;
            secondBest = Math.Max(secondBest, scores[i]);
        }
        if (double.IsNegativeInfinity(secondBest)) secondBest = bestScore;

        return new AlignmentCandidate(
            bestIndex / (double)featureRate,
            bestScore,
            Math.Max(0, bestScore - secondBest));
    }

    private static AudioSyncResult Assess(IReadOnlyList<AudioSyncPoint> rawPoints, double duration)
    {
        var points = rawPoints
            .Where(point => point.Correlation >= 0.09 && point.PeakMargin >= 0.003)
            .OrderBy(point => point.ReferenceSeconds)
            .ToArray();
        if (points.Length < 2)
        {
            return CreateNoMatch(points, "Ses eşleşmesi doğrulanamadı.");
        }

        var meanCorrelation = points.Average(point => point.Correlation);
        var meanMargin = points.Average(point => point.PeakMargin);
        var confidence = Math.Clamp(((meanCorrelation - 0.08) * 4.0) + (meanMargin * 12.0), 0, 1);
        var delays = points.Select(point => point.DelaySeconds).OrderBy(value => value).ToArray();
        var median = Median(delays);
        var spread = delays[^1] - delays[0];

        if (meanCorrelation < 0.105 || confidence < 0.12)
        {
            return CreateNoMatch(points, "Video ile harici ses aynı içerik olarak doğrulanamadı.");
        }

        if (spread <= 0.085)
        {
            return new AudioSyncResult(
                AudioSyncVerdict.Reliable,
                AudioSyncModelKind.Fixed,
                median,
                0,
                1,
                Math.Max(confidence, 0.65),
                points,
                Array.Empty<AudioSyncRegion>(),
                $"Ses otomatik senkronize edildi ({median:+0.000;-0.000;0.000} sn)." );
        }

        var regression = FitLine(points);
        var maxResidual = points.Max(point =>
            Math.Abs(point.DelaySeconds - (regression.Intercept + (regression.Slope * point.ReferenceSeconds))));
        var driftMsPerMin = regression.Slope * 60_000.0;

        if (points.Length >= 3 &&
            regression.R2 >= 0.78 &&
            maxResidual <= 0.12 &&
            Math.Abs(driftMsPerMin) >= 0.8)
        {
            return new AudioSyncResult(
                AudioSyncVerdict.Reliable,
                AudioSyncModelKind.LinearDrift,
                regression.Intercept,
                regression.Slope,
                regression.R2,
                Math.Max(confidence, 0.58),
                points,
                Array.Empty<AudioSyncRegion>(),
                "Ses gecikmesi ve zaman kayması otomatik düzeltiliyor.");
        }

        if (points.Length >= 4 && spread >= 0.14)
        {
            var regions = BuildRegions(points, duration);
            return new AudioSyncResult(
                AudioSyncVerdict.Uncertain,
                AudioSyncModelKind.Piecewise,
                median,
                0,
                regression.R2,
                confidence,
                points,
                regions,
                "Ses farklı bir kurguya ait olabilir. Otomatik canlı düzeltme uygulanmadı.");
        }

        return new AudioSyncResult(
            AudioSyncVerdict.Uncertain,
            AudioSyncModelKind.Fixed,
            median,
            0,
            regression.R2,
            confidence,
            points,
            Array.Empty<AudioSyncRegion>(),
            "Otomatik senkron sonucu yeterince güvenilir değil; ses değiştirilmedi.");
    }

    private static AudioSyncResult CreateNoMatch(IReadOnlyList<AudioSyncPoint> points, string message) => new(
        AudioSyncVerdict.NoMatch,
        AudioSyncModelKind.Fixed,
        0, 0, 0, 0,
        points,
        Array.Empty<AudioSyncRegion>(),
        message);

    private static IReadOnlyList<AudioSyncRegion> BuildRegions(IReadOnlyList<AudioSyncPoint> points, double duration)
    {
        var ordered = points.OrderBy(point => point.ReferenceSeconds).ToArray();
        var regions = new List<AudioSyncRegion>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var start = i == 0 ? 0 : (ordered[i - 1].ReferenceSeconds + ordered[i].ReferenceSeconds) / 2.0;
            var end = i == ordered.Length - 1
                ? Math.Max(duration, ordered[i].ReferenceSeconds + 1)
                : (ordered[i].ReferenceSeconds + ordered[i + 1].ReferenceSeconds) / 2.0;
            regions.Add(new AudioSyncRegion(start, end, ordered[i].DelaySeconds));
        }
        return regions;
    }

    private static LineFit FitLine(IReadOnlyList<AudioSyncPoint> points)
    {
        var xMean = points.Average(point => point.ReferenceSeconds);
        var yMean = points.Average(point => point.DelaySeconds);
        double covariance = 0;
        double variance = 0;
        double total = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var dx = points[i].ReferenceSeconds - xMean;
            var dy = points[i].DelaySeconds - yMean;
            covariance += dx * dy;
            variance += dx * dx;
            total += dy * dy;
        }

        var slope = variance <= 1e-12 ? 0 : covariance / variance;
        var intercept = yMean - (slope * xMean);
        double residual = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var error = points[i].DelaySeconds - (intercept + (slope * points[i].ReferenceSeconds));
            residual += error * error;
        }
        var r2 = total <= 1e-12 ? 1 : Math.Clamp(1 - (residual / total), 0, 1);
        return new LineFit(intercept, slope, r2);
    }

    private static IReadOnlyList<double> BuildInitialPositions(double duration)
    {
        var latest = Math.Max(5, duration - CoarseWindowSeconds - 2);
        var values = duration < 240
            ? new[] { duration * 0.20, duration * 0.58 }
            : new[] { duration * 0.15, duration * 0.50, duration * 0.84 };
        return values
            .Select(value => Math.Clamp(value, 5, latest))
            .DistinctBy(value => Math.Round(value, 1))
            .ToArray();
    }

    private static IReadOnlyList<double> BuildExtraPositions(double duration)
    {
        var latest = Math.Max(5, duration - CoarseWindowSeconds - 2);
        return new[] { duration * 0.30, duration * 0.70 }
            .Select(value => Math.Clamp(value, 5, latest))
            .ToArray();
    }

    private static void NormalizeInPlace(double[] values)
    {
        if (values.Length == 0) return;
        var mean = values.Average();
        double energy = 0;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] -= mean;
            energy += values[i] * values[i];
        }
        var scale = Math.Sqrt(energy / Math.Max(1, values.Length));
        if (scale <= 1e-9) return;
        for (var i = 0; i < values.Length; i++) values[i] /= scale;
    }

    private static double Median(double[] sorted)
    {
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private static string BuildFfmpegHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return string.Empty;
        return string.Join("\r\n", headers
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{pair.Key.Trim()}: {pair.Value.Trim()}")) + "\r\n";
    }

    private static string TrimDiagnostic(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 240 ? line : line[..240] + "…";
    }

    private static string? ResolveExecutable(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "bin", fileName),
            Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
        };
        var bundled = candidates.FirstOrDefault(File.Exists);
        if (bundled is not null) return bundled;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH segments.
            }
        }
        return null;
    }

    private readonly record struct AlignmentCandidate(double OffsetSeconds, double Correlation, double PeakMargin);
    private readonly record struct LineFit(double Intercept, double Slope, double R2);
}
