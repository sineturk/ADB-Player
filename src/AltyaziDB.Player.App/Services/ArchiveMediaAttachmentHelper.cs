using System.IO;
using System.IO.Compression;

namespace AltyaziDB.Player.App.Services;

public enum ArchiveAttachmentKind { Audio, Subtitle }

public sealed record ArchiveMediaAttachment(ArchiveAttachmentKind Kind, string FilePath, string DisplayName);

/// <summary>
/// Extract only explicitly selected audio/subtitle entries from a *single* ZIP.
/// The original ZIP is never modified. No archive paths are materialized.
/// </summary>
public static class ArchiveMediaAttachmentHelper
{
    private const int MaximumEntries = 4096;
    private const long MaximumAudioBytes = 16L * 1024 * 1024 * 1024;
    private const long MaximumSubtitleBytes = 128L * 1024 * 1024;
    private const long MaximumSelectedBytes = MaximumAudioBytes + 256L * 1024 * 1024;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".truehd",
        ".flac", ".mka", ".m4a", ".mp3", ".ogg", ".opus", ".wav"
    };
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup"
    };

    public static bool IsAudio(string pathOrName) =>
        AudioExtensions.Contains(Path.GetExtension(pathOrName));
    public static bool IsSubtitle(string pathOrName) =>
        SubtitleExtensions.Contains(Path.GetExtension(pathOrName));
    public static bool IsZip(string pathOrName) =>
        Path.GetExtension(pathOrName).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<ArchiveMediaAttachment>> SelectAndExtractAsync(
        string archivePath,
        string outputRoot,
        IUserDialogService dialogs,
        bool audioOnly = false,
        bool subtitleOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(dialogs);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("ZIP arşivi bulunamadı.", archivePath);
        if (!IsZip(archivePath)) throw new InvalidDataException("Bu işlemde yalnız tek dosyalı ZIP desteklenir.");
        if (audioOnly && subtitleOnly) throw new ArgumentException("Ses ve altyazı filtreleri aynı anda seçilemez.");

        var entries = await Task.Run(() => Inspect(archivePath, audioOnly, subtitleOnly, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        if (entries.Count == 0)
            throw new InvalidDataException("ZIP içinde desteklenen ses veya altyazı dosyası bulunamadı.");

        IReadOnlyList<int> selected = entries.Count == 1
            ? [0]
            : dialogs.SelectItems(
                audioOnly ? "ZIP içinden ses seçin" : subtitleOnly ? "ZIP içinden altyazı seçin" : "ZIP içinden ses / altyazı seçin",
                entries.Select(entry => entry.DisplayLabel).ToArray(),
                initiallySelected: [0],
                allowMultiple: true);
        if (selected.Count == 0) return [];

        var chosen = selected.Distinct()
            .Select(i => i >= 0 && i < entries.Count ? entries[i] : throw new InvalidDataException("ZIP seçim indeksi geçersiz."))
            .ToArray();
        long total = 0;
        foreach (var entry in chosen)
        {
            var max = entry.Kind == ArchiveAttachmentKind.Audio ? MaximumAudioBytes : MaximumSubtitleBytes;
            if (entry.Size <= 0 || entry.Size > max || total > MaximumSelectedBytes - entry.Size)
                throw new InvalidDataException("Seçilen arşiv dosyası güvenli çıkarma sınırını aşıyor.");
            total += entry.Size;
        }

        // Random isolated directory; entry names can never escape into parent paths.
        var folder = Path.Combine(outputRoot, "zip-attachments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var results = new List<ArchiveMediaAttachment>();
        try
        {
            await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var item in chosen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Index >= zip.Entries.Count) throw new InvalidDataException("ZIP içeriği değişmiş.");
                var entry = zip.Entries[item.Index];
                if (entry.FullName != item.EntryName || entry.Length != item.Size || IsSymbolicLink(entry))
                    throw new InvalidDataException("ZIP girdisi güvenli değil veya arşiv içeriği değişmiş.");
                var safeName = SafeFileName(Path.GetFileName(entry.FullName.Replace('\\', '/')));
                var target = Path.Combine(folder, $"{results.Count:D2}-{safeName}");
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long extracted = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    extracted += read;
                    if (extracted > item.Size || extracted > (item.Kind == ArchiveAttachmentKind.Audio ? MaximumAudioBytes : MaximumSubtitleBytes))
                        throw new InvalidDataException("ZIP açılırken güvenli boyut sınırı aşıldı.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (extracted != item.Size) throw new InvalidDataException("ZIP girdisi beklenen uzunlukta değil.");
                results.Add(new ArchiveMediaAttachment(item.Kind, target, Path.GetFileName(entry.FullName.Replace('\\', '/'))));
            }
            return results;
        }
        catch
        {
            // Never delete the original archive or any pre-existing user files.
            try { Directory.Delete(folder, recursive: true); } catch { }
            throw;
        }
    }

    private static IReadOnlyList<Candidate> Inspect(
        string path, bool audioOnly, bool subtitleOnly, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > MaximumEntries)
            throw new InvalidDataException("ZIP arşivinde izin verilenden fazla girdi var.");
        var found = new List<Candidate>();
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.Entries[i];
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || IsSymbolicLink(entry))
                continue;
            var name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) continue;
            var kind = IsAudio(name) ? ArchiveAttachmentKind.Audio :
                IsSubtitle(name) ? ArchiveAttachmentKind.Subtitle : (ArchiveAttachmentKind?)null;
            if (kind is null || (audioOnly && kind != ArchiveAttachmentKind.Audio) ||
                (subtitleOnly && kind != ArchiveAttachmentKind.Subtitle))
                continue;
            found.Add(new Candidate(i, entry.FullName, kind.Value, entry.Length));
        }
        return found.OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.EntryName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(safe) ? "attachment.bin" : safe;
    }

    private sealed record Candidate(int Index, string EntryName, ArchiveAttachmentKind Kind, long Size)
    {
        public string DisplayLabel =>
            $"{(Kind == ArchiveAttachmentKind.Audio ? "Ses" : "Altyazı")} · {Path.GetFileName(EntryName.Replace('\\', '/'))} · {Size / 1024d / 1024d:0.##} MB";
    }
}
