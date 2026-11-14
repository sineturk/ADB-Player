using System.IO;
using System.IO.Compression;
using System.Text;
using AltyaziDB.Player.App.Services;

var root = Path.Combine(Path.GetTempPath(), "adb-p413-zip-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var zipPath = Path.Combine(root, "sample.zip");
    var audio = new byte[] { 0x0B, 0x77, 0x00, 0x01, 0x02 };
    var subtitle = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nMerhaba\n");
    var vtt = Encoding.UTF8.GetBytes("WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nTest\n");
    using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        WriteEntry(archive, "season1/Turkce.eac3", audio);
        WriteEntry(archive, "season1/Normal.srt", subtitle);
        WriteEntry(archive, "../outside.vtt", vtt);
        WriteEntry(archive, "ignored.exe", new byte[] { 1, 2, 3 });
    }

    var all = await ArchiveMediaAttachmentHelper.SelectAndExtractAsync(
        zipPath, root, new SelectAllDialog());
    Expect(all.Count == 3, "Expected one audio and two subtitle entries");
    Expect(all.Count(item => item.Kind == ArchiveAttachmentKind.Audio) == 1, "Audio kind retained");
    Expect(all.Count(item => item.Kind == ArchiveAttachmentKind.Subtitle) == 2, "Subtitle kinds retained");
    foreach (var item in all)
    {
        Expect(File.Exists(item.FilePath), "Extracted file exists");
        Expect(Path.GetFullPath(item.FilePath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "ZIP path cannot escape output root");
    }
    Expect(all.Any(item => Path.GetExtension(item.FilePath) == ".eac3" &&
        File.ReadAllBytes(item.FilePath).SequenceEqual(audio)), "Audio bytes preserved");
    Expect(all.Any(item => Path.GetExtension(item.FilePath) == ".srt" &&
        File.ReadAllBytes(item.FilePath).SequenceEqual(subtitle)), "Subtitle bytes preserved");
    Expect(!File.Exists(Path.Combine(root, "outside.vtt")), "Traversal cannot create a sibling file");
    Expect(!Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories).Any(), "Executable entries ignored");
    Expect(File.Exists(zipPath), "Original ZIP preserved");

    var onlyAudio = await ArchiveMediaAttachmentHelper.SelectAndExtractAsync(
        zipPath, root, new SelectAllDialog(), audioOnly: true);
    Expect(onlyAudio.Count == 1 && onlyAudio[0].Kind == ArchiveAttachmentKind.Audio,
        "Audio mode never imports subtitles");
    var onlySubtitles = await ArchiveMediaAttachmentHelper.SelectAndExtractAsync(
        zipPath, root, new SelectAllDialog(), subtitleOnly: true);
    Expect(onlySubtitles.Count == 2 &&
        onlySubtitles.All(item => item.Kind == ArchiveAttachmentKind.Subtitle),
        "Subtitle mode never imports audio");

    var cancelled = await ArchiveMediaAttachmentHelper.SelectAndExtractAsync(
        zipPath, root, new SelectAllDialog(cancel: true));
    Expect(cancelled.Count == 0, "User cancellation does not extract any attachment");

    var invalidPath = Path.Combine(root, "invalid.zip");
    await File.WriteAllTextAsync(invalidPath, "not an archive");
    var invalidRejected = false;
    try { _ = await ArchiveMediaAttachmentHelper.SelectAndExtractAsync(invalidPath, root, new SelectAllDialog()); }
    catch (InvalidDataException) { invalidRejected = true; }
    Expect(invalidRejected, "Corrupt ZIP must be rejected");

    Console.WriteLine("[PASS] ZIP runtime smoke: audio, subtitles, bounded selection, safe names, corrupt archives");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
{
    var entry = zip.CreateEntry(name);
    using var stream = entry.Open();
    stream.Write(bytes);
}
static void Expect(bool ok, string label)
{
    if (!ok) throw new Exception("ZIP smoke failure: " + label);
}
sealed class SelectAllDialog(bool cancel = false) : IUserDialogService
{
    public IReadOnlyList<string> PickVideos(string? initialDirectory = null) => [];
    public string? PickFolder(string? initialDirectory = null) => null;
    public string? PickSubtitle(string? initialDirectory = null) => null;
    public string? PickAudio(string? initialDirectory = null) => null;
    public string? PickTorrentFile(string? initialDirectory = null) => null;
    public string? PromptForUrl() => null;
    public IReadOnlyList<int> SelectItems(string title, IReadOnlyList<string> items,
        IReadOnlyList<int>? initiallySelected = null, bool allowMultiple = false) =>
        cancel ? [] : Enumerable.Range(0, items.Count).ToArray();
    public void ShowError(string message, string title = "ADB Player") =>
        throw new NotSupportedException("Headless ZIP smoke must not show UI");
    public bool Confirm(string message, string title = "ADB Player") => true;
}