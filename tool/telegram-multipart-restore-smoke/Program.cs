using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Telegram;
using System.Reflection;

static void Check(bool value, string title)
{
    if (!value) throw new Exception("Telegram multipart rollback regression: " + title);
}
TelegramMediaItem Media(int i, string name, string caption, long length = 1000, long chat = 25)
    => new(chat, 50000 + i, i, "AMZN", name, caption,
      "application/octet-stream", length, 0, DateTimeOffset.UtcNow,
      TelegramArchiveSetDetector.IsArchivePartFileName(name)
        ? TelegramMediaKind.ArchivePart : TelegramMediaKind.Document);

var items = new[]
{
    Media(1, "Siccin.9.2026.1080p.AMZN.WEB-DL.DDP5.1.H264-TURG.mkv.7z.001",
        "MEDYA BILGISI: Siccin 9, 7z Part 1/2", 3890000000),
    Media(2, "Siccin.9.2026.1080p.AMZN.WEB-DL.DDP5.1.H264-TURG.mkv.7z.002",
        "7z Part 2/2", 2220000000),
    Media(3, "Weapons.2025.1080p.WEBRip.DDP5.1.mkv.z01", "PARCA 1", 2500000000),
    Media(4, "Weapons.2025.1080p.WEBRip.DDP5.1.mkv.z02", "PARCA 2", 2500000000),
    Media(5, "Weapons.2025.1080p.WEBRip.DDP5.1.mkv.zip", "ZIP final", 120000000),
    Media(6, "Another.mkv", "MEDYA BILGISI", 90000000),
    Media(7, "Other.7z.001", "Part 1", chat:123),
    Media(8, "Other.7z.002", "Part 2", chat:123),
};
var cards = TelegramArchiveSetDetector.Group(items);
Check(cards.Count == 4, "Eight documents become four cards");
var seven = cards.Single(x => x.FileName.StartsWith("Siccin."));
Check(seven.Kind == TelegramMediaKind.SplitArchive, "7zip is one grouped media card");
Check(seven.ArchiveFormat == TelegramArchiveFormat.Split7Zip, "7zip type stays Split7Zip");
Check(seven.ArchiveParts!.Count == 2, "Two physical parts remain attached");
Check(seven.ArchiveSetComplete && !seven.ArchiveSetAmbiguous, "7zip playback ready");
Check(seven.Size == 6110000000L, "7zip 6.11 GB physical size retained");
Check(seven.ArchiveParts![0].FileName.EndsWith(".001") &&
      seven.ArchiveParts![1].FileName.EndsWith(".002"), "7zip part order stable");
var zip = cards.Single(x => x.FileName.StartsWith("Weapons."));
Check(zip.Kind == TelegramMediaKind.SplitArchive, "WinRAR-authored split ZIP becomes one grouped card");
Check(zip.ArchiveFormat == TelegramArchiveFormat.SplitZip && zip.ArchiveSetComplete,
      "z01 z02 zip recognized as complete");
Check(zip.ArchiveParts!.Count == 3 && zip.ArchiveParts[2].FileName.EndsWith(".zip"),
      "final .zip appended after z01 and z02");
Check(cards.Count(x => x.IsArchiveSet) == 3, "Other chat does not merge into active topic");

var captionDifferences = TelegramArchiveSetDetector.Group([
    Media(11, "Series.S01E01.mkv.7z.001", "Season S01"),
    Media(12, "Series.S01E01.mkv.7z.002", "7z part 2/2")
]);
Check(captionDifferences.Count == 1 && captionDifferences[0].IsArchiveSet,
      "Caption mismatch cannot split one archive");
Check(TelegramArchiveSetDetector.Group([
    Media(13, "Unfinished.mkv.7z.001", "1")
]).Single().IsArchiveSet, "A single .7z.001 remains an archive card (completeness must be checked on opening)");
Check(TelegramArchiveSetDetector.Group([
    Media(14, "Unfinished.mkv.z01", "1")
]).Single().NeedsArchiveRepair, "Missing final ZIP is explicitly incomplete");
Check(TelegramArchiveSetDetector.Group([
    Media(15, "Unfinished.mkv.7z.001", "first"),
    Media(16, "Unfinished.mkv.7z.001", "duplicate")
]).Single().ArchiveSetAmbiguous, "Duplicate part numbers are not auto-played");
Check(TelegramArchiveSetDetector.Group([
    Media(17, "Unfinished.mkv.z01", "z01"),
    Media(18, "Unfinished.mkv.zip", "zip")
]).Single().ArchiveSetComplete, "z01 + zip forms a complete two-volume ZIP");

Check(TelegramArchiveSetDetector.IsArchivePartFileName("Film.7z.001\u200b"),
      "zero-width char after .001 ignored");
Check(TelegramArchiveSetDetector.IsArchivePartFileName(" Film.7z.002 "),
      "trailing whitespace after .002 ignored");
Check(TelegramArchiveSetDetector.IsArchivePartFileName("Film.z01\u00a0"),
      "nonbreaking space in copied filename tolerated");
var unicodeCards = TelegramArchiveSetDetector.Group([
    Media(21,"FILM.7z.001\u200b","first"),
    Media(22,"FILM.7z.002","second")
]);
Check(unicodeCards.Count == 1 && unicodeCards.Single().ArchiveSetComplete,
      "Same 7z group matches despite invisible Unicode suffix");

Console.WriteLine("[PASS] 7-Zip .001/.002, ZIP z01/z02/zip, multi-GB sizes, exact grouping, invisible Unicode filename normalization");
