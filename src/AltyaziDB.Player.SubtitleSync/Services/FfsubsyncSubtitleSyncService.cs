using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.SubtitleSync.Abstractions;
using AltyaziDB.Player.SubtitleSync.Models;

namespace AltyaziDB.Player.SubtitleSync.Services;

public sealed partial class FfsubsyncSubtitleSyncService : ISubtitleSyncService
{
    private static readonly HashSet<string> SupportedSubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt"
    };

    private readonly IAppLogger _logger;
    private readonly string? _executablePath;
    private readonly string? _ffmpegDirectory;
    private readonly SubtitleTimelineAnalyzer _timelineAnalyzer;

    public FfsubsyncSubtitleSyncService(IAppLogger logger)
    {
        _logger = logger;
        _executablePath = ResolveExecutable();
        _ffmpegDirectory = ResolveFfmpegDirectory();
        _timelineAnalyzer = new SubtitleTimelineAnalyzer(logger, _ffmpegDirectory);
    }

    public bool IsAvailable =>
        _timelineAnalyzer.IsAvailable ||
        (!string.IsNullOrWhiteSpace(_executablePath) &&
         File.Exists(_executablePath) &&
         !string.IsNullOrWhiteSpace(_ffmpegDirectory) &&
         File.Exists(Path.Combine(_ffmpegDirectory, "ffmpeg.exe")) &&
         File.Exists(Path.Combine(_ffmpegDirectory, "ffprobe.exe")));

    public string EngineName => _timelineAnalyzer.IsAvailable
        ? "ADB Subtitle Timeline v2"
        : "ffsubsync 0.5.1";

    public string AvailabilityMessage => IsAvailable
        ? (_timelineAnalyzer.IsAvailable
            ? "ADB Subtitle Timeline v2 otomatik altyazı senkronu hazır."
            : "ffsubsync 0.5.1 otomatik altyazı senkronu hazır.")
        : "FFmpeg/SubtitleSync runtime bulunamadı.";

    public async Task<SubtitleSyncResult> SynchronizeAsync(
        SubtitleSyncRequest request,
        IProgress<SubtitleSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable || _ffmpegDirectory is null)
        {
            return SubtitleSyncResult.Unavailable(AvailabilityMessage);
        }

        if (!File.Exists(request.SubtitlePath))
        {
            return SubtitleSyncResult.Unavailable("Senkronlanacak altyazı dosyası bulunamadı.");
        }

        var extension = Path.GetExtension(request.SubtitlePath);
        if (!SupportedSubtitleExtensions.Contains(extension))
        {
            return SubtitleSyncResult.Unavailable("Bu altyazı biçimi otomatik senkron için henüz desteklenmiyor.");
        }

        if (!CanUseReference(request.Reference, out var referenceError))
        {
            return SubtitleSyncResult.Unavailable(referenceError);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        TryDelete(request.OutputPath);
        progress?.Report(new SubtitleSyncProgress(3, "Altyazı senkronu hazırlanıyor"));

        if (_timelineAnalyzer.IsAvailable &&
            request.ReferenceAudioStreamIndex is not null)
        {
            var timeline = await _timelineAnalyzer
                .TrySynchronizeAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);

            if (timeline is { Accepted: true } &&
                !string.IsNullOrWhiteSpace(timeline.OutputPath) &&
                File.Exists(timeline.OutputPath))
            {
                var safety = await AssessSafeApplyAsync(
                        request.SubtitlePath,
                        timeline.OutputPath,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (safety.IsSafe)
                {
                    progress?.Report(new SubtitleSyncProgress(100, "Tamamlandı"));
                    _logger.Info(
                        $"Subtitle Timeline v2 kabul edildi: model={timeline.Model}, " +
                        $"coverage={timeline.VerificationCoverage:0.00}, " +
                        $"residual={timeline.VerificationResidualSeconds:0.000}s, " +
                        $"score={timeline.Score:0.000}");

                    return new SubtitleSyncResult(
                        timeline.AlreadyAligned
                            ? SubtitleSyncVerdict.AlreadyAligned
                            : SubtitleSyncVerdict.Synchronized,
                        timeline.Model,
                        timeline.OutputPath,
                        timeline.OffsetSeconds,
                        timeline.ScaleFactor,
                        timeline.Score,
                        timeline.SplitCount,
                        timeline.Message,
                        "ADB Subtitle Timeline v2",
                        timeline.VerificationResidualSeconds,
                        timeline.VerificationCoverage,
                        timeline.VerificationProbes);
                }

                _logger.Warning(
                    $"Subtitle Timeline v2 Safe Apply Gate reddi: {safety.Reason}");
                TryDelete(timeline.OutputPath);
            }
            else if (timeline is not null)
            {
                _logger.Info(
                    $"Subtitle Timeline v2 yeterli güven oluşturmadı: {timeline.Message}");
                TryDelete(timeline.OutputPath);
            }

            progress?.Report(new SubtitleSyncProgress(
                84,
                "Yedek senkron motoru doğrulanıyor"));
        }

        if (string.IsNullOrWhiteSpace(_executablePath) || !File.Exists(_executablePath))
        {
            return new SubtitleSyncResult(
                SubtitleSyncVerdict.Uncertain,
                SubtitleSyncModelKind.Fixed,
                null,
                0,
                1,
                0,
                0,
                "ADB Subtitle Timeline v2 yeterli güven oluşturmadı; yedek ffsubsync motoru mevcut değil.",
                "ADB Subtitle Timeline v2");
        }

        var logDirectory = Path.Combine(
            Path.GetDirectoryName(request.OutputPath)!,
            "ffsubsync-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };

        var args = process.StartInfo.ArgumentList;
        args.Add(request.Reference.Source);
        args.Add("-i");
        args.Add(Path.GetFullPath(request.SubtitlePath));
        args.Add("-o");
        args.Add(Path.GetFullPath(request.OutputPath));
        args.Add("--ffmpeg-path");
        args.Add(_ffmpegDirectory);
        if (request.ReferenceAudioStreamIndex is int referenceAudioIndex && referenceAudioIndex >= 0)
        {
            args.Add("--reference-stream");
            args.Add($"a:{referenceAudioIndex}");
            _logger.Info($"ffsubsync ses referansı: a:{referenceAudioIndex}");
        }
        args.Add("--split-penalty");
        args.Add(request.SplitPenalty.ToString("0.###", CultureInfo.InvariantCulture));
        args.Add("--max-offset-seconds");
        args.Add(Math.Clamp(request.MaxOffsetSeconds, 15, 300).ToString(CultureInfo.InvariantCulture));
        args.Add("--skip-sync-on-low-quality");
        args.Add("--quality-max-offset-seconds");
        args.Add(Math.Clamp(request.QualityMaxOffsetSeconds, 10, 180).ToString(CultureInfo.InvariantCulture));
        args.Add("--output-encoding");
        args.Add("utf-8");
        args.Add("--log-dir-path");
        args.Add(logDirectory);

        process.StartInfo.Environment["PATH"] = _ffmpegDirectory + Path.PathSeparator +
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("ffsubsync worker başlatılamadı.");
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process may already have exited.
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);

            var percent = 8;
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(exitTask, Task.Delay(750, cancellationToken)).ConfigureAwait(false);
                if (exitTask.IsCompleted) break;
                percent = Math.Min(82, percent + 2);
                progress?.Report(new SubtitleSyncProgress(percent, GetProgressStage(percent)));
            }

            await exitTask.ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            progress?.Report(new SubtitleSyncProgress(90, "Senkron sonucu doğrulanıyor"));

            var logPath = Path.Combine(logDirectory, "ffsubsync.log");
            var log = File.Exists(logPath)
                ? await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            var diagnostic = string.Join(Environment.NewLine, new[] { stdout, stderr, log }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (process.ExitCode != 0)
            {
                _logger.Info($"ffsubsync exit={process.ExitCode}: {TrimDiagnostic(diagnostic)}");
                TryDelete(request.OutputPath);
                return new SubtitleSyncResult(
                    SubtitleSyncVerdict.Uncertain,
                    SubtitleSyncModelKind.Fixed,
                    null,
                    0,
                    1,
                    0,
                    0,
                    "Altyazı otomatik olarak senkronlanamadı.");
            }

            var score = ParseDouble(ScoreRegex(), diagnostic, 0);
            var offset = ParseDouble(OffsetRegex(), diagnostic, 0);
            var scale = ParseDouble(FrameRateRegex(), diagnostic, 1);
            var splits = ParseInt(SplitRegex(), diagnostic, 0, group: 2);

            if (LowQualityRegex().IsMatch(diagnostic))
            {
                TryDelete(request.OutputPath);
                return new SubtitleSyncResult(
                    score < 0 ? SubtitleSyncVerdict.NoMatch : SubtitleSyncVerdict.Uncertain,
                    InferModel(scale, splits),
                    null,
                    offset,
                    scale,
                    score,
                    splits,
                    score < 0
                        ? "Bu altyazının video sürümüyle eşleştiği doğrulanamadı."
                        : "Senkron sonucu yeterince güvenilir bulunmadı; mevcut altyazı korunuyor.");
            }

            if (!File.Exists(request.OutputPath) || new FileInfo(request.OutputPath).Length == 0)
            {
                return new SubtitleSyncResult(
                    SubtitleSyncVerdict.Uncertain,
                    InferModel(scale, splits),
                    null,
                    offset,
                    scale,
                    score,
                    splits,
                    "ffsubsync senkronlu altyazı dosyasını oluşturmadı.");
            }

            var safety = await AssessSafeApplyAsync(
                    request.SubtitlePath,
                    request.OutputPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!safety.IsSafe)
            {
                _logger.Warning(
                    $"Subtitle Safe Apply Gate reddetti: {safety.Reason} · " +
                    $"original={safety.Original.CueCount} cue/{safety.Original.OverlapPairs} overlap/" +
                    $"{safety.Original.MaxConcurrent} concurrent · " +
                    $"output={safety.Output.CueCount} cue/{safety.Output.OverlapPairs} overlap/" +
                    $"{safety.Output.MaxConcurrent} concurrent");
                TryDelete(request.OutputPath);
                return new SubtitleSyncResult(
                    SubtitleSyncVerdict.Uncertain,
                    InferModel(scale, splits),
                    null,
                    offset,
                    scale,
                    score,
                    splits,
                    "Senkron sonucu güvenli doğrulamayı geçemedi; orijinal altyazı korunuyor.");
            }

            var alreadyAligned = Math.Abs(offset) <= 0.08 &&
                                 Math.Abs(scale - 1.0) <= 0.0005 &&
                                 splits == 0;
            progress?.Report(new SubtitleSyncProgress(100, "Tamamlandı"));

            return new SubtitleSyncResult(
                alreadyAligned ? SubtitleSyncVerdict.AlreadyAligned : SubtitleSyncVerdict.Synchronized,
                InferModel(scale, splits),
                request.OutputPath,
                offset,
                scale,
                score,
                splits,
                alreadyAligned
                    ? "Altyazı bu video sürümüyle zaten uyumlu."
                    : splits > 0
                        ? "Altyazı farklı kurgu noktalarına göre video sürümüne uyarlandı."
                        : Math.Abs(scale - 1.0) > 0.0005
                            ? "Altyazı zamanlaması ve kare hızı video sürümüne uyarlandı."
                            : "Altyazı video sürümüne göre otomatik senkronlandı.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(request.OutputPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(request.OutputPath);
            _logger.Error("Otomatik altyazı senkronu başarısız oldu.", exception);
            return new SubtitleSyncResult(
                SubtitleSyncVerdict.Uncertain,
                SubtitleSyncModelKind.Fixed,
                null,
                0,
                1,
                0,
                0,
                "Otomatik altyazı senkronu tamamlanamadı.");
        }
        finally
        {
            try { Directory.Delete(logDirectory, recursive: true); } catch { }
        }
    }

    private static async Task<SubtitleSafeApplyAssessment> AssessSafeApplyAsync(
        string originalPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var original = await ReadTimingMetricsAsync(originalPath, cancellationToken).ConfigureAwait(false);
        var output = await ReadTimingMetricsAsync(outputPath, cancellationToken).ConfigureAwait(false);

        if (original.CueCount < 2 || output.CueCount < 2)
        {
            return new SubtitleSafeApplyAssessment(
                false,
                "Altyazı zaman çizelgesi güvenilir biçimde okunamadı.",
                original,
                output);
        }

        var cueDeltaAllowance = Math.Max(2, (int)Math.Ceiling(original.CueCount * 0.02));
        if (Math.Abs(output.CueCount - original.CueCount) > cueDeltaAllowance)
        {
            return new SubtitleSafeApplyAssessment(
                false,
                "Senkron çıktısında beklenmeyen sayıda altyazı satırı oluştu.",
                original,
                output);
        }

        if (output.InvalidCueCount > original.InvalidCueCount)
        {
            return new SubtitleSafeApplyAssessment(
                false,
                "Senkron çıktısında geçersiz zaman aralıkları oluştu.",
                original,
                output);
        }

        var overlapAllowance = Math.Max(2, (int)Math.Ceiling(original.CueCount * 0.01));
        if (output.OverlapPairs > original.OverlapPairs + overlapAllowance)
        {
            return new SubtitleSafeApplyAssessment(
                false,
                "Senkron çıktısı yeni altyazı çakışmaları oluşturdu.",
                original,
                output);
        }

        var concurrentLimit = Math.Max(2, original.MaxConcurrent + 1);
        if (output.MaxConcurrent > concurrentLimit)
        {
            return new SubtitleSafeApplyAssessment(
                false,
                "Aynı anda ekranda görünen altyazı sayısı güvenli sınırı aştı.",
                original,
                output);
        }

        var overlapSecondsAllowance = Math.Max(
            1.0,
            original.TotalOverlapSeconds * 0.5 + 0.5);
        if (output.TotalOverlapSeconds > original.TotalOverlapSeconds + overlapSecondsAllowance)
        {
            return new SubtitleSafeApplyAssessment(
                false,
                "Senkron çıktısındaki toplam altyazı çakışması aşırı arttı.",
                original,
                output);
        }

        if (output.MaxDurationSeconds > Math.Max(20.0, original.MaxDurationSeconds * 1.75))
        {
            return new SubtitleSafeApplyAssessment(
                false,
                "Senkron çıktısında olağandışı uzun bir altyazı gösterim süresi oluştu.",
                original,
                output);
        }

        if (original.MedianDurationSeconds > 0.2 && output.MedianDurationSeconds > 0.2)
        {
            var durationRatio = output.MedianDurationSeconds / original.MedianDurationSeconds;
            if (durationRatio is < 0.70 or > 1.45)
            {
                return new SubtitleSafeApplyAssessment(
                    false,
                    "Altyazı gösterim süreleri senkron sırasında olağandışı değişti.",
                    original,
                    output);
            }
        }

        return new SubtitleSafeApplyAssessment(true, "OK", original, output);
    }

    private static async Task<SubtitleTimingMetrics> ReadTimingMetricsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var cues = new List<SubtitleCueTiming>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                var startText = line[..arrow].Trim();
                var endText = line[(arrow + 3)..].Trim();
                var cueSettings = endText.IndexOfAny([' ', '\t']);
                if (cueSettings >= 0)
                {
                    endText = endText[..cueSettings];
                }

                if (TryParseSubtitleTimestamp(startText, out var start) &&
                    TryParseSubtitleTimestamp(endText, out var end))
                {
                    cues.Add(new SubtitleCueTiming(start, end));
                }

                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line["Dialogue:".Length..].TrimStart();
            var fields = payload.Split(',', 10);
            if (fields.Length >= 3 &&
                TryParseSubtitleTimestamp(fields[1], out var assStart) &&
                TryParseSubtitleTimestamp(fields[2], out var assEnd))
            {
                cues.Add(new SubtitleCueTiming(assStart, assEnd));
            }
        }

        if (cues.Count == 0)
        {
            return SubtitleTimingMetrics.Empty;
        }

        var invalid = cues.Count(cue =>
            cue.StartSeconds < 0 ||
            cue.EndSeconds <= cue.StartSeconds);
        var valid = cues
            .Where(cue => cue.StartSeconds >= 0 && cue.EndSeconds > cue.StartSeconds)
            .OrderBy(cue => cue.StartSeconds)
            .ThenBy(cue => cue.EndSeconds)
            .ToArray();

        if (valid.Length == 0)
        {
            return new SubtitleTimingMetrics(
                cues.Count,
                invalid,
                0,
                0,
                0,
                0,
                0);
        }

        var overlapPairs = 0;
        var totalOverlapSeconds = 0.0;
        var maxConcurrent = 0;
        var activeEnds = new List<double>();

        foreach (var cue in valid)
        {
            for (var index = activeEnds.Count - 1; index >= 0; index--)
            {
                if (activeEnds[index] <= cue.StartSeconds + 0.04)
                {
                    activeEnds.RemoveAt(index);
                }
            }

            foreach (var activeEnd in activeEnds)
            {
                var overlap = Math.Min(activeEnd, cue.EndSeconds) - cue.StartSeconds;
                if (overlap <= 0.04)
                {
                    continue;
                }

                overlapPairs++;
                totalOverlapSeconds += overlap;
            }

            activeEnds.Add(cue.EndSeconds);
            maxConcurrent = Math.Max(maxConcurrent, activeEnds.Count);
        }

        var durations = valid
            .Select(cue => cue.EndSeconds - cue.StartSeconds)
            .OrderBy(value => value)
            .ToArray();
        var medianDuration = durations.Length % 2 == 1
            ? durations[durations.Length / 2]
            : (durations[(durations.Length / 2) - 1] + durations[durations.Length / 2]) / 2.0;

        return new SubtitleTimingMetrics(
            cues.Count,
            invalid,
            overlapPairs,
            totalOverlapSeconds,
            maxConcurrent,
            medianDuration,
            durations[^1]);
    }

    private static bool TryParseSubtitleTimestamp(string value, out double seconds)
    {
        seconds = 0;
        value = value.Trim().Replace(',', '.');
        if (value.Length == 0)
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length is not (2 or 3))
        {
            return false;
        }

        var hours = 0.0;
        var minutesIndex = 0;
        if (parts.Length == 3)
        {
            if (!double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
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

        if (hours < 0 || minutes is < 0 or >= 60 || secs is < 0 or >= 60.999)
        {
            return false;
        }

        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }

    private sealed record SubtitleCueTiming(
        double StartSeconds,
        double EndSeconds);

    private sealed record SubtitleTimingMetrics(
        int CueCount,
        int InvalidCueCount,
        int OverlapPairs,
        double TotalOverlapSeconds,
        int MaxConcurrent,
        double MedianDurationSeconds,
        double MaxDurationSeconds)
    {
        public static SubtitleTimingMetrics Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0);
    }

    private sealed record SubtitleSafeApplyAssessment(
        bool IsSafe,
        string Reason,
        SubtitleTimingMetrics Original,
        SubtitleTimingMetrics Output);

    private static bool CanUseReference(SubtitleSyncMediaSource reference, out string error)
    {
        error = string.Empty;
        if (File.Exists(reference.Source)) return true;

        if (!Uri.TryCreate(reference.Source, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Altyazı senkronu için video kaynağına erişilemiyor.";
            return false;
        }

        if (reference.HttpHeaders is { Count: > 0 })
        {
            error = "Özel HTTP başlığı gerektiren uzak kaynaklarda otomatik altyazı senkronu henüz kullanılamıyor.";
            return false;
        }

        return true;
    }

    private static SubtitleSyncModelKind InferModel(double scale, int splits)
    {
        if (splits > 0) return SubtitleSyncModelKind.Piecewise;
        return Math.Abs(scale - 1.0) > 0.0005
            ? SubtitleSyncModelKind.FrameRate
            : SubtitleSyncModelKind.Fixed;
    }

    private static string GetProgressStage(int percent) => percent switch
    {
        < 32 => "Video konuşmaları analiz ediliyor",
        < 62 => "Altyazı zamanları eşleştiriliyor",
        _ => "Sürüm ve kurgu farkları doğrulanıyor",
    };

    private static string? ResolveExecutable()
    {
        foreach (var root in RuntimeCandidates())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var name in new[] { "ffsubsync.exe", "ffs.exe", "subsync.exe" })
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
                var nested = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static IEnumerable<string> RuntimeCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "native", "subtitlesync");
        yield return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "native", "subtitlesync"));
    }

    private static string? ResolveFfmpegDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "bin"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "native", "ffmpeg", "bin")),
        };
        return candidates.FirstOrDefault(path =>
            File.Exists(Path.Combine(path, "ffmpeg.exe")) &&
            File.Exists(Path.Combine(path, "ffprobe.exe")));
    }

    private static double ParseDouble(Regex regex, string text, double fallback)
    {
        var match = regex.Match(text);
        return match.Success && double.TryParse(
            match.Groups[1].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;
    }

    private static int ParseInt(Regex regex, string text, int fallback, int group = 1)
    {
        var match = regex.Match(text);
        return match.Success && int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string TrimDiagnostic(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 700 ? line : line[..700] + "…";
    }

    [GeneratedRegex(@"score:\s*([-+0-9.eE]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScoreRegex();

    [GeneratedRegex(@"offset seconds:\s*([-+0-9.eE]+)", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"framerate scale factor:\s*([-+0-9.eE]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FrameRateRegex();

    [GeneratedRegex(@"split alignment:\s*(\d+)\s+segment\(s\),\s*(\d+)\s+split\(s\)", RegexOptions.IgnoreCase)]
    private static partial Regex SplitRegex();

    [GeneratedRegex(@"low-quality alignment|leaving subtitles unmodified", RegexOptions.IgnoreCase)]
    private static partial Regex LowQualityRegex();
}
