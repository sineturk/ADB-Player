using System.Globalization;
using System.IO;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace AltyaziDB.Player.App.Services;

public static class ArchiveAudioHelper
{
    private const long MaximumExtractedAudioBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumEntriesToInspect = 4000;

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".truehd",
        ".flac", ".mka", ".m4a", ".mp3", ".ogg", ".opus", ".wav"
    };

    public static bool IsSupportedArchive(string pathOrName) =>
        ArchiveExtensions.Contains(Path.GetExtension(pathOrName));

    public static async Task<string?> ExtractAudioAsync(
        string archivePath,
        string outputDirectory,
        IUserDialogService dialogs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(dialogs);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Ses arşivi bulunamadı.", archivePath);
        if (!IsSupportedArchive(archivePath)) throw new InvalidDataException("Yalnız ZIP, RAR ve 7-Zip ses arşivleri desteklenir.");

        var entries = await Task.Run(() => Inspect(archivePath, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (entries.Count == 0) throw new InvalidDataException("Arşivde desteklenen bir ses dosyası bulunamadı.");

        var selectedIndex = 0;
        if (entries.Count > 1)
        {
            var selected = dialogs.SelectItems(
                "Arşivden ses dosyası seçin",
                entries.Select(entry => entry.DisplayLabel).ToArray());
            if (selected.Count == 0) return null;
            selectedIndex = selected[0];
        }

        var selectedEntry = entries[selectedIndex];
        if (selectedEntry.IsEncrypted)
            throw new InvalidDataException("Seçilen ses dosyası şifreli. Parolalı arşivler desteklenmiyor.");
        if (selectedEntry.Size > MaximumExtractedAudioBytes)
            throw new InvalidDataException("Arşivdeki ses dosyası güvenli çıkarma sınırını aşıyor.");

        Directory.CreateDirectory(outputDirectory);
        var destinationPath = Path.Combine(
            outputDirectory,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{SafeFileName(Path.GetFileName(selectedEntry.Key))}");
        return await ExtractAsync(archivePath, selectedEntry.Key, destinationPath, cancellationToken).ConfigureAwait(true);
    }

    private static IReadOnlyList<ArchiveAudioEntry> Inspect(string path, CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(path);
        using var archive = OpenArchive(file, path);
        var entries = new List<ArchiveAudioEntry>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumEntriesToInspect)
                throw new InvalidDataException("Arşiv çok fazla dosya içeriyor.");
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key)) continue;
            if (!AudioExtensions.Contains(Path.GetExtension(entry.Key))) continue;
            entries.Add(new ArchiveAudioEntry(entry.Key, entry.Size, entry.IsEncrypted));
        }
        return entries.OrderByDescending(entry => entry.Size).ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<string> ExtractAsync(
        string archivePath,
        string entryKey,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = OpenArchive(file, archivePath);
        var normalized = Normalize(entryKey);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            !candidate.IsDirectory && Normalize(candidate.Key ?? string.Empty).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Arşivde seçilen ses dosyası bulunamadı.");
        if (entry.IsEncrypted) throw new InvalidDataException("Parolalı arşivler desteklenmiyor.");

        try
        {
            await using var input = entry.OpenEntryStream();
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            return destinationPath;
        }
        catch
        {
            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
            throw;
        }
    }

    private static IArchive OpenArchive(Stream stream, string archivePath) => ArchiveFactory.OpenArchive(
        stream,
        new ReaderOptions
        {
            LeaveStreamOpen = true,
            LookForHeader = true,
            ExtensionHint = Path.GetExtension(archivePath).TrimStart('.').ToLowerInvariant()
        });

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "harici-ses.bin" : safe;
    }

    private sealed record ArchiveAudioEntry(string Key, long Size, bool IsEncrypted)
    {
        public string DisplayLabel => $"{Path.GetFileName(Key)} · {FormatSize(Size)}{(IsEncrypted ? " · Şifreli" : string.Empty)}";

        private static string FormatSize(long value)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = Math.Max(0, value);
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size.ToString("0.##", CultureInfo.CurrentCulture)} {units[unit]}";
        }
    }
}
