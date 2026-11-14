using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.App.Services;

/// <summary>
/// Exports embedded tracks from local, user-selected media only. Remote URLs
/// and stream Authorization headers are deliberately never passed to ffmpeg.
/// Audio is stream-copied; text subtitles are transcoded to SRT without OCR.
/// </summary>
public static class EmbeddedTrackExportService
{
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text",
        "subviewer", "microdvd", "mpl2", "sami", "realtext"
    };

    public static string AudioExtension(MediaTrack track) => (track.Codec ?? string.Empty).ToLowerInvariant() switch
    {
        "aac" => ".aac",
        "ac3" => ".ac3",
        "eac3" => ".eac3",
        "dts" => ".dts",
        "truehd" => ".thd",
        "flac" => ".flac",
        "mp3" => ".mp3",
        "opus" => ".opus",
        "vorbis" => ".ogg",
        "alac" => ".m4a",
        "pcm_s16le" or "pcm_s24le" or "pcm_s32le" => ".wav",
        _ => ".mka"
    };

    public static bool CanExportTextSubtitle(MediaTrack track) =>
        track.Type == TrackType.Subtitle && !track.IsExternal && !track.IsDisabled &&
        TextSubtitleCodecs.Contains(track.Codec ?? string.Empty);

    // Reject exports with no actual subtitles, even when ffmpeg created
    // an SRT file containing an arrow, bare timestamps or empty styling.
    private static readonly Regex SrtTimingLine = new(
        @"^\s*\d{2,}:\d{2}:\d{2},\d{3}\s*-->\s*\d{2,}:\d{2}:\d{2},\d{3}(?:\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SrtFormatting = new(
        @"<[^>]*>|\{\\[^}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool ContainsReadableSrtCue(string path)
    {
        var insideCue = false;
        foreach (var line in File.ReadLines(path))
        {
            if (SrtTimingLine.IsMatch(line))
            {
                insideCue = true;
                continue;
            }
            if (!insideCue) continue;
            if (string.IsNullOrWhiteSpace(line))
            {
                insideCue = false;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(SrtFormatting.Replace(line, string.Empty)))
                return true;
        }
        return false;
    }

    // Percentage is based on ffmpeg media timestamps, not wall-clock time.
    // 100% is reserved for the validated output, after the final file move.
    internal static double? ParseProgressPercent(string line, double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            !line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
            !long.TryParse(line.AsSpan("out_time_us=".Length),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0)
            return null;
        return Math.Clamp(us / (durationSeconds * 10000d), 0, 99);
    }

    private static async Task ReadProgressAsync(
        StreamReader reader, double durationSeconds, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        double previous = -1;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            var percent = ParseProgressPercent(line, durationSeconds);
            if (percent.HasValue && percent.Value >= previous + 1)
            {
                previous = percent.Value;
                progress?.Report(percent.Value);
            }
        }
    }

    public static async Task ExportAsync(
        string inputFile, string outputFile, TrackType type, int streamOrdinal,
        IProgress<double>? progress = null, double durationSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if (type is not (TrackType.Audio or TrackType.Subtitle))
            throw new ArgumentOutOfRangeException(nameof(type));
        if (streamOrdinal < 0 || streamOrdinal >= 256)
            throw new ArgumentOutOfRangeException(nameof(streamOrdinal));
        // No remote/UNC source is handed to ffmpeg or copied into a temporary file.
        // A downloaded media file can be reopened from the local disk instead.
        if (string.IsNullOrWhiteSpace(inputFile) ||
            inputFile.StartsWith(@"\\", StringComparison.Ordinal) ||
            inputFile.StartsWith("//", StringComparison.Ordinal) ||
            (Uri.TryCreate(inputFile, UriKind.Absolute, out var uri) &&
             uri.Scheme is "http" or "https"))
            throw new InvalidOperationException("Dışa aktarma yalnızca yerel dosyalarda kullanılabilir. Dosyayı önce yerel diske kaydedip açın.");
        var fullInput = Path.GetFullPath(inputFile);
        var fullOutput = Path.GetFullPath(outputFile);
        if (!File.Exists(fullInput))
            throw new InvalidOperationException("Bulut/uzak akıştan doğrudan dışa aktarma henüz desteklenmiyor. Dosyayı önce yerel diske kaydedin.");
        if (File.Exists(fullOutput) || string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Hedef dosya zaten mevcut. Başka bir dosya adı seçin.");

        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "bin", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("FFmpeg çalışma bileşeni bulunamadı. Tam ADB Player paketini kullanın.");

        var folder = Path.GetDirectoryName(fullOutput)
                     ?? throw new InvalidOperationException("Hedef dizin belirlenemedi.");
        Directory.CreateDirectory(folder);
        var extension = Path.GetExtension(fullOutput);
        var staging = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".partial" + extension);

        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", fullInput,
                                            "-map", "0:" + (type == TrackType.Audio ? "a:" : "s:") + streamOrdinal,
                                            "-vn", "-dn" })
            start.ArgumentList.Add(argument);

        if (type == TrackType.Audio)
        {
            start.ArgumentList.Add("-sn");
            start.ArgumentList.Add("-c:a");
            start.ArgumentList.Add("copy");
        }
        else
        {
            start.ArgumentList.Add("-an");
            start.ArgumentList.Add("-c:s");
            start.ArgumentList.Add("srt");
        }
        start.ArgumentList.Add("-progress");
        start.ArgumentList.Add("pipe:1");
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add(staging);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("FFmpeg başlatılamadı.");
            progress?.Report(0);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdoutTask = ReadProgressAsync(process.StandardOutput, durationSeconds, progress, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Dışa aktarma iptal edildi.", cancellationToken);
                throw new TimeoutException("Dışa aktarma zaman sınırına ulaştı.");
            }
            if (process.ExitCode != 0 || !File.Exists(staging) ||
                new FileInfo(staging).Length == 0 ||
                (type == TrackType.Subtitle &&
                 !ContainsReadableSrtCue(staging)))
                throw new InvalidOperationException(
                    "Seçili parça dışa aktarılamadı veya çıktı boş. Kaydedilecek dosya oluşturulmadı.");
            // A late Cancel must still prevent publishing the verified staging file.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, fullOutput, overwrite: false);
            progress?.Report(100);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch (IOException) { }
        }
    }

}
