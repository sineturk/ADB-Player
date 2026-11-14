using AltyaziDB.Player.Core.Models;
using MonoTorrent;

namespace AltyaziDB.Player.Torrent;

/// <summary>
/// Read-only plan for same-PC qBittorrent handoff. The actual MonoTorrent file
/// paths, not an inferred torrent-name folder, define the expected layout.
/// </summary>
public sealed record SmartTorrentHandoffPlan(
    string TorrentFile,
    string TorrentHash,
    string SaveDirectory,
    string ContentDirectory,
    IReadOnlyList<string> ExpectedFiles,
    string ContentLayout)
{
    public bool IsMagnet => TorrentFile.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);
}

public static class SmartTorrentHandoffPlanner
{
    public static async Task<SmartTorrentHandoffPlan> CreateAsync(
        TorrentSessionSnapshot session,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        if (session.Files.Count == 0)
            throw new InvalidOperationException("Torrent dosyaları henüz hazır değil.");

        var saveDirectory = Path.GetFullPath(downloadDirectory);
        if (!Directory.Exists(saveDirectory))
            throw new InvalidOperationException("İndirme klasörü bulunamadı.");

        var source = session.Source;
        string hash;
        if (source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            if (source.Length > 65536)
                throw new InvalidOperationException("Magnet bağlantısı çok uzun.");
            hash = ReadMagnetInfoHash(source);
        }
        else if (source.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) &&
                 File.Exists(source))
        {
            var metadata = await MonoTorrent.Torrent.LoadAsync(source).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            hash = metadata.InfoHashes.V1?.ToHex() ?? metadata.InfoHashes.V2?.ToHex()
                ?? throw new InvalidOperationException("Torrent kimliği doğrulanamadı.");
            if (metadata.Files.Count != session.Files.Count)
                throw new InvalidOperationException("Torrent dosya listesi eşleşmiyor.");

            var expectedMetadata = metadata.Files
                .Select(f => (Name: Path.GetFileName(f.Path), Size: f.Length))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Size).ToArray();
            var actual = session.Files
                .Select(f => (Name: f.Name, Size: f.Length))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Size).ToArray();
            if (!expectedMetadata.SequenceEqual(actual))
                throw new InvalidOperationException("Torrent dosyaları meta veriyle eşleşmiyor.");
        }
        else
        {
            throw new InvalidOperationException("Torrent kaynağı magnet veya .torrent olmalıdır.");
        }

        if ((hash.Length != 40 && hash.Length != 64) || !hash.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Torrent kimliği doğrulanamadı.");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anyExistingData = false;
        foreach (var file in session.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(file.Path);
            if (!IsInside(saveDirectory, full) || !files.Add(full))
                throw new InvalidOperationException("Dosya yolu indirme klasörüyle uyuşmuyor.");
            if (!File.Exists(full)) continue;
            var size = new FileInfo(full).Length;
            if (size > file.Length)
                throw new InvalidOperationException("Dosya boyutu torrent ile uyuşmuyor.");
            if (size > 0)
                anyExistingData = true;
        }

        if (!anyExistingData)
            throw new InvalidOperationException("Aktarılacak indirilmiş dosya bulunamadı.");

        // MonoTorrent CreateContainingDirectory may not create a folder for
        // single-file torrents. Never invent one from session.Name alone.
        var namedRoot = Path.GetFullPath(Path.Combine(saveDirectory, session.Name));
        var useSubfolder = session.Name.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
            files.All(path => IsInside(namedRoot, path));

        // For a single file in a differently named folder, qBittorrent cannot
        // reproduce its exact path with the default content layout.
        if (session.Files.Count == 1)
        {
            var actualFile = files.Single();
            var expectedName = session.Files[0].Name;
            if (useSubfolder)
            {
                if (!string.Equals(Path.GetFileName(actualFile), expectedName,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Dosya adı torrent ile uyuşmuyor.");
            }
            else if (!string.Equals(Path.GetDirectoryName(actualFile), saveDirectory,
                         StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Dosya farklı bir alt klasörde. qBittorrent ile otomatik eşleştirilemedi.");
            }
        }

        var contentDirectory = useSubfolder ? namedRoot : saveDirectory;
        return new SmartTorrentHandoffPlan(
            source,
            hash.ToLowerInvariant(),
            saveDirectory,
            contentDirectory,
            files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            useSubfolder ? "Subfolder" : "NoSubfolder");
    }

    private static string ReadMagnetInfoHash(string magnet)
    {
        if (!Uri.TryCreate(magnet, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Magnet bağlantısı geçersiz.");

        foreach (var pair in uri.Query.TrimStart('?').Split('&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var equal = pair.IndexOf('=');
            if (equal <= 0 || !pair[..equal].Equals("xt", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = Uri.UnescapeDataString(pair[(equal + 1)..]);
            if (!value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                continue;
            var encoded = value[9..];
            if (encoded.Length == 40 && encoded.All(Uri.IsHexDigit))
                return encoded.ToLowerInvariant();
            if (encoded.Length == 32)
                return DecodeBase32(encoded);
        }
        throw new InvalidOperationException(
            "Magnet BTIH kimliği bulunamadı. Bu tür torrent için orijinal .torrent kullanın.");
    }

    private static string DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[20];
        var accumulator = 0;
        var bits = 0;
        var offset = 0;
        foreach (var c in value.ToUpperInvariant())
        {
            var digit = alphabet.IndexOf(c);
            if (digit < 0)
                throw new InvalidOperationException("Magnet BTIH kimliği geçersiz.");
            accumulator = (accumulator << 5) | digit;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes[offset++] = (byte)((accumulator >> bits) & 0xFF);
            accumulator &= (1 << bits) - 1;
        }
        if (offset != bytes.Length)
            throw new InvalidOperationException("Magnet BTIH kimliği geçersiz.");
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsInside(string parent, string candidate) =>
        candidate.StartsWith(
            Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
