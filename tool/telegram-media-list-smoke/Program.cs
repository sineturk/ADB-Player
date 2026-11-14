using AltyaziDB.Player.Core.Models;

static void Check(bool valid, string label)
{
    if (!valid) throw new InvalidDataException("P4.15.2/P3 media-list smoke: " + label);
    Console.WriteLine("[PASS] " + label);
}

static TelegramMediaItem Media(
    string file, TelegramMediaKind kind = TelegramMediaKind.Video,
    int messageId = 1, int duration = 7200)
    => new(1, messageId, messageId + 10, "AMZN",
        file, "MEDIA INFO: NOT THE REAL FILENAME", "application/octet-stream",
        4L * 1024 * 1024 * 1024,
        duration, new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero), kind);

var video = Media("Episode.S01E01.mkv");
Check(video.MediaCategory == "video" && video.DisplayTitle == "Episode.S01E01.mkv",
    "video uses actual TDLib filename, never MEDIA INFO caption");
Check(video.MediaSizeLabel.Contains("GB") && video.HasKnownDuration &&
      !string.IsNullOrWhiteSpace(video.DurationLabel),
    "size and duration use source metadata");
Check(Media("clip.mp4", duration: 0).DurationLabel.Length == 0,
    "unknown video duration is not invented");
Check(Media("audio.ac3", TelegramMediaKind.Audio).MediaCategory == "audio", "audio classified");
Check(Media("sub.srt", TelegramMediaKind.Subtitle).MediaCategory == "subtitle", "subtitle classified");
Check(Media("notes.nfo", TelegramMediaKind.Document).MediaCategory == "document", "other document classified");
Check(Media("film.zip", TelegramMediaKind.Document).MediaCategory == "archive",
    "standalone ZIP attachment is shown as archive");

var first = Media("MyFilm.mkv.001", TelegramMediaKind.ArchivePart, messageId: 101);
var second = Media("MyFilm.mkv.002", TelegramMediaKind.ArchivePart, messageId: 102);
var archive = first with
{
    Kind = TelegramMediaKind.SplitArchive,
    ArchiveFormat = TelegramArchiveFormat.Split7Zip,
    ArchiveParts = [first, second],
    ArchiveSetComplete = true
};
Check(archive.MediaCategory == "archive" && archive.HasArchivePartCount &&
      archive.ArchivePartCount == 2 && archive.PartStatusCode == "ready",
    "verified multipart archive displays correct part count and ready status");
Check(first.PartStatusCode == "standalone" && first.ArchivePartCount == 1,
    "isolated part is never presented as ready");
Check((archive with { ArchiveSetComplete = false }).PartStatusCode == "missing",
    "incomplete archive is blocked and labeled missing");
Check((archive with { ArchiveSetAmbiguous = true }).PartStatusCode == "ambiguous",
    "duplicate/ambiguous archive is labeled review-needed");
Check((archive with { ArchiveFormat = TelegramArchiveFormat.RawSplit }).PartStatusCode == "unverified",
    "raw split with unknown last-part marker is NEVER labeled verified");
Check((archive with { ArchiveFormat = TelegramArchiveFormat.GenericSplit }).PartStatusCode == "unverified",
    "generic split with unknown last part is NEVER labeled verified");
Check(video.PartStatusCode == "none" && !video.HasArchivePartCount,
    "regular videos do not show archive-part badges");

Console.WriteLine("[PASS] P4.15.2/P3 offline media display states and archive truthfulness");
