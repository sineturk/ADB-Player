namespace AltyaziDB.Player.Core.Models;

/// <summary>
/// Pure audio-only rendering decisions shared by WPF and offline fixtures.
/// Never interprets absent decoder data as proof that a video has no video track.
/// </summary>
public static class AudioOnlyVisualPolicy
{
    public static bool ShouldShow(PlaybackSnapshot snapshot, bool mediaCenterVisible)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !mediaCenterVisible && !snapshot.IsIdle &&
               !string.IsNullOrWhiteSpace(snapshot.Source) &&
               !snapshot.HasVideo &&
               (snapshot.HasAudio || LocalAudioFilePolicy.IsAudio(snapshot.Source));
    }

    public static string CodecLabel(string? codec, string? source)
    {
        codec = codec?.Trim();
        if (string.IsNullOrWhiteSpace(codec))
        {
            var extension = Path.GetExtension(source ?? string.Empty);
            return string.IsNullOrEmpty(extension) ? "AUDIO" : extension.TrimStart('.').ToUpperInvariant();
        }
        return codec.ToLowerInvariant() switch
        {
            "eac3" => "E-AC-3",
            "ac3" => "AC-3",
            "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" => "PCM",
            "vorbis" => "VORBIS",
            _ => codec.ToUpperInvariant()
        };
    }

    public static string QualityLabel(PlaybackSnapshot snapshot, string mono, string stereo)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var pieces = new List<string>();
        if (snapshot.AudioChannelCount is int channels and > 0)
            pieces.Add(channels switch
            {
                1 => mono,
                2 => stereo,
                6 => "5.1",
                8 => "7.1",
                _ => channels.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ch"
            });
        if (snapshot.AudioSampleRateHz is int rate and > 0)
            pieces.Add((rate / 1000d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " kHz");
        if (snapshot.AudioBitrateKbps is int bitrate and > 0)
            pieces.Add(bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture) + " kbps");
        return string.Join(" · ", pieces);
    }
}
