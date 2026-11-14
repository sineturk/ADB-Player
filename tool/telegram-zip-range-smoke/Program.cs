using System.IO;
using System.IO.Compression;
using System.Reflection;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Telegram;

var folder = Path.Combine(Path.GetTempPath(), "adb-telegram-zip-range-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var archivePath = Path.Combine(folder, "test-telegram.zip");
    var audio = new byte[12 * 1024 * 1024];
    Random.Shared.NextBytes(audio);
    var subtitle = System.Text.Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nMerhaba!\n");
    using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
        Write(zip, "season1/Turkce.eac3", audio, CompressionLevel.NoCompression);
        Write(zip, "season1/Normal.srt", subtitle, CompressionLevel.Optimal);
    }

    var bytes = await File.ReadAllBytesAsync(archivePath);
    long bytesRead = 0;
    var media = new TelegramMediaItem(
        12, 34, 56, "Chat", "test-telegram.zip", "", "application/zip",
        bytes.LongLength, 0, DateTimeOffset.UtcNow, TelegramMediaKind.ArchivePart);
    Func<TelegramMediaItem, long, int, CancellationToken, Task<byte[]>> rangedRead =
        (part, offset, count, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (part.FileId != media.FileId || offset < 0 || offset > bytes.LongLength - count)
                throw new InvalidDataException("Outside mock Telegram range");
            bytesRead += count;
            var buffer = new byte[count];
            Array.Copy(bytes, checked((int)offset), buffer, 0, count);
            return Task.FromResult(buffer);
        };

    var asm = typeof(TdJsonTelegramService).Assembly;
    var mapType = asm.GetType("AltyaziDB.Player.Telegram.SplitArchiveVolumeMap", throwOnError: true)!;
    var ctor = mapType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
    var map = ctor.Invoke([new TelegramMediaItem[] { media }, rangedRead]);
    var reader = asm.GetType("AltyaziDB.Player.Telegram.SplitZipArchiveReader", throwOnError: true)!;
    var inspect = reader.GetMethod("InspectAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    var inspection = await (Task<TelegramArchiveInspection>)inspect.Invoke(null, [map, CancellationToken.None])!;

    Assert(inspection.IsComplete, "Single-volume remote ZIP central directory inspected");
    Assert(inspection.Entries.Count == 2, "Audio and subtitle entries visible");
    var audioEntry = inspection.Entries.Single(x => x.Name.EndsWith(".eac3", StringComparison.OrdinalIgnoreCase));
    var subtitleEntry = inspection.Entries.Single(x => x.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
    Assert(audioEntry.IsPlayable && subtitleEntry.IsPlayable, "Both ZIP tracks selectable");
    Assert(audioEntry.UsesDirectRange, "Stored audio can stream directly by range");
    Assert(subtitleEntry.CompressionMethod == 8, "Compressed subtitle recognized");
    Assert(bytesRead < bytes.LongLength / 5, "ZIP index inspection did not download entire archive");

    var read = mapType.GetMethod("ReadExactAsync", BindingFlags.Public | BindingFlags.Instance)!;
    var firstAudio = await (Task<byte[]>)read.Invoke(map,
        [audioEntry.DataOffset, 128, CancellationToken.None])!;
    Assert(firstAudio.AsSpan().SequenceEqual(audio.AsSpan(0, 128)),
        "Audio bytes served directly from ZIP entry offset");

    var compressed = await (Task<byte[]>)read.Invoke(map,
        [subtitleEntry.DataOffset, checked((int)subtitleEntry.CompressedSize), CancellationToken.None])!;
    using var source = new MemoryStream(compressed);
    using var decompressed = new DeflateStream(source, CompressionMode.Decompress);
    using var target = new MemoryStream();
    await decompressed.CopyToAsync(target);
    Assert(target.ToArray().SequenceEqual(subtitle),
        "Only the compressed subtitle entry decoded without downloading full ZIP");
    Assert(bytesRead < bytes.LongLength / 5,
        "Audio preview and subtitle extraction still read only a fraction of archive");
    // A stored ZIP cut into NAME.001/.002 remains a *single ZIP disk*
    // even when TDLib stores its bytes in two independent remote files.
    var splitAt = audioEntry.DataOffset + 256 * 1024;
    Assert(splitAt > 0 && splitAt < bytes.LongLength, "test ZIP has a valid split boundary");
    var partA = media with { FileId = 1001, FileName = "Heart_of_the_Beast_TURG.001", Size = splitAt };
    var partB = media with { FileId = 1002, FileName = "Heart_of_the_Beast_TURG.002", Size = bytes.LongLength - splitAt };
    var grouped = TelegramArchiveSetDetector.Group([partA, partB]);
    Assert(grouped.Count == 1 && grouped[0].ArchiveFormat == TelegramArchiveFormat.GenericSplit &&
           grouped[0].ArchiveSetComplete, "bare .001/.002 is one complete set");
    long splitReadBytes = 0;
    Func<TelegramMediaItem, long, int, CancellationToken, Task<byte[]>> splitRead =
        (part, offset, count, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var start = part.FileId == partA.FileId ? 0L :
                part.FileId == partB.FileId ? splitAt :
                throw new InvalidDataException("Unknown Telegram volume");
            Assert(offset >= 0 && count >= 0 && offset <= part.Size - count,
                "individual Telegram part range is bounded");
            splitReadBytes += count;
            var result = new byte[count];
            Array.Copy(bytes, checked((int)(start + offset)), result, 0, count);
            return Task.FromResult(result);
        };
    var splitMap = ctor.Invoke([grouped[0].ArchiveParts!, splitRead]);
    var inspectConcat = reader.GetMethod("InspectConcatenatedAsync",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    var splitInspection = await (Task<TelegramArchiveInspection>)inspectConcat.Invoke(
        null, [splitMap, CancellationToken.None])!;
    Assert(splitInspection.IsComplete && splitInspection.Entries.Count == 2,
        "stored/deflate ZIP index is parsed across bare .001/.002");
    var stored = splitInspection.Entries.Single(x => x.Name.EndsWith(".eac3", StringComparison.OrdinalIgnoreCase));
    Assert(stored.UsesDirectRange && stored.DataOffset == audioEntry.DataOffset,
        "ZIP Store entry preserves direct Range offsets after physical split");
    var across = await (Task<byte[]>)read.Invoke(splitMap,
        [splitAt - 8, 16, CancellationToken.None])!;
    Assert(across.AsSpan().SequenceEqual(bytes.AsSpan(checked((int)(splitAt - 8)), 16)),
        "stored ZIP Range seamlessly crosses the .001/.002 file boundary");
    Assert(splitReadBytes < bytes.LongLength / 5,
        "concatenated stored ZIP index and preview never download every part");
    Console.WriteLine("[PASS] Telegram generic .001/.002: ZIP Store, central directory, cross-volume Range");

    Console.WriteLine("[PASS] Telegram single ZIP: remote index, audio Range, subtitle deflate, no full download");
}
finally
{
    try { Directory.Delete(folder, recursive: true); } catch { }
}

static void Write(ZipArchive zip, string name, byte[] bytes, CompressionLevel compression)
{
    var entry = zip.CreateEntry(name, compression);
    using var output = entry.Open();
    output.Write(bytes);
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("Telegram ZIP Range smoke failed: " + message);
}
