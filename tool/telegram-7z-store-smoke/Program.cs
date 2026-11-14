using System.Diagnostics;
using System.Reflection;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Telegram;

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidDataException("P4.13j 7z Store regression: " + message);
}

var sevenZip = new[]
{
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
}.FirstOrDefault(File.Exists);
Ensure(sevenZip is not null, "Windows CI needs 7-Zip to create real Store and LZMA fixtures.");

var root = Path.Combine(Path.GetTempPath(), "adb-p413j-7z-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var original = new byte[8 * 1024 * 1024];
    Random.Shared.NextBytes(original);
    // MKV signature is valid enough for byte-level testing; no media decoding is needed.
    original[0] = 0x1A; original[1] = 0x45; original[2] = 0xDF; original[3] = 0xA3;
    await File.WriteAllBytesAsync(Path.Combine(root, "movie.mkv"), original);
    await PackAsync("movie-store.7z", "movie.mkv", "-mx=0");
    var physical = await File.ReadAllBytesAsync(Path.Combine(root, "movie-store.7z"));
    var splitAt = physical.LongLength / 2;
    long fetched = 0;
    var part1 = new TelegramMediaItem(1, 101, 11, "test", "movie-store.7z.001", "",
        "application/octet-stream", splitAt, 0, DateTimeOffset.UtcNow, TelegramMediaKind.ArchivePart);
    var part2 = part1 with { MessageId = 102, FileId = 12,
        FileName = "movie-store.7z.002", Size = physical.LongLength - splitAt };
    var grouped = TelegramArchiveSetDetector.Group([part1, part2]).Single();
    Ensure(grouped.ArchiveSetComplete && grouped.ArchiveFormat == TelegramArchiveFormat.Split7Zip,
        "stored 7z volumes form one complete card");
    Func<TelegramMediaItem, long, int, CancellationToken, Task<byte[]>> fetch =
        (part, offset, count, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var start = part.FileId == 11 ? 0L : part.FileId == 12 ? splitAt :
                throw new InvalidDataException("unknown volume");
            Ensure(offset >= 0 && count >= 0 && offset <= part.Size - count,
                "requested bytes must belong to a single physical Telegram part");
            fetched += count;
            var result = new byte[count];
            Array.Copy(physical, checked((int)(start + offset)), result, 0, count);
            return Task.FromResult(result);
        };
    var asm = typeof(TdJsonTelegramService).Assembly;
    var mapType = asm.GetType("AltyaziDB.Player.Telegram.SplitArchiveVolumeMap", true)!;
    var ctor = mapType.GetConstructors(BindingFlags.Instance | BindingFlags.Public |
        BindingFlags.NonPublic).Single();
    var map = ctor.Invoke([grouped.ArchiveParts!, fetch]);
    var inspectorType = asm.GetType("AltyaziDB.Player.Telegram.TelegramArchiveStreamingEngine", true)!;
    var inspect = inspectorType.GetMethod("InspectAsync", BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic)!;
    var inspection = await (Task<TelegramArchiveInspection>)inspect.Invoke(
        null, [map, TelegramArchiveFormat.Split7Zip, CancellationToken.None])!;
    Ensure(inspection.IsComplete && inspection.Entries.Count == 1, "single stored video found");
    var entry = inspection.Entries.Single();
    Ensure(entry.UsesDirectRange, "7z Store must advertise direct seekable Range");
    var read = mapType.GetMethod("ReadExactAsync", BindingFlags.Public | BindingFlags.Instance)!;
    foreach (var position in new long[] {0, 2048, original.LongLength / 2, original.LongLength - 2048})
    {
        var data = await (Task<byte[]>)read.Invoke(map,
            [entry.DataOffset + position, 2048, CancellationToken.None])!;
        Ensure(data.AsSpan().SequenceEqual(original.AsSpan(checked((int)position), 2048)),
            "arbitrary Store seek returns exact video bytes");
    }
    Ensure(fetched < physical.LongLength / 8,
        "7z Store inspection and seeks must not fetch whole source");
    // The affected Telegram uploader names volumes MOVIE.001/.002,
    // although their signature is 7z. Both naming schemes use the same
    // verified byte-offset path once the real archive has been identified.
    var generic = TelegramArchiveSetDetector.Group([
        part1 with { FileName = "movie-store.001" },
        part2 with { FileName = "movie-store.002" }
    ]).Single();
    Ensure(generic.ArchiveFormat == TelegramArchiveFormat.GenericSplit,
        "extensionless numeric Telegram parts stay generic until probed");
    var genericMap = ctor.Invoke([generic.ArchiveParts!, fetch]);
    var genericInspection = await (Task<TelegramArchiveInspection>)inspect.Invoke(
        null, [genericMap, TelegramArchiveFormat.GenericSplit, CancellationToken.None])!;
    Ensure(genericInspection.Entries.Single().UsesDirectRange,
        "7z Store in bare .001/.002 must also use instant Range instead of cache extraction");
    Console.WriteLine("[PASS] 7z Store inside extensionless .001/.002: instant seek");

    Console.WriteLine("[PASS] 7z Store .001/.002: direct Range, 4 seek locations, no full extraction");

    // Compressed 7z must NOT be mislabeled as directly seekable.
    var compressed = Enumerable.Repeat((byte)0x5A, 1024 * 1024).ToArray();
    await File.WriteAllBytesAsync(Path.Combine(root, "movie-compressed.mkv"), compressed);
    await PackAsync("movie-compressed.7z", "movie-compressed.mkv", "-mx=9");
    var compressedBytes = await File.ReadAllBytesAsync(Path.Combine(root, "movie-compressed.7z"));
    var single = part1 with { FileId = 13, FileName = "movie-compressed.7z.001", Size = compressedBytes.LongLength };
    var cm = ctor.Invoke([new[] { single },
        (Func<TelegramMediaItem,long,int,CancellationToken,Task<byte[]>>)((_, offset, count, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var result = new byte[count];
            Array.Copy(compressedBytes, checked((int)offset), result, 0, count);
            return Task.FromResult(result);
        })]);
    var compressedInspection = await (Task<TelegramArchiveInspection>)inspect.Invoke(
        null, [cm, TelegramArchiveFormat.Split7Zip, CancellationToken.None])!;
    Ensure(compressedInspection.Entries.Count == 1 &&
           !compressedInspection.Entries[0].UsesDirectRange,
           "LZMA-compressed 7z retains buffered fallback and is never mistaken for Store");
    Console.WriteLine("[PASS] compressed 7z remains guarded fallback");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

async Task PackAsync(string archiveName, string fileName, string compression)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(sevenZip!)
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }};
    process.StartInfo.ArgumentList.Add("a");
    process.StartInfo.ArgumentList.Add("-t7z");
    process.StartInfo.ArgumentList.Add(compression);
    process.StartInfo.ArgumentList.Add("-mhe=off");
    process.StartInfo.ArgumentList.Add(archiveName);
    process.StartInfo.ArgumentList.Add(fileName);
    Ensure(process.Start(), "7z fixture process starts");
    var output = process.StandardOutput.ReadToEndAsync();
    var err = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var message = await output + await err;
    Ensure(process.ExitCode == 0, "7z fixture created: " + message);
}
