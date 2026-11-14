using System.Reflection;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

namespace AltyaziDB.Player.Telegram;

/// <summary>
/// Narrow 7z Store/Copy fast path. SharpCompress 0.50.1 exposes decoded file
/// bytes through a forward-only stream even when the underlying 7z pack data
/// is byte-for-byte identical to the movie. Locate that stored pack offset
/// and verify the first bytes against the normal decoder before advertising
/// seekable Range. Never assume that all uncompressed 7z files are contiguous.
/// A library layout change or an ambiguous solid/multi-file archive falls
/// back to the existing decoder instead of returning corrupt bytes.
/// </summary>
internal static class TelegramSevenZipStoredRangeResolver
{
    public static async Task<long?> TryResolveAsync(
        IArchive archive,
        IArchiveEntry entry,
        SplitArchiveVolumeMap map,
        CancellationToken cancellationToken)
    {
        if (archive is not SevenZipArchive ||
            entry is not SevenZipArchiveEntry sevenEntry ||
            entry.CompressionType != CompressionType.None ||
            entry.IsEncrypted || entry.Size <= 0)
            return null;

        // Only a single non-empty stream can be mapped without also
        // interpreting 7z folder/substream packing and solid-file offsets.
        if (archive.Entries.Count(item => !item.IsDirectory && item.Size > 0) != 1)
            return null;

        try
        {
            // Access *only* the pack offset of the pinned SharpCompress
            // version; validate it against independently decoded bytes.
            var database = typeof(SevenZipArchive)
                .GetField("_database", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(archive);
            var part = sevenEntry.GetType().BaseType?
                .GetProperty("FilePart", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(sevenEntry);
            var folder = part?.GetType()
                .GetProperty("Folder", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(part);
            var getOffset = database?.GetType().GetMethod(
                "GetFolderStreamPos", BindingFlags.Instance | BindingFlags.NonPublic);
            if (database is null || folder is null || getOffset is null)
                return null;

            if (getOffset.Invoke(database, [folder, 0]) is not long dataOffset ||
                dataOffset < 0 || dataOffset > map.Length - entry.Size)
                return null;

            var count = (int)Math.Min(128L, entry.Size);
            var decoded = new byte[count];
            using (var decodedStream = sevenEntry.OpenEntryStream())
            {
                var read = 0;
                while (read < decoded.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var got = decodedStream.Read(decoded, read, decoded.Length - read);
                    if (got <= 0) return null;
                    read += got;
                }
            }

            var direct = await map.ReadExactAsync(dataOffset, count, cancellationToken)
                .ConfigureAwait(false);
            return decoded.AsSpan().SequenceEqual(direct) ? dataOffset : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Never let a reflection change or unsupported archive disable
            // standard 7z playback. The slow path remains an explicit fallback.
            return null;
        }
    }
}
