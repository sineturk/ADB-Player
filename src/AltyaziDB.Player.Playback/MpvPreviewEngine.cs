using System.Globalization;
using System.Runtime.InteropServices;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Playback.Interop;

namespace AltyaziDB.Player.Playback;

/// <summary>
/// Zaman çizelgesi önizlemesi için ana oynatıcıdan bağımsız, sessiz ve duraklatılmış
/// ikinci bir libmpv oturumu kullanır. Ana videonun konumu hiçbir zaman değiştirilmez.
/// </summary>
public sealed class MpvPreviewEngine : IAsyncDisposable
{
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _eventGate = new();

    private nint _context;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _eventTask;
    private TaskCompletionSource<bool>? _fileLoadedSource;
    private TaskCompletionSource<bool>? _playbackRestartSource;
    private string? _currentSource;
    private string _currentHeaderSignature = string.Empty;
    private bool _nativeInitialized;
    private bool _disposed;

    public MpvPreviewEngine(IAppLogger logger)
    {
        _logger = logger;
    }

    public bool IsInitialized => _context != nint.Zero && _nativeInitialized && !_disposed;

    public async Task InitializeAsync(nint videoWindowHandle, CancellationToken cancellationToken = default)
    {
        if (videoWindowHandle == nint.Zero)
            throw new ArgumentException("Geçerli bir önizleme yüzeyi gereklidir.", nameof(videoWindowHandle));

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsInitialized) return;

            MpvLibraryResolver.EnsureRegistered();
            _context = MpvNative.mpv_create();
            if (_context == nint.Zero)
                throw new InvalidOperationException("Önizleme libmpv oturumu oluşturulamadı.");

            try
            {
                SetOption("terminal", "no");
                SetOption("osc", "no");
                SetOption("input-default-bindings", "no");
                SetOption("input-vo-keyboard", "no");
                SetOption("keep-open", "yes");
                SetOption("idle", "yes");
                SetOption("force-window", "no");
                SetOption("vo", "gpu-next");
                SetOption("gpu-api", "d3d11");
                SetOption("hwdec", "no");
                SetOption("audio", "no");
                SetOption("pause", "yes");
                SetOption("cache", "yes");
                SetOption("cache-pause", "no");
                SetOption("demuxer-max-bytes", "32MiB");
                SetOption("network-timeout", "12");
                SetOption("msg-level", "all=error");

                Check(MpvNative.SetOptionInt64(_context, "wid", videoWindowHandle.ToInt64()), "Önizleme yüzeyi bağlanamadı");
                Check(MpvNative.mpv_initialize(_context), "Önizleme libmpv oturumu başlatılamadı");
                _nativeInitialized = true;

                _lifetimeCts = new CancellationTokenSource();
                _eventTask = Task.Run(() => EventLoopAsync(_lifetimeCts.Token));
                _logger.Info("Zaman çizelgesi önizleme motoru başlatıldı.");
            }
            catch
            {
                CleanupFailedInitialization();
                throw;
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task ShowFrameAsync(
        string source,
        IReadOnlyDictionary<string, string>? httpHeaders,
        double seconds,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var headerFields = BuildHeaderFields(httpHeaders);
            var sourceChanged = !string.Equals(_currentSource, source, StringComparison.Ordinal) ||
                                !string.Equals(_currentHeaderSignature, headerFields, StringComparison.Ordinal);

            if (sourceChanged)
            {
                SetPropertyString("http-header-fields", headerFields);
                var loaded = NewCompletionSource();
                lock (_eventGate) _fileLoadedSource = loaded;
                ExecuteCommand("loadfile", source, "replace");
                try
                {
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ClearIfCurrent(ref _fileLoadedSource, loaded);
                }
                _currentSource = source;
                _currentHeaderSignature = headerFields;
                CheckAndReport(MpvNative.SetPropertyFlag(_context, "pause", true), "Önizleme duraklatılamadı");
            }

            var restarted = NewCompletionSource();
            lock (_eventGate) _playbackRestartSource = restarted;
            ExecuteCommand(
                "seek",
                Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture),
                "absolute+keyframes");

            try
            {
                await restarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Bazı ağ akışları seek tamamlandığında PlaybackRestart olayı üretmeyebilir.
                // Kare yine de yüzeye yazılmış olabileceğinden önizlemeyi açık bırakırız.
                _logger.Warning("Zaman çizelgesi önizlemesi kare bekleme süresini aştı.");
            }
            finally
            {
                ClearIfCurrent(ref _playbackRestartSource, restarted);
                CheckAndReport(MpvNative.SetPropertyFlag(_context, "pause", true), "Önizleme duraklatılamadı");
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts?.Cancel();

        try
        {
            if (_eventTask is not null)
                await _eventTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.Warning("Önizleme motoru kapanırken zaman aşımı oluştu.");
        }

        if (_context != nint.Zero)
        {
            if (_nativeInitialized) MpvNative.mpv_terminate_destroy(_context);
            else MpvNative.mpv_destroy(_context);
            _context = nint.Zero;
            _nativeInitialized = false;
        }

        _lifetimeCts?.Dispose();
        _initializeGate.Dispose();
        _requestGate.Dispose();
    }

    private async Task EventLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _context != nint.Zero)
            {
                var pointer = MpvNative.mpv_wait_event(_context, 0.15);
                if (pointer == nint.Zero) continue;
                var value = Marshal.PtrToStructure<MpvEvent>(pointer);
                switch (value.EventId)
                {
                    case MpvEventId.FileLoaded:
                        Complete(ref _fileLoadedSource, true);
                        break;
                    case MpvEventId.PlaybackRestart:
                        Complete(ref _playbackRestartSource, true);
                        break;
                    case MpvEventId.EndFile:
                        if (value.Data != nint.Zero)
                        {
                            var endFile = Marshal.PtrToStructure<MpvEventEndFile>(value.Data);
                            if (endFile.Reason == MpvEndFileReason.Error && endFile.Error < 0)
                            {
                                var message = ErrorText(endFile.Error);
                                Fail(ref _fileLoadedSource, new InvalidOperationException(message));
                                Fail(ref _playbackRestartSource, new InvalidOperationException(message));
                            }
                        }
                        break;
                    case MpvEventId.Shutdown:
                        return;
                }
                await Task.Yield();
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Error("Zaman çizelgesi önizleme olay döngüsü durdu.", exception);
            Fail(ref _fileLoadedSource, exception);
            Fail(ref _playbackRestartSource, exception);
        }
    }

    private static TaskCompletionSource<bool> NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Complete(ref TaskCompletionSource<bool>? source, bool value)
    {
        TaskCompletionSource<bool>? current;
        lock (_eventGate)
        {
            current = source;
            source = null;
        }
        current?.TrySetResult(value);
    }

    private void Fail(ref TaskCompletionSource<bool>? source, Exception exception)
    {
        TaskCompletionSource<bool>? current;
        lock (_eventGate)
        {
            current = source;
            source = null;
        }
        current?.TrySetException(exception);
    }


    private void ClearIfCurrent(ref TaskCompletionSource<bool>? source, TaskCompletionSource<bool> expected)
    {
        lock (_eventGate)
        {
            if (ReferenceEquals(source, expected)) source = null;
        }
    }

    private static string BuildHeaderFields(IReadOnlyDictionary<string, string>? headers) =>
        headers is null
            ? string.Empty
            : string.Join(",", headers
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{pair.Key}: {pair.Value.Replace(",", "\\,")}"));

    private void SetOption(string name, string value) =>
        Check(MpvNative.mpv_set_option_string(_context, name, value), $"Önizleme seçeneği ayarlanamadı: {name}");

    private void SetPropertyString(string name, string value) =>
        CheckAndReport(MpvNative.mpv_set_property_string(_context, name, value), $"Önizleme özelliği ayarlanamadı: {name}");

    private void ExecuteCommand(params string[] arguments) =>
        CheckAndReport(MpvNative.Command(_context, arguments), $"Önizleme komutu başarısız: {arguments[0]}");

    private void Check(int result, string message)
    {
        if (result >= 0) return;
        throw new InvalidOperationException($"{message}: {ErrorText(result)}");
    }

    private void CheckAndReport(int result, string message)
    {
        if (result >= 0) return;
        _logger.Warning($"{message}: {ErrorText(result)}");
    }

    private static string ErrorText(int result)
    {
        var pointer = MpvNative.mpv_error_string(result);
        return pointer == nint.Zero ? $"mpv hata kodu {result}" : Marshal.PtrToStringUTF8(pointer) ?? $"mpv hata kodu {result}";
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!IsInitialized) throw new InvalidOperationException("Önizleme motoru hazır değil.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CleanupFailedInitialization()
    {
        if (_context != nint.Zero)
        {
            if (_nativeInitialized) MpvNative.mpv_terminate_destroy(_context);
            else MpvNative.mpv_destroy(_context);
        }
        _context = nint.Zero;
        _nativeInitialized = false;
    }
}
