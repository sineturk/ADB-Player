using System.Buffers.Binary;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Telegram;

internal sealed class SplitArchiveVolumeMap
{
    private readonly TelegramMediaItem[] _parts;
    private readonly long[] _starts;
    private readonly Func<TelegramMediaItem, long, int, CancellationToken, Task<byte[]>> _readPartAsync;

    public SplitArchiveVolumeMap(
        IReadOnlyList<TelegramMediaItem> parts,
        Func<TelegramMediaItem, long, int, CancellationToken, Task<byte[]>> readPartAsync)
    {
        if (parts.Count == 0) throw new ArgumentException("Arşiv parçası bulunamadı.", nameof(parts));
        _parts = parts.ToArray();
        _starts = new long[_parts.Length];
        _readPartAsync = readPartAsync;
        long position = 0;
        for (var index = 0; index < _parts.Length; index++)
        {
            if (_parts[index].Size <= 0) throw new InvalidDataException($"Arşiv parçasının boyutu alınamadı: {_parts[index].FileName}");
            _starts[index] = position;
            position = checked(position + _parts[index].Size);
        }
        Length = position;
    }

    public int PartCount => _parts.Length;
    public long Length { get; }

    public IReadOnlyList<Stream> CreateArchiveStreams(
        TelegramArchiveFormat format,
        CancellationToken cancellationToken)
    {
        if (format == TelegramArchiveFormat.Split7Zip)
        {
            return
            [
                new TelegramArchiveReadStream(
                    Length,
                    (offset, count, token) => ReadExactAsync(offset, count, token),
                    cancellationToken)
            ];
        }

        return _parts
            .Select((part, index) => (Stream)new TelegramArchiveReadStream(
                part.Size,
                (offset, count, token) => ReadPartExactAsync(index, offset, count, token),
                cancellationToken))
            .ToArray();
    }

    public long ToGlobalOffset(int diskNumber, long diskOffset)
    {
        if (diskNumber < 0 || diskNumber >= _parts.Length)
            throw new InvalidDataException($"Arşiv {diskNumber + 1}. parçaya başvuruyor; bu parça bulunamadı.");
        if (diskOffset < 0 || diskOffset > _parts[diskNumber].Size)
            throw new InvalidDataException("Arşiv parça ofseti geçersiz.");
        return checked(_starts[diskNumber] + diskOffset);
    }

    public async Task<byte[]> ReadExactAsync(long offset, int count, CancellationToken cancellationToken)
    {
        if (offset < 0 || count < 0 || offset > Length - count)
            throw new EndOfStreamException("Arşivden istenen byte aralığı bulunamadı.");
        if (count == 0) return [];

        var output = new byte[count];
        var written = 0;
        var position = offset;
        while (written < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partIndex = FindPart(position);
            var partOffset = position - _starts[partIndex];
            var wanted = (int)Math.Min(count - written, _parts[partIndex].Size - partOffset);
            if (wanted <= 0) throw new EndOfStreamException("Arşiv parça sınırı okunamadı.");
            var bytes = await _readPartAsync(_parts[partIndex], partOffset, wanted, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0) throw new EndOfStreamException("Telegram arşiv parçası veri döndürmedi.");
            var copy = Math.Min(wanted, bytes.Length);
            bytes.AsSpan(0, copy).CopyTo(output.AsSpan(written, copy));
            written += copy;
            position += copy;
        }
        return output;
    }

    private async Task<byte[]> ReadPartExactAsync(
        int partIndex,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (partIndex < 0 || partIndex >= _parts.Length)
            throw new ArgumentOutOfRangeException(nameof(partIndex));
        var part = _parts[partIndex];
        if (offset < 0 || count < 0 || offset > part.Size - count)
            throw new EndOfStreamException("Arşiv parçasından istenen byte aralığı bulunamadı.");
        if (count == 0) return [];

        var output = new byte[count];
        var written = 0;
        while (written < count)
        {
            var bytes = await _readPartAsync(part, offset + written, count - written, cancellationToken)
                .ConfigureAwait(false);
            if (bytes.Length == 0) throw new EndOfStreamException("Telegram arşiv parçası veri döndürmedi.");
            var copy = Math.Min(count - written, bytes.Length);
            bytes.AsSpan(0, copy).CopyTo(output.AsSpan(written, copy));
            written += copy;
        }
        return output;
    }

    private int FindPart(long globalOffset)
    {
        var index = Array.BinarySearch(_starts, globalOffset);
        if (index >= 0) return Math.Min(index, _parts.Length - 1);
        index = ~index - 1;
        if (index < 0 || index >= _parts.Length) throw new EndOfStreamException("Arşiv parçası bulunamadı.");
        return index;
    }
}

internal static class SplitZipArchiveReader
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint CentralDirectoryHeaderSignature = 0x02014B50;
    private const uint LocalFileHeaderSignature = 0x04034B50;
    private const int MaximumCentralDirectoryBytes = 64 * 1024 * 1024;

    public static async Task<TelegramArchiveInspection> InspectAsync(
        SplitArchiveVolumeMap archive,
        CancellationToken cancellationToken)
    {
        var tailSize = (int)Math.Min(archive.Length, 128 * 1024L);
        var tailOffset = archive.Length - tailSize;
        var tail = await archive.ReadExactAsync(tailOffset, tailSize, cancellationToken).ConfigureAwait(false);
        var eocdIndex = FindEndOfCentralDirectory(tail);
        if (eocdIndex < 0 || eocdIndex + 22 > tail.Length)
            throw new InvalidDataException("Bölünmüş ZIP son dizini bulunamadı. Son .zip parçası eksik veya hatalı olabilir.");

        var eocd = tail.AsSpan(eocdIndex);
        var diskNumber = (int)ReadUInt16(eocd, 4);
        var directoryDisk = (int)ReadUInt16(eocd, 6);
        long entryCount = ReadUInt16(eocd, 10);
        long directorySize = ReadUInt32(eocd, 12);
        long directoryOffset = ReadUInt32(eocd, 16);
        var eocdGlobalOffset = tailOffset + eocdIndex;

        if (diskNumber == ushort.MaxValue || directoryDisk == ushort.MaxValue || entryCount == ushort.MaxValue ||
            directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            var zip64 = await ReadZip64DirectoryInfoAsync(archive, eocdGlobalOffset, tail, eocdIndex, cancellationToken)
                .ConfigureAwait(false);
            diskNumber = zip64.DiskNumber;
            directoryDisk = zip64.DirectoryDisk;
            entryCount = zip64.EntryCount;
            directorySize = zip64.DirectorySize;
            directoryOffset = zip64.DirectoryOffset;
        }

        if (diskNumber + 1 != archive.PartCount)
            return new TelegramArchiveInspection(
                TelegramArchiveFormat.SplitZip,
                [],
                false,
                $"Arşiv {diskNumber + 1} parça bekliyor, Telegram'da {archive.PartCount} parça bulundu.");
        if (directorySize <= 0 || directorySize > MaximumCentralDirectoryBytes)
            throw new InvalidDataException("ZIP merkezi dizini desteklenen sınırın dışında.");
        if (entryCount < 0 || entryCount > 200_000)
            throw new InvalidDataException("ZIP dosya sayısı desteklenen sınırın dışında.");

        var directoryGlobalOffset = archive.ToGlobalOffset(directoryDisk, directoryOffset);
        var directory = await archive.ReadExactAsync(directoryGlobalOffset, checked((int)directorySize), cancellationToken)
            .ConfigureAwait(false);
        var entries = new List<TelegramArchiveEntry>();
        var cursor = 0;
        for (long index = 0; index < entryCount; index++)
        {
            if (cursor + 46 > directory.Length || ReadUInt32(directory, cursor) != CentralDirectoryHeaderSignature)
                throw new InvalidDataException("ZIP merkezi dizin kaydı okunamadı.");
            var flags = ReadUInt16(directory, cursor + 8);
            var method = ReadUInt16(directory, cursor + 10);
            long compressedSize = ReadUInt32(directory, cursor + 20);
            long uncompressedSize = ReadUInt32(directory, cursor + 24);
            var nameLength = ReadUInt16(directory, cursor + 28);
            var extraLength = ReadUInt16(directory, cursor + 30);
            var commentLength = ReadUInt16(directory, cursor + 32);
            long startDisk = ReadUInt16(directory, cursor + 34);
            long localOffset = ReadUInt32(directory, cursor + 42);
            var recordLength = checked(46 + nameLength + extraLength + commentLength);
            if (cursor + recordLength > directory.Length) throw new InvalidDataException("ZIP merkezi dizin kaydı kesik.");

            var nameBytes = directory.AsSpan(cursor + 46, nameLength);
            var name = DecodeName(nameBytes, (flags & 0x0800) != 0);
            var extra = directory.AsSpan(cursor + 46 + nameLength, extraLength);
            ReadZip64Extra(extra, ref uncompressedSize, ref compressedSize, ref localOffset, ref startDisk);
            if (startDisk > int.MaxValue) throw new InvalidDataException("ZIP disk numarası desteklenmiyor.");

            var localGlobalOffset = archive.ToGlobalOffset((int)startDisk, localOffset);
            var localHeader = await archive.ReadExactAsync(localGlobalOffset, 30, cancellationToken).ConfigureAwait(false);
            if (ReadUInt32(localHeader, 0) != LocalFileHeaderSignature)
                throw new InvalidDataException($"ZIP yerel dosya başlığı bulunamadı: {name}");
            var localNameLength = ReadUInt16(localHeader, 26);
            var localExtraLength = ReadUInt16(localHeader, 28);
            var dataOffset = checked(localGlobalOffset + 30L + localNameLength + localExtraLength);
            var playable = IsPlayable(name) && !name.EndsWith("/", StringComparison.Ordinal);
            entries.Add(new TelegramArchiveEntry(
                name,
                uncompressedSize,
                compressedSize,
                method,
                dataOffset,
                (flags & 0x0001) != 0,
                playable));
            cursor += recordLength;
        }

        var playableEntries = entries
            .Where(entry => entry.IsPlayable)
            .OrderByDescending(entry => entry.UncompressedSize)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var status = playableEntries.Length == 0
            ? "Arşivde oynatılabilir video veya ses bulunamadı."
            : playableEntries.Any(entry => entry.CanInstantStream)
                ? $"{playableEntries.Length} oynatılabilir medya bulundu."
                : "Arşivdeki medya sıkıştırılmış veya şifreli; anlık seek kullanılamıyor.";
        return new TelegramArchiveInspection(TelegramArchiveFormat.SplitZip, playableEntries, true, status);
    }

    private static async Task<ZipDirectoryInfo> ReadZip64DirectoryInfoAsync(
        SplitArchiveVolumeMap archive,
        long eocdGlobalOffset,
        byte[] tail,
        int eocdIndex,
        CancellationToken cancellationToken)
    {
        var locatorIndex = eocdIndex - 20;
        if (locatorIndex < 0 || ReadUInt32(tail, locatorIndex) != Zip64LocatorSignature)
            locatorIndex = FindLastSignature(tail.AsSpan(0, eocdIndex), Zip64LocatorSignature);
        if (locatorIndex < 0 || locatorIndex + 20 > tail.Length)
            throw new InvalidDataException("ZIP64 son dizin bulucusu bulunamadı.");

        var locator = tail.AsSpan(locatorIndex, 20);
        var zip64Disk = checked((int)ReadUInt32(locator, 4));
        var zip64Offset = checked((long)ReadUInt64(locator, 8));
        var zip64GlobalOffset = archive.ToGlobalOffset(zip64Disk, zip64Offset);
        if (zip64GlobalOffset >= eocdGlobalOffset) throw new InvalidDataException("ZIP64 son dizin konumu geçersiz.");
        var header = await archive.ReadExactAsync(zip64GlobalOffset, 56, cancellationToken).ConfigureAwait(false);
        if (ReadUInt32(header, 0) != Zip64EndOfCentralDirectorySignature)
            throw new InvalidDataException("ZIP64 son dizin kaydı bulunamadı.");
        return new ZipDirectoryInfo(
            checked((int)ReadUInt32(header, 16)),
            checked((int)ReadUInt32(header, 20)),
            checked((long)ReadUInt64(header, 32)),
            checked((long)ReadUInt64(header, 40)),
            checked((long)ReadUInt64(header, 48)));
    }

    private static void ReadZip64Extra(
        ReadOnlySpan<byte> extra,
        ref long uncompressedSize,
        ref long compressedSize,
        ref long localOffset,
        ref long startDisk)
    {
        var cursor = 0;
        while (cursor + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(cursor, 2));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(cursor + 2, 2));
            cursor += 4;
            if (cursor + size > extra.Length) throw new InvalidDataException("ZIP ek alanı kesik.");
            if (id == 0x0001)
            {
                var data = extra.Slice(cursor, size);
                var position = 0;
                if (uncompressedSize == uint.MaxValue) uncompressedSize = ReadZip64Int64(data, ref position);
                if (compressedSize == uint.MaxValue) compressedSize = ReadZip64Int64(data, ref position);
                if (localOffset == uint.MaxValue) localOffset = ReadZip64Int64(data, ref position);
                if (startDisk == ushort.MaxValue)
                {
                    if (position + 4 > data.Length) throw new InvalidDataException("ZIP64 disk numarası eksik.");
                    startDisk = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position, 4));
                }
                return;
            }
            cursor += size;
        }
    }

    private static long ReadZip64Int64(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 8 > data.Length) throw new InvalidDataException("ZIP64 boyut alanı eksik.");
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(position, 8));
        position += 8;
        return checked((long)value);
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes, bool utf8) =>
        (utf8 ? System.Text.Encoding.UTF8 : System.Text.Encoding.Latin1).GetString(bytes);

    private static bool IsPlayable(string name)
    {
        var extension = Path.GetExtension(name);
        return new[]
        {
            ".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".mpg", ".mpeg",
            ".mka", ".mp3", ".flac", ".aac", ".ac3", ".eac3", ".dts", ".opus", ".ogg"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static int FindLastSignature(ReadOnlySpan<byte> bytes, uint signature)
    {
        for (var index = bytes.Length - 4; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index, 4)) == signature) return index;
        }
        return -1;
    }

    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> bytes)
    {
        for (var index = bytes.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index, 4)) != EndOfCentralDirectorySignature)
                continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index + 20, 2));
            if (index + 22 + commentLength == bytes.Length) return index;
        }
        return -1;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length) throw new InvalidDataException("ZIP alanı kesik.");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length) throw new InvalidDataException("ZIP alanı kesik.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 8 > bytes.Length) throw new InvalidDataException("ZIP64 alanı kesik.");
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
    }

    private sealed record ZipDirectoryInfo(
        int DiskNumber,
        int DirectoryDisk,
        long EntryCount,
        long DirectorySize,
        long DirectoryOffset);
}
