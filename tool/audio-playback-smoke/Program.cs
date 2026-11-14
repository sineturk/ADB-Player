using AltyaziDB.Player.Core.Models;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("[P4.15.5 FAIL] " + label);
    Console.WriteLine("[PASS] " + label);
}

var video = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4" };
foreach (var ext in new[] {
    ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus",
    ".wma", ".mka", ".aif", ".aiff", ".ac3", ".eac3", ".dts"
})
{
    Check(LocalAudioFilePolicy.IsAudio("C:/Music/Sample" + ext) &&
          LocalAudioFilePolicy.IsAudio("C:/Music/Sample" + ext.ToUpperInvariant()) &&
          LocalAudioFilePolicy.IsPlayable("C:/Music/Sample" + ext, video),
          "Standalone audio recognized: " + ext);
}
Check(LocalAudioFilePolicy.IsPlayable("C:/Video/clip.MKV", video) &&
      !LocalAudioFilePolicy.IsAudio("C:/Video/clip.MKV"),
      "Existing video support remains independent of audio");
Check(!LocalAudioFilePolicy.IsPlayable("C:/Music/notes.srt", video) &&
      !LocalAudioFilePolicy.IsPlayable("C:/Music/archive.zip", video) &&
      !LocalAudioFilePolicy.IsPlayable("C:/Music/program.exe", video),
      "Subtitle/archive/program files cannot enter the music playlist");
Check(!LocalAudioFilePolicy.IsAudio(null) &&
      !LocalAudioFilePolicy.IsAudio("") &&
      !LocalAudioFilePolicy.IsPlayable("", video),
      "Empty source is not playable");
var patterns = LocalAudioFilePolicy.FileDialogPattern.Split(';');
Check(patterns.Length == 14 && patterns.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 14,
      "Audio dialog patterns are complete and unique");
Check(patterns.Contains("*.mp3") && patterns.Contains("*.flac") && patterns.Contains("*.wav"),
      "MP3, FLAC and WAV are visible in the main file picker");
// P4.15.5a: core-only audio visual state and quality policy.
var audioSnapshot = new PlaybackSnapshot("C:/Music/Track.FLAC", "Track", 12, 241,
    80, 1, false, false, false, false, true, "flac", 96000, 2, 2400);
Check(AudioOnlyVisualPolicy.ShouldShow(audioSnapshot, false),
    "Decoded local FLAC shows the audio-only surface without video");
Check(!AudioOnlyVisualPolicy.ShouldShow(audioSnapshot with { HasVideo = true }, false),
    "Video track always wins; audio art cannot obscure actual video");
Check(!AudioOnlyVisualPolicy.ShouldShow(audioSnapshot, true) &&
      !AudioOnlyVisualPolicy.ShouldShow(audioSnapshot with { IsIdle = true }, false),
    "Audio surface is hidden in media center and when libmpv is idle");
var remoteAudio = audioSnapshot with { Source = "https://test.example/stream", AudioCodecName = "eac3" };
Check(AudioOnlyVisualPolicy.ShouldShow(remoteAudio, false) &&
      AudioOnlyVisualPolicy.CodecLabel("eac3", remoteAudio.Source) == "E-AC-3",
    "Actual decoded audio without an audio extension still shows and names EAC3");
Check(AudioOnlyVisualPolicy.CodecLabel(null, "C:/Music/Track.MP3") == "MP3" &&
      AudioOnlyVisualPolicy.CodecLabel("pcm_s16le", "C:/Music/Sample.wav") == "PCM",
    "Audio codec fallback uses extension only when decoder info is missing");
Check(AudioOnlyVisualPolicy.QualityLabel(audioSnapshot, "Mono", "Stereo") ==
      "Stereo · 96 kHz · 2400 kbps",
    "Audio quality displays only real decoder channel, sample and bitrate");
Check(AudioOnlyVisualPolicy.QualityLabel(audioSnapshot with
      { AudioSampleRateHz = null, AudioChannelCount = null, AudioBitrateKbps = null },
      "Mono", "Stereo") == "",
    "Unavailable audio metadata never becomes invented technical information");
Console.WriteLine("[PASS] Local audio extension policy smoke; no native libmpv or real audio playback");
