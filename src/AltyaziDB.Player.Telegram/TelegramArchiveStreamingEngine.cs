using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.IO.Compression;
using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Telegram;

internal static class TelegramArchiveStreamingEngine
{
    public static Task<TelegramArchiveInspection> InspectAsync(
        SplitArchiveVolumeMap map,
        TelegramArchiveFormat format,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var streams = map.CreateArchiveStreams(format, cancellationToken);
        try
        {
            using var archive = OpenArchive(streams, format);
            var entries = archive.Entries
                .Where(entry => !entry.IsDirectory && !string.IsNullOrWhiteSpace(entry.Key))
                .Select(entry => new TelegramArchiveEntry(
                    entry.Key!,
                    entry.Size,
                    entry.CompressedSize,
                    entry.CompressionType == CompressionType.None ? 0 : 1,
                    -1,
                    entry.IsEncrypted,
                    IsPlayable(entry.Key!)))
                .Where(entry => entry.IsPlayable)
                .OrderByDescending(entry => entry.UncompressedSize)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var status = entries.Length == 0
                ? "Arşivde oynatılabilir video veya ses bulunamadı."
                : archive.IsEncrypted || entries.All(entry => entry.IsEncrypted)
                    ? "Arşivdeki medya şifreli. Parola desteği henüz etkin değil."
                    : $"{entries.Length} oynatılabilir medya bulundu. Sıkıştırılmış içerik oynatma sırasında çözülecek.";
            return new TelegramArchiveInspection(format, entries, archive.IsComplete, status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                "Arşiv yapısı okunamadı. Parçaların eksiksiz ve doğru sırada olduğundan emin olun.",
                exception);
        }
        finally
        {
            DisposeStreams(streams);
        }
    }, cancellationToken);

    public static IArchive OpenArchive(
        IReadOnlyList<Stream> streams,
        TelegramArchiveFormat format)
    {
        var options = new ReaderOptions
        {
            LeaveStreamOpen = true,
            LookForHeader = format == TelegramArchiveFormat.MultipartRar,
            ExtensionHint = format switch
            {
                TelegramArchiveFormat.SplitZip => "zip",
                TelegramArchiveFormat.MultipartRar => "rar",
                TelegramArchiveFormat.Split7Zip => "7z",
                _ => null
            }
        };
        return streams.Count == 1
            ? ArchiveFactory.OpenArchive(streams[0], options)
            : ArchiveFactory.OpenArchive(streams, options);
    }

    public static IArchiveEntry FindEntry(IArchive archive, string entryName)
    {
        var normalized = NormalizeEntryName(entryName);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            !candidate.IsDirectory &&
            NormalizeEntryName(candidate.Key ?? string.Empty).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry;

        var fileName = Path.GetFileName(normalized);
        entry = archive.Entries.FirstOrDefault(candidate =>
            !candidate.IsDirectory &&
            Path.GetFileName(NormalizeEntryName(candidate.Key ?? string.Empty))
                .Equals(fileName, StringComparison.OrdinalIgnoreCase));
        return entry ?? throw new InvalidDataException($"Arşivde seçilen medya bulunamadı: {entryName}");
    }

    public static void DisposeStreams(IReadOnlyList<Stream> streams)
    {
        foreach (var stream in streams)
        {
            try { stream.Dispose(); }
            catch { }
        }
    }

    private static string NormalizeEntryName(string value) => value.Replace('\\', '/').TrimStart('/');

    private static bool IsPlayable(string name)
    {
        var extension = Path.GetExtension(name);
        return new[]
        {
            ".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".mpg", ".mpeg",
            ".mka", ".mp3", ".flac", ".aac", ".ac3", ".eac3", ".dts", ".opus", ".ogg"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class TelegramArchiveReadStream : Stream
{
    private readonly long _length;
    private readonly Func<long, int, CancellationToken, Task<byte[]>> _readAsync;
    private readonly CancellationToken _lifetimeToken;
    private long _position;

    public TelegramArchiveReadStream(
        long length,
        Func<long, int, CancellationToken, Task<byte[]>> readAsync,
        CancellationToken lifetimeToken)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        _length = length;
        _readAsync = readAsync ?? throw new ArgumentNullException(nameof(readAsync));
        _lifetimeToken = lifetimeToken;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Tampon aralığı geçersiz.");
        return ReadCoreAsync(buffer.AsMemory(offset, count), _lifetimeToken).AsTask().GetAwaiter().GetResult();
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _position >= _length) return 0;
        var wanted = (int)Math.Min(buffer.Length, _length - _position);
        var bytes = _readAsync(_position, wanted, _lifetimeToken).GetAwaiter().GetResult();
        var read = Math.Min(wanted, bytes.Length);
        bytes.AsSpan(0, read).CopyTo(buffer);
        _position += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) => ReadCoreAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0 || _position >= _length) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, cancellationToken);
        var wanted = (int)Math.Min(buffer.Length, _length - _position);
        var bytes = await _readAsync(_position, wanted, linked.Token).ConfigureAwait(false);
        var read = Math.Min(wanted, bytes.Length);
        bytes.AsMemory(0, read).CopyTo(buffer);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > _length) throw new IOException("Arşiv akışında geçersiz konuma gidildi.");
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class TelegramArchiveCacheSession : IAsyncDisposable
{
    private const int InitialBufferBytes = 8 * 1024 * 1024;
    private const int CopyBufferBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly SplitArchiveVolumeMap _map;
    private readonly TelegramArchiveFormat _format;
    private readonly TelegramArchiveEntry _entry;
    private readonly string _sessionDirectory;
    private readonly string _cachePath;
    private readonly Action<string, Exception?>? _reportError;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncSignal _signal = new();
    private Task? _worker;
    private long _availableBytes;
    private volatile Exception? _failure;
    private volatile bool _completed;

    public TelegramArchiveCacheSession(
        SplitArchiveVolumeMap map,
        TelegramArchiveFormat format,
        TelegramArchiveEntry entry,
        string cacheRoot,
        Action<string, Exception?>? reportError = null)
    {
        _map = map;
        _format = format;
        _entry = entry.UncompressedSize > 0
            ? entry
            : throw new ArgumentOutOfRangeException(nameof(entry));
        _reportError = reportError;
        _sessionDirectory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        _cachePath = Path.Combine(_sessionDirectory, SanitizeFileName(Path.GetFileName(entry.Name)));
    }

    public long Length => _entry.UncompressedSize;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _worker ??= Task.Run(Extract, CancellationToken.None);
        }

        var wanted = Math.Min(_entry.UncompressedSize, InitialBufferBytes);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        startup.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await WaitUntilAvailableAsync(wanted, startup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("Arşiv çözme akışı üç dakika içinde oynatma tamponunu hazırlayamadı.");
        }
    }

    public async Task<byte[]> ReadAsync(
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || count < 0 || offset > _entry.UncompressedSize - count)
            throw new EndOfStreamException("Çözülen arşiv akışında istenen byte aralığı bulunamadı.");
        if (count == 0) return [];

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        wait.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await WaitUntilAvailableAsync(offset + 1, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("İstenen arşiv bölümü henüz çözülemedi.");
        }

        var available = Volatile.Read(ref _availableBytes);
        var readable = (int)Math.Min(count, available - offset);
        if (readable <= 0) throw CreateReadFailure();
        var output = new byte[readable];
        await using var file = new FileStream(
            _cachePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        file.Seek(offset, SeekOrigin.Begin);
        var total = 0;
        while (total < output.Length)
        {
            var read = await file.ReadAsync(output.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total == output.Length ? output : output[..total];
    }

    private void Extract()
    {
        IReadOnlyList<Stream>? streams = null;
        IArchive? archive = null;
        Stream? compressedSource = null;
        Stream? input = null;
        try
        {
            Directory.CreateDirectory(_sessionDirectory);
            if (_format == TelegramArchiveFormat.SplitZip &&
                _entry.CompressionMethod == 8 &&
                _entry.DataOffset >= 0 &&
                _entry.CompressedSize > 0)
            {
                compressedSource = new TelegramArchiveReadStream(
                    _entry.CompressedSize,
                    (offset, count, token) => _map.ReadExactAsync(
                        checked(_entry.DataOffset + offset),
                        count,
                        token),
                    _lifetime.Token);
                input = new DeflateStream(compressedSource, CompressionMode.Decompress, leaveOpen: false);
            }
            else
            {
                streams = _map.CreateArchiveStreams(_format, _lifetime.Token);
                archive = TelegramArchiveStreamingEngine.OpenArchive(streams, _format);
                var archiveEntry = TelegramArchiveStreamingEngine.FindEntry(archive, _entry.Name);
                if (archiveEntry.IsEncrypted)
                    throw new InvalidDataException("Arşivdeki medya şifreli. Parola desteği henüz etkin değil.");
                input = archiveEntry.OpenEntryStream();
            }

            using var output = new FileStream(
                _cachePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                CopyBufferBytes,
                FileOptions.SequentialScan);
            var buffer = new byte[CopyBufferBytes];
            while (true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                Volatile.Write(ref _availableBytes, output.Position);
                _signal.Pulse();
            }
            output.Flush(flushToDisk: false);
            Volatile.Write(ref _availableBytes, output.Position);
            _completed = true;
            _signal.Pulse();
            if (_availableBytes <= 0)
                throw new InvalidDataException("Arşivdeki medya boş veri döndürdü.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _completed = true;
            _signal.Pulse();
        }
        catch (Exception exception)
        {
            _failure = exception;
            _completed = true;
            _signal.Pulse();
            _reportError?.Invoke("Telegram arşiv çözme akışı durdu.", exception);
        }
        finally
        {
            try { input?.Dispose(); }
            catch { }
            try { archive?.Dispose(); }
            catch { }
            try { compressedSource?.Dispose(); }
            catch { }
            if (streams is not null) TelegramArchiveStreamingEngine.DisposeStreams(streams);
        }
    }

    private async Task WaitUntilAvailableAsync(long wanted, CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _availableBytes) < wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failure is not null) throw CreateReadFailure();
            if (_completed) break;
            var pulse = _signal.Next;
            await Task.WhenAny(pulse, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
        }
        if (Volatile.Read(ref _availableBytes) <= 0 && _completed) throw CreateReadFailure();
    }

    private Exception CreateReadFailure() => _failure is null
        ? new EndOfStreamException("Arşiv çözme akışı beklenmedik şekilde sona erdi.")
        : new InvalidDataException($"Arşivdeki medya çözülemedi: {_failure.Message}", _failure);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { }
        }
        _lifetime.Dispose();
        try
        {
            if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, recursive: true);
        }
        catch { }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "archive-media.bin" : safe;
    }

    private sealed class AsyncSignal
    {
        private readonly object _gate = new();
        private TaskCompletionSource<bool> _next = Create();

        public Task Next
        {
            get { lock (_gate) return _next.Task; }
        }

        public void Pulse()
        {
            TaskCompletionSource<bool> current;
            lock (_gate)
            {
                current = _next;
                _next = Create();
            }
            current.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> Create() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
