using System.Collections;
using System.IO;
using AltyaziDB.Player.Core.Models;
using TagLibSharp2.Core;
using TagLibSharp2.Mpeg;
using TagLibSharp2.Xiph;
using TagLibSharp2.Mp4;
using TagLibSharp2.Riff;

namespace AltyaziDB.Player.App.Services;

public sealed record LocalAudioVisualMetadata(
    string? Title, string? Artist, string? Album, byte[]? CoverBytes)
{
    public static LocalAudioVisualMetadata Empty { get; } = new(null, null, null, null);
}

/// <summary>
/// Reads optional tags and small cover artwork from a *local*, standalone audio file.
/// Never requests artwork from external services or reads Telegram/remote media.
/// The caller runs this off the UI thread and cancels stale source completions.
/// </summary>
internal static class LocalAudioVisualMetadataReader
{
    private const int MaxCoverBytes = 4 * 1024 * 1024;
    private static readonly string[] CoverFiles =
        ["cover.jpg", "folder.jpg", "front.jpg", "album.jpg", "cover.png", "folder.png"];

    public static LocalAudioVisualMetadata Read(string? source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            !LocalAudioFilePolicy.IsAudio(source) || !File.Exists(source))
            return LocalAudioVisualMetadata.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        string? title = null;
        string? artist = null;
        string? album = null;
        byte[]? image = null;
        try
        {
            // TagLibSharp2 is a metadata parser, not an audio decoder.
            var tags = MediaFile.Read(source);
            if (tags.IsSuccess)
            {
                title = Trim(tags.Tag?.Title);
                artist = Trim(tags.Tag?.Artist);
                album = Trim(tags.Tag?.Album);
            }
        }
        catch (Exception exception) when (exception is IOException or
                 UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Damaged or unsupported tags never prevent libmpv playback.
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var extension = Path.GetExtension(source).ToLowerInvariant();
            object? file = extension switch
            {
                ".mp3" => GetMp3(source),
                ".flac" => GetFlac(source),
                ".m4a" => GetMp4(source),
                ".wav" => GetWav(source),
                _ => null
            };
            if (file is not null) image = TryEmbeddedPicture(file);
        }
        catch (Exception exception) when (exception is IOException or
                 UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Fallback to an adjacent album image or the built-in ADB logo.
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (image is null)
        {
            try
            {
                var folder = Path.GetDirectoryName(source);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    foreach (var name in CoverFiles)
                    {
                        var candidate = Path.Combine(folder, name);
                        if (!File.Exists(candidate)) continue;
                        var file = new FileInfo(candidate);
                        if (file.Length is <= 0 or > MaxCoverBytes) continue;
                        var bytes = File.ReadAllBytes(candidate);
                        if (IsImage(bytes)) { image = bytes; break; }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or
                     UnauthorizedAccessException or ArgumentException)
            {
                // Album images are optional, not required for audio playback.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new LocalAudioVisualMetadata(title, artist, album, image);
    }

    private static object? GetMp3(string source)
    {
        var result = Mp3File.ReadFromFile(source);
        return result.IsSuccess ? result.File : null;
    }

    private static object? GetFlac(string source)
    {
        var result = FlacFile.ReadFromFile(source);
        return result.IsSuccess ? result.File : null;
    }

    private static object? GetMp4(string source)
    {
        var result = Mp4File.ReadFromFile(source);
        return result.IsSuccess ? result.File : null;
    }

    private static object? GetWav(string source)
    {
        var result = WavFile.ReadFromFile(source);
        return result.IsSuccess ? result.File : null;
    }

    private static byte[]? TryEmbeddedPicture(object mediaFile)
    {
        // Every supported format has its own typed picture model. Avoid
        // exposing this parser implementation to the playback ViewModel.
        var pictures = mediaFile.GetType().GetProperty("Pictures")?.GetValue(mediaFile);
        if (pictures is not IEnumerable items) return null;
        foreach (var item in items)
        {
            if (item is null) continue;
            var data = item.GetType().GetProperty("PictureData")?.GetValue(item);
            if (data is null) continue;
            var size = data.GetType().GetProperty("Length")?.GetValue(data);
            if (size is int count && count > MaxCoverBytes) continue;
            var bytes = data.GetType().GetMethod("ToArray", Type.EmptyTypes)?.Invoke(data, null) as byte[];
            if (bytes is { Length: > 0 and <= MaxCoverBytes } && IsImage(bytes))
                return bytes;
        }
        return null;
    }

    private static bool IsImage(byte[] bytes) =>
        (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 &&
         bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10) ||
        (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255);

    private static string? Trim(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null :
            text.Length <= 180 ? text : text[..180];
    }
}
