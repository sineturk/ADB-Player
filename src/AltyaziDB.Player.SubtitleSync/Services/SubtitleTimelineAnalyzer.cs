using System.Diagnostics;
using System.Globalization;
using System.Text;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.SubtitleSync.Models;

namespace AltyaziDB.Player.SubtitleSync.Services;

internal sealed class SubtitleTimelineAnalyzer
{
    private const int SampleRate = 8000;
    private const int FeatureRate = 5;
    private const int FrameSamples = SampleRate / FeatureRate;
    private const double MinimumLocalScore = 0.34;
    private const double MinimumVerificationScore = 0.40;
    private const double MinimumVerificationCoverage = 0.55;

    // Common cinema / broadcast cadence conversions. A subtitle authored
    // against 25 fps and played on 23.976 fps needs a 25 / (24000/1001)
    // timeline stretch ~= 1.042708333. The inverse direction is equally common.
    private static readonly KnownFrameRateScale[] KnownFrameRateScales =
    [
        new("25 → 23.976 FPS", 25.0 * 1001.0 / 24000.0),
        new("25 → 24 FPS", 25.0 / 24.0),
        new("24 → 23.976 FPS", 1001.0 / 1000.0),
        new("23.976 → 25 FPS", 24000.0 / (1001.0 * 25.0)),
        new("24 → 25 FPS", 24.0 / 25.0),
        new("23.976 → 24 FPS", 1000.0 / 1001.0),
    ];

    private readonly IAppLogger _logger;
    private readonly string? _ffmpegPath;

    public SubtitleTimelineAnalyzer(IAppLogger logger, string? ffmpegDirectory)
    {
        _logger = logger;
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegDirectory)
            ? null
            : Path.Combine(ffmpegDirectory, "ffmpeg.exe");
    }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_ffmpegPath) &&
        File.Exists(_ffmpegPath);

    public async Task<SubtitleTimelineCandidate?> TrySynchronizeAsync(
        SubtitleSyncRequest request,
        IProgress<SubtitleSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable ||
            string.IsNullOrWhiteSpace(_ffmpegPath) ||
            request.ReferenceAudioStreamIndex is not int audioStreamIndex ||
            audioStreamIndex < 0)
        {
            return null;
        }

        var document = await SubtitleDocument.ReadAsync(
                request.SubtitlePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (document.Cues.Count < 8)
        {
            return null;
        }

        var validCues = document.Cues
            .Where(cue => cue.EndSeconds > cue.StartSeconds && cue.StartSeconds >= 0)
            .OrderBy(cue => cue.StartSeconds)
            .ToArray();
        if (validCues.Length < 8)
        {
            return null;
        }

        var workRoot = Path.Combine(
            Path.GetTempPath(),
            "ADB-Player",
            "subtitle-sync-v2",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        var pcmPath = Path.Combine(workRoot, "reference.s16le");

        try
        {
            progress?.Report(new SubtitleSyncProgress(
                8,
                "Orijinal ses zaman çizelgesi çıkarılıyor"));

            var maxSubtitleTime = validCues.Max(cue => cue.EndSeconds);
            var decodeDuration = Math.Clamp(
                maxSubtitleTime + Math.Max(45, request.MaxOffsetSeconds) + 60,
                60,
                8 * 60 * 60);

            await DecodeReferenceAudioAsync(
                    request.Reference,
                    audioStreamIndex,
                    decodeDuration,
                    pcmPath,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new SubtitleSyncProgress(
                24,
                "Konuşma aktivitesi analiz ediliyor"));

            var audioFeatures = await BuildAudioFeaturesAsync(
                    pcmPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (audioFeatures.Activity.Length < FeatureRate * 30)
            {
                return null;
            }

            progress?.Report(new SubtitleSyncProgress(
                40,
                "FPS/cadence ve çok noktalı eşleşme aranıyor"));

            TimelineModel model;
            SubtitleCueTiming[] transformedCues;
            VerificationResult verification;
            var finalAccepted = false;
            var acceptanceMode = "strict";
            var cadence = TryResolveKnownFrameRateHypothesis(
                validCues,
                audioFeatures,
                request.MaxOffsetSeconds);

            if (cadence is not null)
            {
                model = cadence.Model;
                transformedCues = cadence.TransformedCues;
                verification = cadence.Verification;
                finalAccepted = cadence.Accepted;
                acceptanceMode = cadence.AcceptanceMode;
                _logger.Info(
                    $"Subtitle Timeline v2 canonical cadence seçildi: " +
                    $"fps={model.FrameRateConversionLabel}, " +
                    $"offset={model.OffsetSeconds:+0.000;-0.000;0.000}s, " +
                    $"scale={model.ScaleFactor:0.000000}, " +
                    $"coverage={verification.Coverage:0.00}, " +
                    $"residual={verification.MedianAbsoluteResidualSeconds:0.000}s, " +
                    $"score={verification.MedianScore:0.000}, " +
                    $"probes={verification.GoodProbes}/{verification.TotalProbes}, " +
                    $"mode={acceptanceMode}");
            }
            else
            {
                var sourceTimeline = BuildSubtitleFeatures(
                    validCues,
                    audioFeatures.Activity.Length);

                var points = AnalyzeWindows(
                    sourceTimeline,
                    audioFeatures,
                    validCues,
                    request.MaxOffsetSeconds);
                if (points.Count < 4)
                {
                    _logger.Info(
                        $"Subtitle Timeline v2: yeterli güvenilir pencere yok ({points.Count}).");
                    return null;
                }

                model = BuildModel(points, validCues[^1].EndSeconds);
                transformedCues = validCues
                    .Select(cue => new SubtitleCueTiming(
                        Math.Max(0, model.Transform(cue.StartSeconds)),
                        Math.Max(0.05, model.Transform(cue.EndSeconds))))
                    .Select(cue => cue.EndSeconds <= cue.StartSeconds
                        ? cue with { EndSeconds = cue.StartSeconds + 0.25 }
                        : cue)
                    .ToArray();
                verification = Verify(
                    transformedCues,
                    audioFeatures);
                finalAccepted = verification.Accepted;

                _logger.Info(
                    $"Subtitle Timeline v2 model={model.Kind} points={points.Count} " +
                    $"offset={model.OffsetSeconds:+0.000;-0.000;0.000}s " +
                    $"scale={model.ScaleFactor:0.000000} splits={model.SplitCount}" +
                    (string.IsNullOrWhiteSpace(model.FrameRateConversionLabel)
                        ? string.Empty
                        : $" fps={model.FrameRateConversionLabel}"));
            }

            progress?.Report(new SubtitleSyncProgress(
                66,
                model.Kind == SubtitleSyncModelKind.Piecewise
                    ? "Farklı kurgu bölgeleri eşleştiriliyor"
                    : model.Kind == SubtitleSyncModelKind.FrameRate
                        ? "Kare hızı / drift modeli uygulanıyor"
                        : "Sabit zaman farkı uygulanıyor"));

            progress?.Report(new SubtitleSyncProgress(
                78,
                "Senkron sonucu bağımsız noktalarda doğrulanıyor"));
            if (!finalAccepted)
            {
                _logger.Info(
                    $"Subtitle Timeline v2 doğrulama reddi: " +
                    $"coverage={verification.Coverage:0.00}, " +
                    $"residual={verification.MedianAbsoluteResidualSeconds:0.000}s, " +
                    $"score={verification.MedianScore:0.000}, " +
                    $"probes={verification.GoodProbes}/{verification.TotalProbes}");
                return new SubtitleTimelineCandidate(
                    false,
                    model.Kind,
                    null,
                    model.OffsetSeconds,
                    model.ScaleFactor,
                    verification.Confidence,
                    model.SplitCount,
                    verification.MedianAbsoluteResidualSeconds,
                    verification.Coverage,
                    verification.GoodProbes,
                    "Çok noktalı doğrulama yeterli güven oluşturmadı.");
            }

            await document.WriteTransformedAsync(
                    request.OutputPath,
                    model.Transform,
                    cancellationToken)
                .ConfigureAwait(false);

            var alreadyAligned =
                model.Kind == SubtitleSyncModelKind.Fixed &&
                Math.Abs(model.OffsetSeconds) <= 0.12 &&
                verification.MedianAbsoluteResidualSeconds <= 0.25;

            var message = alreadyAligned
                ? "Altyazı orijinal ses zaman çizelgesiyle zaten uyumlu."
                : model.Kind switch
                {
                    SubtitleSyncModelKind.Piecewise =>
                        $"Altyazı farklı kurgu bölgelerine göre senkronlandı · " +
                        $"{verification.GoodProbes}/{verification.TotalProbes} doğrulama noktası.",
                    SubtitleSyncModelKind.FrameRate when
                        !string.IsNullOrWhiteSpace(model.FrameRateConversionLabel) =>
                        $"Altyazı {model.FrameRateConversionLabel} dönüşümüyle senkronlandı · " +
                        $"{verification.GoodProbes}/{verification.TotalProbes} doğrulama noktası.",
                    SubtitleSyncModelKind.FrameRate =>
                        $"Altyazı drift/kare hızı farkı düzeltilerek senkronlandı · " +
                        $"{verification.GoodProbes}/{verification.TotalProbes} doğrulama noktası.",
                    _ =>
                        $"Altyazı orijinal sesle otomatik senkronlandı · " +
                        $"{verification.GoodProbes}/{verification.TotalProbes} doğrulama noktası."
                };

            return new SubtitleTimelineCandidate(
                true,
                model.Kind,
                request.OutputPath,
                model.OffsetSeconds,
                model.ScaleFactor,
                verification.Confidence,
                model.SplitCount,
                verification.MedianAbsoluteResidualSeconds,
                verification.Coverage,
                verification.GoodProbes,
                message,
                alreadyAligned);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Info(
                $"Subtitle Timeline v2 kullanılamadı; ffsubsync fallback denenecek: " +
                $"{TrimDiagnostic(exception.Message)}");
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                {
                    Directory.Delete(workRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private async Task DecodeReferenceAudioAsync(
        SubtitleSyncMediaSource source,
        int audioStreamIndex,
        double durationSeconds,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
            },
            EnableRaisingEvents = true,
        };

        var args = process.StartInfo.ArgumentList;
        args.Add("-nostdin");
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");

        if (source.HttpHeaders is { Count: > 0 })
        {
            var headers = string.Join(
                "\r\n",
                source.HttpHeaders
                    .Where(pair =>
                        !string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    .Select(pair => $"{pair.Key}: {pair.Value}"));
            if (!string.IsNullOrWhiteSpace(headers))
            {
                args.Add("-headers");
                args.Add(headers + "\r\n");
            }
        }

        args.Add("-i");
        args.Add(source.Source);
        args.Add("-map");
        args.Add($"0:a:{audioStreamIndex}");
        args.Add("-vn");
        args.Add("-ac");
        args.Add("1");
        args.Add("-ar");
        args.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-acodec");
        args.Add("pcm_s16le");
        args.Add("-f");
        args.Add("s16le");
        args.Add("-t");
        args.Add(durationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        args.Add("-y");
        args.Add(outputPath);

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg altyazı referans sesini başlatamadı.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "FFmpeg orijinal ses parçasını çözemedi."
                    : TrimDiagnostic(stderr));
        }

        if (!File.Exists(outputPath) ||
            new FileInfo(outputPath).Length < SampleRate * 2L * 20L)
        {
            throw new InvalidOperationException(
                "Altyazı zaman çizelgesi için yeterli orijinal ses örneği üretilemedi.");
        }
    }

    private static async Task<AudioTimelineFeatures> BuildAudioFeaturesAsync(
        string pcmPath,
        CancellationToken cancellationToken)
    {
        const int bytesPerSample = 2;
        var frameBytes = FrameSamples * bytesPerSample;
        var buffer = new byte[frameBytes];
        var energies = new List<double>();

        await using var stream = new FileStream(
            pcmPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            useAsync: true);

        while (true)
        {
            var read = 0;
            while (read < frameBytes)
            {
                var count = await stream.ReadAsync(
                        buffer.AsMemory(read, frameBytes - read),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }

            if (read < bytesPerSample * 32) break;

            double sumSquares = 0;
            var sampleCount = read / bytesPerSample;
            for (var offset = 0; offset + 1 < read; offset += 2)
            {
                var sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                var normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }

            var rms = Math.Sqrt(sumSquares / Math.Max(1, sampleCount));
            energies.Add(20.0 * Math.Log10(Math.Max(rms, 1e-8)));

            if (read < frameBytes) break;
        }

        if (energies.Count == 0)
        {
            return AudioTimelineFeatures.Empty;
        }

        var sorted = energies.OrderBy(value => value).ToArray();
        var noiseFloor = Percentile(sorted, 0.20);
        var upper = Percentile(sorted, 0.80);
        var dynamicRange = Math.Max(4.0, upper - noiseFloor);
        var threshold = noiseFloor + Math.Clamp(dynamicRange * 0.42, 3.5, 10.0);

        var activity = energies
            .Select(value => value >= threshold)
            .ToArray();

        CloseShortGaps(activity, 2);
        RemoveShortRuns(activity, 1);

        var onset = new double[activity.Length];
        for (var index = 1; index < activity.Length; index++)
        {
            if (!activity[index] || activity[index - 1]) continue;
            onset[index] = 1.0;
            if (index > 0) onset[index - 1] = Math.Max(onset[index - 1], 0.45);
            if (index + 1 < onset.Length) onset[index + 1] = Math.Max(onset[index + 1], 0.55);
            if (index + 2 < onset.Length) onset[index + 2] = Math.Max(onset[index + 2], 0.25);
        }

        return new AudioTimelineFeatures(activity, onset);
    }

    private static SubtitleFeatureTimeline BuildSubtitleFeatures(
        IReadOnlyList<SubtitleCueTiming> cues,
        int frameCount)
    {
        var activity = new bool[frameCount];
        var onset = new double[frameCount];

        foreach (var cue in cues)
        {
            var start = Math.Clamp(
                (int)Math.Round(cue.StartSeconds * FeatureRate),
                0,
                Math.Max(0, frameCount - 1));
            var end = Math.Clamp(
                (int)Math.Ceiling(cue.EndSeconds * FeatureRate),
                start + 1,
                frameCount);

            for (var index = start; index < end; index++)
            {
                activity[index] = true;
            }

            onset[start] = 1.0;
            if (start > 0) onset[start - 1] = Math.Max(onset[start - 1], 0.40);
            if (start + 1 < onset.Length) onset[start + 1] = Math.Max(onset[start + 1], 0.60);
        }

        return new SubtitleFeatureTimeline(activity, onset);
    }

    private CadenceHypothesis? TryResolveKnownFrameRateHypothesis(
        IReadOnlyList<SubtitleCueTiming> cues,
        AudioTimelineFeatures audio,
        int maxOffsetSeconds)
    {
        if (cues.Count < 8)
        {
            return null;
        }

        CadenceHypothesis? best = null;
        foreach (var known in KnownFrameRateScales)
        {
            var scaledCues = cues
                .Select(cue => new SubtitleCueTiming(
                    cue.StartSeconds * known.ScaleFactor,
                    cue.EndSeconds * known.ScaleFactor))
                .ToArray();

            var offset = FindBestGlobalOffset(
                scaledCues,
                audio,
                Math.Clamp(maxOffsetSeconds, 15, 180));
            if (offset is null)
            {
                continue;
            }

            var transformed = scaledCues
                .Select(cue => new SubtitleCueTiming(
                    Math.Max(0, cue.StartSeconds + offset.OffsetSeconds),
                    Math.Max(0.05, cue.EndSeconds + offset.OffsetSeconds)))
                .Select(cue => cue.EndSeconds <= cue.StartSeconds
                    ? cue with { EndSeconds = cue.StartSeconds + 0.25 }
                    : cue)
                .ToArray();

            var verification = Verify(transformed, audio);
            var legacyVerification = verification.Accepted
                ? VerificationResult.Rejected
                : VerifyLegacyCadence(transformed, audio);
            var legacyCadenceCrosscheck =
                !verification.Accepted &&
                legacyVerification.Accepted;

            // Some dialogue-heavy windows can still produce a few bad residual
            // probes because VAD/onset correlation is intentionally language
            // agnostic. For a known cadence ratio, allow a robust-consensus path
            // only when the whole-programme offset search is itself distinctive.
            var robustCadenceConsensus =
                offset.Score >= 0.40 &&
                offset.Margin >= 0.08 &&
                verification.GoodProbes >= 5 &&
                verification.Coverage >= MinimumVerificationCoverage &&
                verification.MedianScore >= 0.46 &&
                verification.MedianAbsoluteResidualSeconds <= 0.45;

            var cadenceAccepted =
                verification.Accepted ||
                legacyCadenceCrosscheck ||
                robustCadenceConsensus;
            var acceptedVerification = legacyCadenceCrosscheck
                ? legacyVerification
                : verification;
            var acceptanceMode = verification.Accepted
                ? "strict-dialogue"
                : legacyCadenceCrosscheck
                    ? "legacy-crosscheck"
                    : robustCadenceConsensus
                        ? "robust-consensus"
                        : "rejected";

            _logger.Info(
                $"Subtitle Timeline v2 cadence probe {known.Label}: " +
                $"offset={offset.OffsetSeconds:+0.000;-0.000;0.000}s, " +
                $"global={offset.Score:0.000}, margin={offset.Margin:0.000}, " +
                $"coverage={acceptedVerification.Coverage:0.00}, " +
                $"residual={acceptedVerification.MedianAbsoluteResidualSeconds:0.000}s, " +
                $"score={acceptedVerification.MedianScore:0.000}, " +
                $"probes={acceptedVerification.GoodProbes}/{acceptedVerification.TotalProbes}, " +
                $"accepted={cadenceAccepted}, mode={acceptanceMode}");

            if (!cadenceAccepted)
            {
                continue;
            }

            var rank =
                acceptedVerification.Confidence +
                Math.Clamp(offset.Score, 0, 1) * 0.30 +
                Math.Clamp(offset.Margin * 4.0, 0, 0.20) -
                Math.Clamp(acceptedVerification.MedianAbsoluteResidualSeconds / 4.0, 0, 0.25) +
                (legacyCadenceCrosscheck || robustCadenceConsensus ? 0.04 : 0);

            var candidate = new CadenceHypothesis(
                TimelineModel.FrameRate(
                    offset.OffsetSeconds,
                    known.ScaleFactor,
                    known.Label),
                transformed,
                acceptedVerification,
                cadenceAccepted,
                acceptanceMode,
                rank);

            if (best is null || candidate.Rank > best.Rank)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static GlobalOffsetResult? FindBestGlobalOffset(
        IReadOnlyList<SubtitleCueTiming> scaledCues,
        AudioTimelineFeatures audio,
        int maxOffsetSeconds)
    {
        if (scaledCues.Count < 8 || audio.Onset.Length == 0)
        {
            return null;
        }

        // Use at most ~240 evenly distributed cue starts. This keeps the search
        // cheap while making the score representative of the whole programme.
        var stride = Math.Max(1, scaledCues.Count / 240);
        var onsetFrames = scaledCues
            .Where((_, index) => index % stride == 0)
            .Select(cue => (int)Math.Round(cue.StartSeconds * FeatureRate))
            .ToArray();
        if (onsetFrames.Length < 8)
        {
            return null;
        }

        var maxLagFrames = maxOffsetSeconds * FeatureRate;
        var scores = new List<(int Lag, double Score)>();
        var bestLag = 0;
        var bestScore = double.NegativeInfinity;

        for (var lag = -maxLagFrames; lag <= maxLagFrames; lag++)
        {
            double sum = 0;
            var usable = 0;

            foreach (var frame in onsetFrames)
            {
                var audioIndex = frame + lag;
                if ((uint)audioIndex >= (uint)audio.Activity.Length)
                {
                    continue;
                }

                usable++;
                var onsetAffinity = LocalOnsetAffinity(audio.Onset, audioIndex);
                var activityAffinity = LocalActivityAffinity(audio.Activity, audioIndex);
                sum += 0.78 * onsetAffinity + 0.22 * activityAffinity;
            }

            if (usable < onsetFrames.Length * 0.72)
            {
                continue;
            }

            var score = sum / usable;
            scores.Add((lag, score));
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (scores.Count == 0 || double.IsNegativeInfinity(bestScore))
        {
            return null;
        }

        var second = scores
            .Where(item => Math.Abs(item.Lag - bestLag) > FeatureRate * 2)
            .Select(item => item.Score)
            .DefaultIfEmpty(0)
            .Max();

        return new GlobalOffsetResult(
            bestLag / (double)FeatureRate,
            bestScore,
            Math.Max(0, bestScore - second));
    }

    private static double LocalOnsetAffinity(
        IReadOnlyList<double> onset,
        int center)
    {
        double best = 0;
        var radius = FeatureRate; // +/- 1 second tolerance around subtitle start.
        for (var delta = -radius; delta <= radius; delta++)
        {
            var index = center + delta;
            if ((uint)index >= (uint)onset.Count)
            {
                continue;
            }

            var distanceWeight = 1.0 - Math.Abs(delta) / (double)(radius + 1);
            best = Math.Max(best, onset[index] * distanceWeight);
        }

        return best;
    }

    private static double LocalActivityAffinity(
        IReadOnlyList<bool> activity,
        int center)
    {
        var radius = Math.Max(1, FeatureRate / 2);
        var active = 0;
        var usable = 0;
        for (var delta = -radius; delta <= radius; delta++)
        {
            var index = center + delta;
            if ((uint)index >= (uint)activity.Count)
            {
                continue;
            }

            usable++;
            if (activity[index]) active++;
        }

        return usable == 0 ? 0 : active / (double)usable;
    }

    private static IReadOnlyList<TimelinePoint> AnalyzeWindows(
        SubtitleFeatureTimeline subtitle,
        AudioTimelineFeatures audio,
        IReadOnlyList<SubtitleCueTiming> cues,
        int maxOffsetSeconds)
    {
        var first = cues[0].StartSeconds;
        var last = cues[^1].EndSeconds;
        var span = Math.Max(1, last - first);
        var windowSeconds = Math.Clamp(span / 8.0, 35.0, 90.0);
        var windowFrames = Math.Max(FeatureRate * 25, (int)Math.Round(windowSeconds * FeatureRate));

        // A pure 25 -> 23.976 conversion accumulates ~4.27% drift. On a
        // 48:32 programme this is ~124 seconds, already beyond the historical
        // 120-second search gate. Reserve enough range for known cadence drift
        // plus a small fixed-offset allowance while keeping the 300s hard cap.
        var cadenceSearchSeconds = (int)Math.Ceiling(span * 0.055) + 20;
        var effectiveMaxOffsetSeconds = Math.Clamp(
            Math.Max(maxOffsetSeconds, cadenceSearchSeconds),
            15,
            300);
        var maxLagFrames = effectiveMaxOffsetSeconds * FeatureRate;
        var windowCount = span < 12 * 60 ? 7 : 9;
        var points = new List<TimelinePoint>();

        for (var slot = 0; slot < windowCount; slot++)
        {
            var fraction = windowCount == 1
                ? 0.5
                : 0.08 + (0.84 * slot / (windowCount - 1));
            var centerSeconds = first + span * fraction;
            var centerFrame = (int)Math.Round(centerSeconds * FeatureRate);

            var match = FindBestLag(
                subtitle,
                audio,
                centerFrame,
                windowFrames,
                maxLagFrames);
            if (match is null) continue;

            if (match.Score >= MinimumLocalScore &&
                (match.Margin >= 0.010 || match.Score >= 0.48))
            {
                points.Add(new TimelinePoint(
                    centerSeconds,
                    match.LagFrames / (double)FeatureRate,
                    match.Score,
                    match.Margin));
            }
        }

        return points;
    }

    private static LagMatch? FindBestLag(
        SubtitleFeatureTimeline subtitle,
        AudioTimelineFeatures audio,
        int centerFrame,
        int windowFrames,
        int maxLagFrames)
    {
        var half = windowFrames / 2;
        var start = Math.Max(0, centerFrame - half);
        var end = Math.Min(subtitle.Activity.Length, centerFrame + half);
        if (end - start < FeatureRate * 15) return null;

        var subtitleActive = 0;
        var subtitleOnsetWeight = 0.0;
        for (var index = start; index < end; index++)
        {
            if (subtitle.Activity[index]) subtitleActive++;
            subtitleOnsetWeight += subtitle.Onset[index];
        }

        if (subtitleActive < FeatureRate * 4 || subtitleOnsetWeight < 2.0)
        {
            return null;
        }

        var bestLag = 0;
        var bestScore = double.NegativeInfinity;
        var scores = new List<(int Lag, double Score)>();

        for (var lag = -maxLagFrames; lag <= maxLagFrames; lag++)
        {
            double overlap = 0;
            double onsetMatch = 0;
            var usable = 0;

            for (var index = start; index < end; index++)
            {
                var audioIndex = index + lag;
                if ((uint)audioIndex >= (uint)audio.Activity.Length) continue;
                usable++;

                if (subtitle.Activity[index] && audio.Activity[audioIndex])
                {
                    overlap++;
                }

                if (subtitle.Onset[index] > 0)
                {
                    onsetMatch += subtitle.Onset[index] * audio.Onset[audioIndex];
                }
            }

            if (usable < (end - start) * 0.65) continue;

            var activeCoverage = overlap / Math.Max(1.0, subtitleActive);
            var onsetCoverage = onsetMatch / Math.Max(1.0, subtitleOnsetWeight);
            var score = 0.72 * activeCoverage + 0.28 * onsetCoverage;
            scores.Add((lag, score));

            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (scores.Count == 0 || double.IsNegativeInfinity(bestScore))
        {
            return null;
        }

        var second = scores
            .Where(item => Math.Abs(item.Lag - bestLag) > FeatureRate)
            .Select(item => item.Score)
            .DefaultIfEmpty(0)
            .Max();

        return new LagMatch(
            bestLag,
            bestScore,
            Math.Max(0, bestScore - second));
    }

    private static TimelineModel BuildModel(
        IReadOnlyList<TimelinePoint> sourcePoints,
        double durationSeconds)
    {
        var points = sourcePoints
            .OrderBy(point => point.TimeSeconds)
            .ToArray();
        var lags = points.Select(point => point.LagSeconds).OrderBy(value => value).ToArray();
        var medianLag = Median(lags);
        var fixedResiduals = points
            .Select(point => Math.Abs(point.LagSeconds - medianLag))
            .OrderBy(value => value)
            .ToArray();
        var fixedMad = Median(fixedResiduals);

        var (intercept, slope, r2) = WeightedLinearRegression(points);
        var linearTravel = Math.Abs(slope * Math.Max(1, durationSeconds));

        var canonicalFrameRate = TryBuildKnownFrameRateModel(
            points,
            durationSeconds,
            1.0 + slope);
        if (canonicalFrameRate is not null)
        {
            return canonicalFrameRate;
        }

        var adjacentJumps = 0;
        for (var index = 1; index < points.Length; index++)
        {
            if (Math.Abs(points[index].LagSeconds - points[index - 1].LagSeconds) >= 0.90)
            {
                adjacentJumps++;
            }
        }

        if (points.Length >= 5 &&
            adjacentJumps > 0 &&
            adjacentJumps <= Math.Max(2, (points.Length - 1) / 2) &&
            fixedMad >= 0.28)
        {
            var anchors = SmoothPiecewiseAnchors(points);
            var splitCount = 0;
            for (var index = 1; index < anchors.Length; index++)
            {
                if (Math.Abs(anchors[index].LagSeconds - anchors[index - 1].LagSeconds) >= 0.80)
                {
                    splitCount++;
                }
            }

            return TimelineModel.Piecewise(
                anchors,
                medianLag,
                Math.Max(1, splitCount));
        }

        if (points.Length >= 4 &&
            linearTravel >= 0.60 &&
            r2 >= 0.52 &&
            Math.Abs(slope) <= 0.08)
        {
            return TimelineModel.FrameRate(
                intercept,
                1.0 + slope);
        }

        return TimelineModel.Fixed(medianLag);
    }

    private static TimelineModel? TryBuildKnownFrameRateModel(
        IReadOnlyList<TimelinePoint> points,
        double durationSeconds,
        double measuredScale)
    {
        if (points.Count < 4 || durationSeconds < 60)
        {
            return null;
        }

        KnownFrameRateCandidate? best = null;
        foreach (var known in KnownFrameRateScales)
        {
            if (Math.Abs(known.ScaleFactor - measuredScale) > 0.0065)
            {
                continue;
            }

            var slope = known.ScaleFactor - 1.0;
            var interceptCandidates = points
                .Select(point => point.LagSeconds - slope * point.TimeSeconds)
                .OrderBy(value => value)
                .ToArray();
            var intercept = Median(interceptCandidates);

            var residuals = points
                .Select(point => Math.Abs(
                    point.LagSeconds -
                    (intercept + slope * point.TimeSeconds)))
                .OrderBy(value => value)
                .ToArray();
            var medianResidual = Median(residuals);
            var highResidual = Percentile(residuals, 0.80);
            var travel = Math.Abs(slope * durationSeconds);

            if (travel < 0.60 ||
                medianResidual > 0.65 ||
                highResidual > 1.20)
            {
                continue;
            }

            var candidate = new KnownFrameRateCandidate(
                known.Label,
                known.ScaleFactor,
                intercept,
                medianResidual,
                highResidual);

            if (best is null ||
                candidate.MedianResidualSeconds < best.MedianResidualSeconds)
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return null;
        }

        return TimelineModel.FrameRate(
            best.InterceptSeconds,
            best.ScaleFactor,
            best.Label);
    }

    private static TimelinePoint[] SmoothPiecewiseAnchors(
        IReadOnlyList<TimelinePoint> points)
    {
        var output = new TimelinePoint[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var leftJump = index > 0
                ? Math.Abs(current.LagSeconds - points[index - 1].LagSeconds)
                : 0;
            var rightJump = index + 1 < points.Count
                ? Math.Abs(points[index + 1].LagSeconds - current.LagSeconds)
                : 0;

            // Do not blur a real edit boundary into a long artificial drift ramp.
            if (leftJump >= 0.80 || rightJump >= 0.80)
            {
                output[index] = current;
                continue;
            }

            var start = Math.Max(0, index - 1);
            var end = Math.Min(points.Count - 1, index + 1);
            var local = new List<double>();
            for (var cursor = start; cursor <= end; cursor++)
            {
                local.Add(points[cursor].LagSeconds);
            }

            output[index] = current with
            {
                LagSeconds = Median(local)
            };
        }

        return output;
    }

    private static VerificationResult VerifyLegacyCadence(
        IReadOnlyList<SubtitleCueTiming> transformedCues,
        AudioTimelineFeatures audio)
    {
        if (transformedCues.Count < 8)
        {
            return VerificationResult.Rejected;
        }

        var timeline = BuildSubtitleFeatures(
            transformedCues,
            audio.Activity.Length);
        var first = transformedCues[0].StartSeconds;
        var last = transformedCues[^1].EndSeconds;
        var span = Math.Max(1, last - first);
        var windowSeconds = Math.Clamp(span / 10.0, 25.0, 60.0);
        var windowFrames = (int)Math.Round(windowSeconds * FeatureRate);
        var residualSearchFrames = (int)Math.Round(1.8 * FeatureRate);
        const int probeCount = 7;
        var probes = new List<VerificationProbe>();

        for (var slot = 0; slot < probeCount; slot++)
        {
            var fraction = 0.10 + (0.80 * slot / (probeCount - 1));
            var center = first + span * fraction;
            var match = FindBestLag(
                timeline,
                audio,
                (int)Math.Round(center * FeatureRate),
                windowFrames,
                residualSearchFrames);
            if (match is null) continue;

            probes.Add(new VerificationProbe(
                match.LagFrames / (double)FeatureRate,
                match.Score));
        }

        return BuildVerificationResult(probes, probeCount);
    }

    private static VerificationResult Verify(
        IReadOnlyList<SubtitleCueTiming> transformedCues,
        AudioTimelineFeatures audio)
    {
        if (transformedCues.Count < 8)
        {
            return VerificationResult.Rejected;
        }

        var timeline = BuildSubtitleFeatures(
            transformedCues,
            audio.Activity.Length);
        var first = transformedCues[0].StartSeconds;
        var last = transformedCues[^1].EndSeconds;
        var span = Math.Max(1, last - first);
        var windowSeconds = Math.Clamp(span / 14.0, 22.0, 50.0);
        var windowFrames = (int)Math.Round(windowSeconds * FeatureRate);
        var residualSearchFrames = (int)Math.Round(1.8 * FeatureRate);
        var probeCount = transformedCues.Count >= 120 ? 9 : 7;
        var probes = new List<VerificationProbe>();

        for (var slot = 0; slot < probeCount; slot++)
        {
            var fraction = 0.06 + (0.88 * slot / (probeCount - 1));
            var cueIndex = Math.Clamp(
                (int)Math.Round(fraction * (transformedCues.Count - 1)),
                0,
                transformedCues.Count - 1);
            var cue = transformedCues[cueIndex];
            var center = (cue.StartSeconds + cue.EndSeconds) / 2.0;
            var match = FindBestLag(
                timeline,
                audio,
                (int)Math.Round(center * FeatureRate),
                windowFrames,
                residualSearchFrames);
            if (match is null) continue;

            probes.Add(new VerificationProbe(
                match.LagFrames / (double)FeatureRate,
                match.Score));
        }

        return BuildVerificationResult(probes, probeCount);
    }

    private static VerificationResult BuildVerificationResult(
        IReadOnlyList<VerificationProbe> probes,
        int probeCount)
    {
        if (probes.Count < 4)
        {
            return new VerificationResult(
                false,
                0,
                double.PositiveInfinity,
                0,
                0,
                probes.Count,
                probeCount);
        }

        var good = probes
            .Where(probe => probe.Score >= MinimumVerificationScore)
            .ToArray();
        var coverage = good.Length / (double)probeCount;
        if (good.Length == 0)
        {
            return new VerificationResult(
                false,
                coverage,
                double.PositiveInfinity,
                0,
                0,
                0,
                probeCount);
        }

        var residuals = good
            .Select(probe => Math.Abs(probe.ResidualSeconds))
            .OrderBy(value => value)
            .ToArray();
        var scores = good
            .Select(probe => probe.Score)
            .OrderBy(value => value)
            .ToArray();
        var medianResidual = Median(residuals);
        var medianScore = Median(scores);
        var highResidual = Percentile(residuals, 0.85);

        var accepted =
            good.Length >= 4 &&
            coverage >= MinimumVerificationCoverage &&
            medianScore >= 0.42 &&
            medianResidual <= 0.60 &&
            highResidual <= 1.20;

        var residualFactor = 1.0 - Math.Clamp(medianResidual / 1.20, 0, 1);
        var confidence = Math.Clamp(
            medianScore * coverage * (0.65 + 0.35 * residualFactor),
            0,
            1);

        return new VerificationResult(
            accepted,
            coverage,
            medianResidual,
            medianScore,
            confidence,
            good.Length,
            probeCount);
    }

    private static (double Intercept, double Slope, double R2) WeightedLinearRegression(
        IReadOnlyList<TimelinePoint> points)
    {
        var sumW = points.Sum(point => Math.Max(0.05, point.Score));
        var meanX = points.Sum(point => point.TimeSeconds * Math.Max(0.05, point.Score)) / sumW;
        var meanY = points.Sum(point => point.LagSeconds * Math.Max(0.05, point.Score)) / sumW;

        double sxx = 0;
        double sxy = 0;
        double syy = 0;
        foreach (var point in points)
        {
            var weight = Math.Max(0.05, point.Score);
            var dx = point.TimeSeconds - meanX;
            var dy = point.LagSeconds - meanY;
            sxx += weight * dx * dx;
            sxy += weight * dx * dy;
            syy += weight * dy * dy;
        }

        var slope = sxx > 1e-9 ? sxy / sxx : 0;
        var intercept = meanY - slope * meanX;

        double error = 0;
        foreach (var point in points)
        {
            var weight = Math.Max(0.05, point.Score);
            var predicted = intercept + slope * point.TimeSeconds;
            var residual = point.LagSeconds - predicted;
            error += weight * residual * residual;
        }

        var r2 = syy > 1e-9
            ? Math.Clamp(1.0 - error / syy, 0, 1)
            : 0;

        return (intercept, slope, r2);
    }

    private static void CloseShortGaps(bool[] values, int maxGap)
    {
        var index = 0;
        while (index < values.Length)
        {
            if (values[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < values.Length && !values[index]) index++;
            var length = index - start;
            if (start > 0 &&
                index < values.Length &&
                length <= maxGap)
            {
                for (var cursor = start; cursor < index; cursor++)
                {
                    values[cursor] = true;
                }
            }
        }
    }

    private static void RemoveShortRuns(bool[] values, int maxRun)
    {
        var index = 0;
        while (index < values.Length)
        {
            if (!values[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < values.Length && values[index]) index++;
            var length = index - start;
            if (length <= maxRun)
            {
                for (var cursor = start; cursor < index; cursor++)
                {
                    values[cursor] = false;
                }
            }
        }
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }

    private static double Median(IReadOnlyList<double> sortedOrUnsorted)
    {
        if (sortedOrUnsorted.Count == 0) return 0;
        var values = sortedOrUnsorted.OrderBy(value => value).ToArray();
        return values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2.0;
    }

    private static string TrimDiagnostic(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 700 ? line : line[..700] + "…";
    }

    private sealed record AudioTimelineFeatures(
        bool[] Activity,
        double[] Onset)
    {
        public static AudioTimelineFeatures Empty { get; } =
            new(Array.Empty<bool>(), Array.Empty<double>());
    }

    private sealed record SubtitleFeatureTimeline(
        bool[] Activity,
        double[] Onset);

    private sealed record LagMatch(
        int LagFrames,
        double Score,
        double Margin);

    private sealed record GlobalOffsetResult(
        double OffsetSeconds,
        double Score,
        double Margin);

    private sealed record CadenceHypothesis(
        TimelineModel Model,
        SubtitleCueTiming[] TransformedCues,
        VerificationResult Verification,
        bool Accepted,
        string AcceptanceMode,
        double Rank);

    private sealed record TimelinePoint(
        double TimeSeconds,
        double LagSeconds,
        double Score,
        double Margin);

    private sealed record VerificationProbe(
        double ResidualSeconds,
        double Score);

    private sealed record VerificationResult(
        bool Accepted,
        double Coverage,
        double MedianAbsoluteResidualSeconds,
        double MedianScore,
        double Confidence,
        int GoodProbes,
        int TotalProbes)
    {
        public static VerificationResult Rejected { get; } =
            new(false, 0, double.PositiveInfinity, 0, 0, 0, 0);
    }

    private sealed record KnownFrameRateScale(
        string Label,
        double ScaleFactor);

    private sealed record KnownFrameRateCandidate(
        string Label,
        double ScaleFactor,
        double InterceptSeconds,
        double MedianResidualSeconds,
        double HighResidualSeconds);

    private sealed record TimelineModel(
        SubtitleSyncModelKind Kind,
        double OffsetSeconds,
        double ScaleFactor,
        int SplitCount,
        IReadOnlyList<TimelinePoint>? Anchors,
        string? FrameRateConversionLabel = null)
    {
        public static TimelineModel Fixed(double offset) =>
            new(SubtitleSyncModelKind.Fixed, offset, 1.0, 0, null);

        public static TimelineModel FrameRate(
            double offset,
            double scale,
            string? conversionLabel = null) =>
            new(
                SubtitleSyncModelKind.FrameRate,
                offset,
                scale,
                0,
                null,
                conversionLabel);

        public static TimelineModel Piecewise(
            IReadOnlyList<TimelinePoint> anchors,
            double fallbackOffset,
            int splitCount) =>
            new(
                SubtitleSyncModelKind.Piecewise,
                fallbackOffset,
                1.0,
                splitCount,
                anchors);

        public double Transform(double seconds)
        {
            if (Kind == SubtitleSyncModelKind.FrameRate)
            {
                return ScaleFactor * seconds + OffsetSeconds;
            }

            if (Kind != SubtitleSyncModelKind.Piecewise ||
                Anchors is null ||
                Anchors.Count == 0)
            {
                return seconds + OffsetSeconds;
            }

            if (seconds <= Anchors[0].TimeSeconds)
            {
                return seconds + Anchors[0].LagSeconds;
            }

            if (seconds >= Anchors[^1].TimeSeconds)
            {
                return seconds + Anchors[^1].LagSeconds;
            }

            for (var index = 1; index < Anchors.Count; index++)
            {
                var right = Anchors[index];
                var left = Anchors[index - 1];
                var boundary = (left.TimeSeconds + right.TimeSeconds) / 2.0;
                if (seconds > boundary) continue;

                // Piecewise means an edit-boundary jump, not a synthetic linear
                // stretch between distant probes. Use the nearest verified region.
                return seconds + left.LagSeconds;
            }

            return seconds + Anchors[^1].LagSeconds;
        }
    }

    private sealed class SubtitleDocument
    {
        private readonly string[] _lines;
        private readonly SubtitleFormat _format;

        private SubtitleDocument(
            string[] lines,
            SubtitleFormat format,
            IReadOnlyList<SubtitleCueTiming> cues)
        {
            _lines = lines;
            _format = format;
            Cues = cues;
        }

        public IReadOnlyList<SubtitleCueTiming> Cues { get; }

        public static async Task<SubtitleDocument> ReadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            var extension = Path.GetExtension(path);
            var format = extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                         extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase)
                ? SubtitleFormat.Ass
                : extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase)
                    ? SubtitleFormat.Vtt
                    : SubtitleFormat.Srt;

            var cues = new List<SubtitleCueTiming>();
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (TryParseArrowTiming(line, out var start, out var end, out _))
                {
                    cues.Add(new SubtitleCueTiming(start, end));
                    continue;
                }

                if (format == SubtitleFormat.Ass &&
                    TryParseAssTiming(line, out start, out end))
                {
                    cues.Add(new SubtitleCueTiming(start, end));
                }
            }

            return new SubtitleDocument(lines, format, cues);
        }

        public async Task WriteTransformedAsync(
            string outputPath,
            Func<double, double> transform,
            CancellationToken cancellationToken)
        {
            var output = new string[_lines.Length];
            for (var index = 0; index < _lines.Length; index++)
            {
                var raw = _lines[index];
                var trimmed = raw.Trim();

                if (TryParseArrowTiming(
                        trimmed,
                        out var start,
                        out var end,
                        out var suffix))
                {
                    var transformedStart = Math.Max(0, transform(start));
                    var transformedEnd = Math.Max(
                        transformedStart + 0.05,
                        transform(end));
                    var arrowIndex = raw.IndexOf("-->", StringComparison.Ordinal);
                    var indent = arrowIndex >= 0
                        ? raw[..Math.Max(0, raw.TakeWhile(char.IsWhiteSpace).Count())]
                        : string.Empty;
                    var separator = _format == SubtitleFormat.Srt ? ',' : '.';
                    output[index] =
                        $"{indent}{FormatTimestamp(transformedStart, separator)} --> " +
                        $"{FormatTimestamp(transformedEnd, separator)}{suffix}";
                    continue;
                }

                if (_format == SubtitleFormat.Ass &&
                    trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                {
                    var prefixLength = raw.IndexOf(':') + 1;
                    var prefix = raw[..prefixLength];
                    var payload = raw[prefixLength..].TrimStart();
                    var fields = payload.Split(',', 10);
                    if (fields.Length >= 10 &&
                        TryParseTimestamp(fields[1], out var assStart) &&
                        TryParseTimestamp(fields[2], out var assEnd))
                    {
                        var transformedStart = Math.Max(0, transform(assStart));
                        var transformedEnd = Math.Max(
                            transformedStart + 0.05,
                            transform(assEnd));
                        fields[1] = FormatAssTimestamp(transformedStart);
                        fields[2] = FormatAssTimestamp(transformedEnd);
                        output[index] = prefix + string.Join(",", fields);
                        continue;
                    }
                }

                output[index] = raw;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllLinesAsync(
                    outputPath,
                    output,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool TryParseArrowTiming(
            string line,
            out double start,
            out double end,
            out string suffix)
        {
            start = 0;
            end = 0;
            suffix = string.Empty;

            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) return false;

            var startText = line[..arrow].Trim();
            var right = line[(arrow + 3)..].TrimStart();
            var tokenLength = 0;
            while (tokenLength < right.Length &&
                   !char.IsWhiteSpace(right[tokenLength]))
            {
                tokenLength++;
            }

            if (tokenLength == 0) return false;
            var endText = right[..tokenLength];
            suffix = tokenLength < right.Length
                ? right[tokenLength..]
                : string.Empty;

            return TryParseTimestamp(startText, out start) &&
                   TryParseTimestamp(endText, out end);
        }

        private static bool TryParseAssTiming(
            string line,
            out double start,
            out double end)
        {
            start = 0;
            end = 0;
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var payload = line["Dialogue:".Length..].TrimStart();
            var fields = payload.Split(',', 10);
            return fields.Length >= 3 &&
                   TryParseTimestamp(fields[1], out start) &&
                   TryParseTimestamp(fields[2], out end);
        }

        private static bool TryParseTimestamp(
            string value,
            out double seconds)
        {
            seconds = 0;
            value = value.Trim().Replace(',', '.');
            var parts = value.Split(':');
            if (parts.Length is not (2 or 3)) return false;

            var hours = 0.0;
            var minutesIndex = 0;
            if (parts.Length == 3)
            {
                if (!double.TryParse(
                        parts[0],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out hours))
                {
                    return false;
                }

                minutesIndex = 1;
            }

            if (!double.TryParse(
                    parts[minutesIndex],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var minutes) ||
                !double.TryParse(
                    parts[minutesIndex + 1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var secs))
            {
                return false;
            }

            if (hours < 0 ||
                minutes is < 0 or >= 60 ||
                secs is < 0 or >= 60.999)
            {
                return false;
            }

            seconds = hours * 3600 + minutes * 60 + secs;
            return true;
        }

        private static string FormatTimestamp(double seconds, char fractionSeparator)
        {
            var bounded = Math.Max(0, seconds);
            var totalMilliseconds = (long)Math.Round(bounded * 1000.0);
            var hours = totalMilliseconds / 3_600_000;
            var minutes = (totalMilliseconds / 60_000) % 60;
            var secs = (totalMilliseconds / 1000) % 60;
            var millis = totalMilliseconds % 1000;
            return $"{hours:00}:{minutes:00}:{secs:00}{fractionSeparator}{millis:000}";
        }

        private static string FormatAssTimestamp(double seconds)
        {
            var bounded = Math.Max(0, seconds);
            var totalCentiseconds = (long)Math.Round(bounded * 100.0);
            var hours = totalCentiseconds / 360_000;
            var minutes = (totalCentiseconds / 6_000) % 60;
            var secs = (totalCentiseconds / 100) % 60;
            var centis = totalCentiseconds % 100;
            return $"{hours}:{minutes:00}:{secs:00}.{centis:00}";
        }
    }

    private enum SubtitleFormat
    {
        Srt,
        Vtt,
        Ass
    }

    private sealed record SubtitleCueTiming(
        double StartSeconds,
        double EndSeconds);
}

internal sealed record SubtitleTimelineCandidate(
    bool Accepted,
    SubtitleSyncModelKind Model,
    string? OutputPath,
    double OffsetSeconds,
    double ScaleFactor,
    double Score,
    int SplitCount,
    double VerificationResidualSeconds,
    double VerificationCoverage,
    int VerificationProbes,
    string Message,
    bool AlreadyAligned = false);
