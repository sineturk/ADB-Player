namespace AltyaziDB.Player.Core.Models;

/// <summary>
/// Local audio files that libmpv can demux and decode without external
/// transcoding or additional codec packages. These extensions affect file
/// discovery only: libmpv remains the final authority on decodability.
/// </summary>
public static class LocalAudioFilePolicy
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma",
        ".mka", ".aif", ".aiff", ".ac3", ".eac3", ".dts"
    };

    public static bool IsAudio(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath) &&
        Extensions.Contains(Path.GetExtension(filePath));

    public static bool IsPlayable(string? filePath, IReadOnlySet<string> videoExtensions) =>
        IsAudio(filePath) ||
        (!string.IsNullOrWhiteSpace(filePath) &&
         videoExtensions.Contains(Path.GetExtension(filePath)));

    public static string FileDialogPattern =>
        string.Join(";", Extensions.OrderBy(x => x, StringComparer.Ordinal).Select(x => "*" + x));
}
