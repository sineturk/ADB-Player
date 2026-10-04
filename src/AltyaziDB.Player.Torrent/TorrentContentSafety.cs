using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Torrent;

internal static class TorrentContentSafety
{
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".msp",
        ".lnk", ".pif", ".cpl", ".hta", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh"
    };

    /// <summary>
    /// Torrentte oynatılabilir video bulunmasını zorunlu tutar. Çalıştırılabilir
    /// yan dosyalar yüzünden bütün torrent reddedilmez; streaming servisi yalnız
    /// seçilen video dosyasına High, diğer bütün dosyalara DoNotDownload verir.
    /// </summary>
    public static void ValidatePlayableVideo(IReadOnlyList<TorrentFileItem> files)
    {
        if (!files.Any(file => file.IsVideo))
        {
            throw new InvalidDataException(
                "Magnet bağlantısı açıldı ancak torrent içinde desteklenen bir video dosyası bulunamadı.");
        }

        // Defense in depth: gelecekte çağrı sırası değişirse riskli bir dosyanın
        // yanlışlıkla seçili/indiriliyor durumda bırakılmasına izin verme.
        var selectedDangerous = files.FirstOrDefault(file =>
            file.IsSelected && IsBlockedFileName(file.Name));
        if (selectedDangerous is not null)
        {
            throw new InvalidDataException(
                $"Torrent güvenlik nedeniyle reddedildi. Riskli dosya indirme için seçilmiş: {selectedDangerous.Name}");
        }
    }

    public static IReadOnlyList<TorrentFileItem> GetBlockedFiles(IReadOnlyList<TorrentFileItem> files) =>
        files.Where(file => IsBlockedFileName(file.Name)).ToArray();

    public static bool IsBlockedFileName(string path) =>
        DangerousExtensions.Contains(Path.GetExtension(path));
}
