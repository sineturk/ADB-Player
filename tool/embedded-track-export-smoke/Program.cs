using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Models;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("[P4.16.8.1 FAIL] " + label);
    Console.WriteLine("[PASS] " + label);
}

var folder = Path.Combine(Path.GetTempPath(), "adb-track-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    string Fixture(string name, string value)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, value);
        return path;
    }

    Check(EmbeddedTrackExportService.ContainsReadableSrtCue(Fixture(
        "valid.srt", "1\n00:00:00,000 --> 00:00:01,500\nMerhaba\n\n")),
        "A real subtitle cue passes");
    Check(!EmbeddedTrackExportService.ContainsReadableSrtCue(Fixture(
        "empty.srt", "")), "Empty SRT is rejected");
    Check(!EmbeddedTrackExportService.ContainsReadableSrtCue(Fixture(
        "fake.srt", "1\n-->\n\n")), "A fake arrow is not a cue");
    Check(!EmbeddedTrackExportService.ContainsReadableSrtCue(Fixture(
        "no-text.srt", "1\n00:00:00,000 --> 00:00:01,500\n\n")),
        "Timing-only subtitle is rejected");
    Check(!EmbeddedTrackExportService.ContainsReadableSrtCue(Fixture(
        "markup.srt", "1\n00:00:00,000 --> 00:00:01,500\n<i></i>\n\n")),
        "Markup-only subtitle is rejected");
    Check(EmbeddedTrackExportService.ContainsReadableSrtCue(Fixture(
        "styled.srt", "1\n00:00:00,000 --> 00:00:01,500\n<i>Merhaba</i>\n")),
        "Styled text subtitle passes");

    var text = new MediaTrack(0, TrackType.Subtitle, null, "tr", "ass", false, false);
    var image = text with { Codec = "hdmv_pgs_subtitle" };
    Check(EmbeddedTrackExportService.CanExportTextSubtitle(text) &&
          !EmbeddedTrackExportService.CanExportTextSubtitle(image),
        "ASS can export as SRT; PGS is blocked without OCR");
    Check(EmbeddedTrackExportService.AudioExtension(
          new MediaTrack(0, TrackType.Audio, null, "tr", "eac3", false, false)) == ".eac3",
        "Audio stream-copy retains EAC3 codec extension");

    foreach (var remote in new[] { "https://example.invalid/movie.mkv", @"\\server\media\movie.mkv" })
    {
        var rejected = false;
        try
        {
            await EmbeddedTrackExportService.ExportAsync(remote,
                Path.Combine(folder, "should-not-exist.srt"), TrackType.Subtitle, 0);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected && !File.Exists(Path.Combine(folder, "should-not-exist.srt")),
            "Remote source cannot create output: " + (remote.StartsWith("https:") ? "HTTP" : "UNC"));
    }
}
finally
{
    try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
}
Check(EmbeddedTrackExportService.ParseProgressPercent("out_time_us=2500000", 10) is double p &&
      Math.Abs(p - 25) < 0.01, "Progress reflects processed media time");
Check(EmbeddedTrackExportService.ParseProgressPercent("out_time_us=999999999", 10) == 99,
      "100 percent is reserved for verified output");
Check(EmbeddedTrackExportService.ParseProgressPercent("out_time_us=bad", 10) is null &&
      EmbeddedTrackExportService.ParseProgressPercent("out_time_us=2500000", 0) is null,
      "Malformed progress and unknown duration never fabricate a percent");
Console.WriteLine("[PASS] P4.16.9 export validation smoke (no media playback required)");
