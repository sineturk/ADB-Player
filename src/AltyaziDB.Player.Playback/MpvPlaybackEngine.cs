using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Playback.Interop;

namespace AltyaziDB.Player.Playback;

public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private readonly IAppLogger _logger;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);

    private nint _context;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _eventTask;
    private Task? _pollTask;
    private double _pendingResumePosition;
    private string? _currentSource;
    private IReadOnlyList<MediaTrack> _currentTracks = Array.Empty<MediaTrack>();
    private IReadOnlyList<ChapterInfo> _currentChapters = Array.Empty<ChapterInfo>();
    private PlaybackSnapshot _currentSnapshot = PlaybackSnapshot.Empty;
    private bool _nativeInitialized;
    private bool _disposed;

    public MpvPlaybackEngine(IAppLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;
    public event EventHandler<IReadOnlyList<MediaTrack>>? TracksChanged;
    public event EventHandler<IReadOnlyList<ChapterInfo>>? ChaptersChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? ErrorOccurred;

    public bool IsInitialized => _context != nint.Zero && _nativeInitialized && !_disposed;

    public PlaybackSnapshot CurrentSnapshot
    {
        get
        {
            lock (_stateGate)
            {
                return _currentSnapshot;
            }
        }
    }

    public IReadOnlyList<MediaTrack> CurrentTracks
    {
        get
        {
            lock (_stateGate)
            {
                return _currentTracks;
            }
        }
    }

    public IReadOnlyList<ChapterInfo> CurrentChapters
    {
        get
        {
            lock (_stateGate)
            {
                return _currentChapters;
            }
        }
    }

    public async Task InitializeAsync(
        nint videoWindowHandle,
        CancellationToken cancellationToken = default)
    {
        if (videoWindowHandle == nint.Zero)
        {
            throw new ArgumentException("Geçerli bir video pencere tanıtıcısı gereklidir.", nameof(videoWindowHandle));
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsInitialized)
            {
                return;
            }

            try
            {
                MpvLibraryResolver.EnsureRegistered();
                _context = MpvNative.mpv_create();
                if (_context == nint.Zero)
                {
                    throw new InvalidOperationException("libmpv oturumu oluşturulamadı.");
                }

                SetOption("terminal", "no");
                SetOption("osc", "no");
                SetOption("input-default-bindings", "no");
                SetOption("input-vo-keyboard", "no");
                SetOption("keep-open", "yes");
                SetOption("idle", "yes");
                SetOption("force-window", "no");
                SetOption("vo", "gpu-next");
                SetOption("gpu-api", "d3d11");
                SetOption("hwdec", "auto-safe");
                SetOption("audio-client-name", "AltyazıDB Player");
                SetOption("sub-auto", "fuzzy");
                SetOption("audio-file-auto", "fuzzy");
                SetOption("alang", "tr,tur,eng,en");
                SetOption("slang", "tr,tur,eng,en");
                SetOption("msg-level", "all=warn");
                SetOption("screenshot-format", "png");
                // Uzak video ve harici ses kaynakları doğrudan oynatılırken libmpv
                // veriyi arka planda önbelleğe alır; dosyanın tamamı beklenmez.
                SetOption("cache", "yes");
                SetOption("cache-pause", "no");
                SetOption("cache-secs", "60");
                SetOption("demuxer-max-bytes", "128MiB");
                SetOption("demuxer-max-back-bytes", "32MiB");
                SetOption("network-timeout", "20");

                // libmpv wid seçeneği x64 Windows'ta tam HWND değerini almalıdır.
                // 32 bite kırpmak bazı makinelerde geçersiz pencere tanıtıcısı üretir.
                var wid = videoWindowHandle.ToInt64();
                Check(MpvNative.SetOptionInt64(_context, "wid", wid), "Video yüzeyi bağlanamadı");
                Check(MpvNative.mpv_initialize(_context), "libmpv başlatılamadı");
                _nativeInitialized = true;

                _lifetimeCts = new CancellationTokenSource();
                _eventTask = Task.Run(() => EventLoopAsync(_lifetimeCts.Token));
                _pollTask = Task.Run(() => PollLoopAsync(_lifetimeCts.Token));

                _logger.Info("libmpv başarıyla başlatıldı.");
            }
            catch (DllNotFoundException exception)
            {
                CleanupFailedInitialization();
                throw new InvalidOperationException(
                    "libmpv Windows dosyaları yüklenemedi. Build W1 native kopyalama adımını ve NuGet paket önbelleğini kontrol edin.\n\n" + MpvLibraryResolver.GetDiagnosticMessage(),
                    exception);
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

    public Task OpenAsync(
        string source,
        double resumePositionSeconds = 0,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var headerFields = httpHeaders is null
            ? string.Empty
            : string.Join(",", httpHeaders
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{pair.Key}: {pair.Value.Replace(",", "\\,")}"));
        SetPropertyString("http-header-fields", headerFields);

        _currentSource = source;
        _pendingResumePosition = Math.Max(0, resumePositionSeconds);
        SetPropertyString("secondary-sid", "no");
        SetAudioDelay(0);
        ClearAudioTempoCorrection();
        ExecuteCommand("loadfile", source, "replace");
        _logger.Info($"Kaynak açılıyor: {SafeLogSource(source)} · HTTP başlıkları: {(httpHeaders?.Count ?? 0)}");
        return Task.CompletedTask;
    }

    public void TogglePause()
    {
        EnsureInitialized();
        var paused = MpvNative.GetFlag(_context, "pause", true);
        Pause(!paused);
    }

    private static string SafeLogSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return $"{uri.Scheme}://{uri.Host}/…";
        return source;
    }

    public void Pause(bool paused)
    {
        EnsureInitialized();
        CheckAndReport(MpvNative.SetPropertyFlag(_context, "pause", paused), "Duraklatma durumu değiştirilemedi");
    }

    public void SeekRelative(double seconds)
    {
        EnsureInitialized();
        ExecuteCommand(
            "seek",
            seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "relative+exact");
    }

    public void SeekAbsolute(double seconds)
    {
        EnsureInitialized();
        ExecuteCommand(
            "seek",
            Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture),
            "absolute+exact");
    }

    public void SetVolume(double volume)
    {
        EnsureInitialized();
        CheckAndReport(
            MpvNative.SetPropertyDouble(_context, "volume", Math.Clamp(volume, 0, 150)),
            "Ses seviyesi değiştirilemedi");
    }

    public void SetSpeed(double speed)
    {
        EnsureInitialized();
        CheckAndReport(
            MpvNative.SetPropertyDouble(_context, "speed", Math.Clamp(speed, 0.25, 4)),
            "Oynatma hızı değiştirilemedi");
    }

    public void SetAudioDelay(double seconds)
    {
        EnsureInitialized();
        CheckAndReport(
            MpvNative.SetPropertyDouble(_context, "audio-delay", Math.Clamp(seconds, -120, 120)),
            "Ses senkron gecikmesi değiştirilemedi");
    }

    public void SetAudioTempoCorrection(double factor)
    {
        EnsureInitialized();
        var bounded = Math.Clamp(factor, 0.5, 2.0);
        if (Math.Abs(bounded - 1.0) < 0.0000005)
        {
            ClearAudioTempoCorrection();
            return;
        }

        // AudioSyncTool uses FFmpeg atempo for progressive drift correction.
        // Apply the same factor only to the selected audio track; video speed
        // remains unchanged and the pitch is preserved by libavfilter.
        SetPropertyString(
            "af",
            $"lavfi=[atempo={bounded.ToString("0.############", CultureInfo.InvariantCulture)}]");
        _logger.Info($"AudioSync tempo correction: {bounded:0.#########}x");
    }

    public void ClearAudioTempoCorrection()
    {
        EnsureInitialized();
        SetPropertyString("af", string.Empty);
    }

    public void SelectAudioTrack(long? trackId)
    {
        EnsureInitialized();
        SetPropertyString("aid", trackId?.ToString(CultureInfo.InvariantCulture) ?? "auto");
    }

    public void SelectSubtitleTrack(long? trackId)
    {
        EnsureInitialized();
        SetPropertyString("sid", trackId?.ToString(CultureInfo.InvariantCulture) ?? "no");
    }

    public void SelectSecondarySubtitleTrack(long? trackId)
    {
        EnsureInitialized();
        SetPropertyString("secondary-sid", trackId?.ToString(CultureInfo.InvariantCulture) ?? "no");
    }

    public void SetSubtitleDelay(double seconds, bool secondary = false)
    {
        EnsureInitialized();
        CheckAndReport(
            MpvNative.SetPropertyDouble(_context, secondary ? "secondary-sub-delay" : "sub-delay", Math.Clamp(seconds, -120, 120)),
            "Altyazı gecikmesi değiştirilemedi");
    }

    public void SetSubtitleScale(double scale, bool secondary = false)
    {
        EnsureInitialized();
        CheckAndReport(
            MpvNative.SetPropertyDouble(_context, secondary ? "secondary-sub-scale" : "sub-scale", Math.Clamp(scale, 0.25, 4.0)),
            "Altyazı ölçeği değiştirilemedi");
    }

    public void SetSubtitlePosition(double position, bool secondary = false)
    {
        EnsureInitialized();
        CheckAndReport(
            MpvNative.SetPropertyDouble(_context, secondary ? "secondary-sub-pos" : "sub-pos", Math.Clamp(position, 0, 150)),
            "Altyazı konumu değiştirilemedi");
    }

    public void SetSubtitleAssOverride(bool preserveOriginal, bool secondary = false)
    {
        EnsureInitialized();
        SetPropertyString(secondary ? "secondary-sub-ass-override" : "sub-ass-override", preserveOriginal ? "no" : "force");
    }

    public void SelectChapter(long chapterId)
    {
        EnsureInitialized();
        SetPropertyString("chapter", Math.Max(0, chapterId).ToString(CultureInfo.InvariantCulture));
    }

    public void AddSubtitle(string path, bool select = true)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ExecuteCommand("sub-add", path, select ? "select" : "auto");
        RefreshTracks();
    }

    public void ReloadSubtitle()
    {
        EnsureInitialized();
        ExecuteCommand("sub-reload");
        RefreshTracks();
    }

    public void AddAudio(string path, IReadOnlyDictionary<string, string>? httpHeaders = null)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (httpHeaders is not null && httpHeaders.Count > 0)
        {
            var headerFields = string.Join(",", httpHeaders
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{pair.Key}: {pair.Value.Replace(",", "\\,")}"));
            SetPropertyString("http-header-fields", headerFields);
        }
        ExecuteCommand("audio-add", path, "select");
        RefreshTracks();
    }

    public void SetShaderChain(IReadOnlyList<string> shaderPaths)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(shaderPaths);

        if (shaderPaths.Count == 0)
        {
            SetPropertyString("glsl-shaders", string.Empty);
            _logger.Info("GLSL görüntü iyileştirme zinciri kapatıldı.");
            return;
        }

        var files = shaderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
        var missing = files.FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
        {
            throw new FileNotFoundException("Görüntü iyileştirme shader dosyası bulunamadı.", missing);
        }

        SetPropertyString("glsl-shaders", string.Join(';', files));
        _logger.Info($"GLSL görüntü iyileştirme zinciri etkinleştirildi: {files.Length} shader");
    }

    public void ApplyVideoProcessingSettings(VideoProcessingSettings settings)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(settings);

        SetPropertyString("target-colorspace-hint", settings.TargetColorspaceHint ? "auto" : "no");
        SetPropertyString("target-colorspace-hint-mode", string.IsNullOrWhiteSpace(settings.TargetColorspaceHintMode) ? "target" : settings.TargetColorspaceHintMode);
        SetPropertyString("target-trc", string.IsNullOrWhiteSpace(settings.TargetTrc) ? "auto" : settings.TargetTrc);
        SetPropertyString("target-peak", string.IsNullOrWhiteSpace(settings.TargetPeak) ? "auto" : settings.TargetPeak);
        SetPropertyString("tone-mapping", string.IsNullOrWhiteSpace(settings.ToneMapping) ? "auto" : settings.ToneMapping);
        SetPropertyString("gamut-mapping-mode", string.IsNullOrWhiteSpace(settings.GamutMappingMode) ? "auto" : settings.GamutMappingMode);
        SetPropertyString("deband", settings.DebandEnabled ? "yes" : "no");

        if (settings.DebandEnabled)
        {
            SetPropertyString("deband-iterations", Math.Clamp(settings.DebandIterations, 1, 16).ToString(CultureInfo.InvariantCulture));
            SetPropertyString("deband-threshold", Math.Clamp(settings.DebandThreshold, 0, 4096).ToString("0.###", CultureInfo.InvariantCulture));
            SetPropertyString("deband-range", Math.Clamp(settings.DebandRange, 1, 64).ToString("0.###", CultureInfo.InvariantCulture));
            SetPropertyString("deband-grain", Math.Clamp(settings.DebandGrain, 0, 4096).ToString("0.###", CultureInfo.InvariantCulture));
        }

        _logger.Info(
            $"Video profili uygulandı: {settings.ProfileCode} · deband={(settings.DebandEnabled ? "on" : "off")} · tone={settings.ToneMapping}");
    }

    public void SetVideoEqualizer(double brightness, double contrast, double saturation, double gamma)
    {
        EnsureInitialized();
        CheckAndReport(MpvNative.SetPropertyDouble(_context, "brightness", Math.Clamp(brightness, -100, 100)), "Parlaklık değiştirilemedi");
        CheckAndReport(MpvNative.SetPropertyDouble(_context, "contrast", Math.Clamp(contrast, -100, 100)), "Kontrast değiştirilemedi");
        CheckAndReport(MpvNative.SetPropertyDouble(_context, "saturation", Math.Clamp(saturation, -100, 100)), "Doygunluk değiştirilemedi");
        CheckAndReport(MpvNative.SetPropertyDouble(_context, "gamma", Math.Clamp(gamma, -100, 100)), "Gamma değiştirilemedi");
    }

    public IReadOnlyList<AudioDeviceInfo> GetAudioDevices()
    {
        EnsureInitialized();
        var devices = new List<AudioDeviceInfo> { AudioDeviceInfo.Automatic() };
        try
        {
            var count = Math.Clamp(MpvNative.GetInt64(_context, "audio-device-list/count"), 0, 128);
            for (var index = 0; index < count; index++)
            {
                var name = MpvNative.GetString(_context, $"audio-device-list/{index}/name");
                if (string.IsNullOrWhiteSpace(name) || name.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var description = MpvNative.GetString(_context, $"audio-device-list/{index}/description");
                devices.Add(new AudioDeviceInfo(name, string.IsNullOrWhiteSpace(description) ? name : description));
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"Ses aygıtları listelenemedi: {exception.Message}");
        }

        return devices;
    }

    public void SetAudioDevice(string deviceName)
    {
        EnsureInitialized();
        SetPropertyString("audio-device", string.IsNullOrWhiteSpace(deviceName) ? "auto" : deviceName);
        _logger.Info($"Ses çıkış aygıtı seçildi: {(string.IsNullOrWhiteSpace(deviceName) ? "auto" : deviceName)}");
    }

    public void TakeScreenshot(string path)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ExecuteCommand("screenshot-to-file", path, "video");
        _logger.Info($"Ekran görüntüsü kaydedildi: {path}");
    }

    public void StepFrame(bool backward = false)
    {
        EnsureInitialized();
        ExecuteCommand(backward ? "frame-back-step" : "frame-step");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCts?.Cancel();

        var tasks = new[] { _eventTask, _pollTask }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.Warning("libmpv arka plan görevleri kapanırken zaman aşımı oluştu.");
        }

        if (_context != nint.Zero)
        {
            if (_nativeInitialized)
            {
                MpvNative.mpv_terminate_destroy(_context);
            }
            else
            {
                MpvNative.mpv_destroy(_context);
            }

            _nativeInitialized = false;
            _context = nint.Zero;
        }

        _lifetimeCts?.Dispose();
        _initializeGate.Dispose();
        _logger.Info("libmpv kapatıldı.");
    }

    private async Task EventLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _context != nint.Zero)
            {
                var eventPointer = MpvNative.mpv_wait_event(_context, 0.20);
                if (eventPointer == nint.Zero)
                {
                    continue;
                }

                var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
                switch (mpvEvent.EventId)
                {
                    case MpvEventId.None:
                        break;
                    case MpvEventId.FileLoaded:
                        OnFileLoaded();
                        break;
                    case MpvEventId.EndFile:
                        HandleEndFile(mpvEvent);
                        break;
                    case MpvEventId.VideoReconfig:
                    case MpvEventId.AudioReconfig:
                        RefreshTracks();
                        break;
                    case MpvEventId.QueueOverflow:
                        _logger.Warning("libmpv olay kuyruğu doldu.");
                        break;
                    case MpvEventId.Shutdown:
                        return;
                }

                await Task.Yield();
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportError("libmpv olay döngüsü durdu.", exception);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var trackPollCounter = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                PublishSnapshot();
                trackPollCounter++;
                if (trackPollCounter >= 8)
                {
                    trackPollCounter = 0;
                    RefreshTracks();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportError("Oynatma durumu izlenemedi.", exception);
        }
    }

    private void HandleEndFile(MpvEvent mpvEvent)
    {
        PublishSnapshot();

        if (mpvEvent.Data == nint.Zero)
        {
            return;
        }

        var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
        if (endFile.Reason == MpvEndFileReason.Eof)
        {
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
        else if (endFile.Reason == MpvEndFileReason.Error)
        {
            ReportError($"Oynatma dosya hatasıyla sona erdi: {MpvNative.ErrorText(endFile.Error)}");
        }
    }

    private void OnFileLoaded()
    {
        if (_pendingResumePosition > 2)
        {
            SeekAbsolute(_pendingResumePosition);
            _logger.Info($"Kayıtlı konuma dönüldü: {_pendingResumePosition:0.0} sn");
        }

        _pendingResumePosition = 0;
        RefreshTracks();
        RefreshChapters();
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        if (!IsInitialized)
        {
            return;
        }

        try
        {
            var title = MpvNative.GetString(_context, "media-title");
            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(_currentSource))
            {
                title = Uri.TryCreate(_currentSource, UriKind.Absolute, out var uri) && uri.IsFile
                    ? Path.GetFileName(uri.LocalPath)
                    : Path.GetFileName(_currentSource);
            }

            var snapshot = new PlaybackSnapshot(
                _currentSource,
                string.IsNullOrWhiteSpace(title) ? "AltyazıDB Player" : title,
                Math.Max(0, MpvNative.GetDouble(_context, "time-pos")),
                Math.Max(0, MpvNative.GetDouble(_context, "duration")),
                MpvNative.GetDouble(_context, "volume", 80),
                MpvNative.GetDouble(_context, "speed", 1),
                MpvNative.GetFlag(_context, "pause", true),
                MpvNative.GetFlag(_context, "core-idle", true),
                MpvNative.GetFlag(_context, "paused-for-cache", false),
                !string.IsNullOrWhiteSpace(MpvNative.GetString(_context, "video-format")),
                !string.IsNullOrWhiteSpace(MpvNative.GetString(_context, "audio-codec-name")));

            lock (_stateGate)
            {
                _currentSnapshot = snapshot;
            }

            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch (Exception exception)
        {
            _logger.Error("Oynatma anlık durumu okunamadı.", exception);
        }
    }

    private void RefreshTracks()
    {
        if (!IsInitialized)
        {
            return;
        }

        try
        {
            var count = Math.Clamp(MpvNative.GetInt64(_context, "track-list/count"), 0, 256);
            var tracks = new List<MediaTrack>((int)count);

            for (var index = 0; index < count; index++)
            {
                var prefix = $"track-list/{index}";
                var id = MpvNative.GetInt64(_context, $"{prefix}/id", -1);
                var type = ParseTrackType(MpvNative.GetString(_context, $"{prefix}/type"));
                if (id < 0 || type == TrackType.Unknown)
                {
                    continue;
                }

                tracks.Add(new MediaTrack(
                    id,
                    type,
                    MpvNative.GetString(_context, $"{prefix}/title"),
                    MpvNative.GetString(_context, $"{prefix}/lang"),
                    MpvNative.GetString(_context, $"{prefix}/codec"),
                    MpvNative.GetFlag(_context, $"{prefix}/selected"),
                    MpvNative.GetFlag(_context, $"{prefix}/external"),
                    MpvNative.GetFlag(_context, $"{prefix}/default")));
            }

            var readOnly = new ReadOnlyCollection<MediaTrack>(tracks);
            lock (_stateGate)
            {
                _currentTracks = readOnly;
            }

            TracksChanged?.Invoke(this, readOnly);
        }
        catch (Exception exception)
        {
            _logger.Error("Medya parçaları okunamadı.", exception);
        }
    }

    private void RefreshChapters()
    {
        if (!IsInitialized)
        {
            return;
        }

        try
        {
            var count = Math.Clamp(MpvNative.GetInt64(_context, "chapter-list/count"), 0, 10000);
            var chapters = new List<ChapterInfo>((int)count);

            for (var index = 0; index < count; index++)
            {
                var prefix = $"chapter-list/{index}";
                var title = MpvNative.GetString(_context, $"{prefix}/title");
                var time = Math.Max(0, MpvNative.GetDouble(_context, $"{prefix}/time"));

                chapters.Add(new ChapterInfo(
                    index,
                    time,
                    string.IsNullOrWhiteSpace(title) ? $"Bölüm {index + 1}" : title));
            }

            var readOnly = new ReadOnlyCollection<ChapterInfo>(chapters);
            lock (_stateGate)
            {
                _currentChapters = readOnly;
            }

            ChaptersChanged?.Invoke(this, readOnly);
        }
        catch (Exception exception)
        {
            _logger.Error("Bölüm bilgileri okunamadı.", exception);
        }
    }

    private static TrackType ParseTrackType(string? value) => value switch
    {
        "video" => TrackType.Video,
        "audio" => TrackType.Audio,
        "sub" => TrackType.Subtitle,
        _ => TrackType.Unknown
    };

    private void SetOption(string name, string value)
    {
        Check(MpvNative.mpv_set_option_string(_context, name, value), $"mpv seçeneği ayarlanamadı: {name}");
    }

    private void SetPropertyString(string name, string value)
    {
        CheckAndReport(
            MpvNative.mpv_set_property_string(_context, name, value),
            $"mpv özelliği değiştirilemedi: {name}");
    }

    private void ExecuteCommand(params string[] arguments)
    {
        var error = MpvNative.Command(_context, arguments);
        CheckAndReport(error, $"mpv komutu başarısız: {arguments[0]}");
    }

    private static void Check(int error, string message)
    {
        if (error < 0)
        {
            throw new InvalidOperationException($"{message}: {MpvNative.ErrorText(error)}");
        }
    }

    private void CheckAndReport(int error, string message)
    {
        if (error >= 0)
        {
            return;
        }

        ReportError($"{message}: {MpvNative.ErrorText(error)}");
    }

    private void ReportError(string message, Exception? exception = null)
    {
        _logger.Error(message, exception);
        ErrorOccurred?.Invoke(this, exception is null ? message : $"{message} {exception.Message}");
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!IsInitialized)
        {
            throw new InvalidOperationException("Oynatma motoru henüz başlatılmadı.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MpvPlaybackEngine));
        }
    }

    private void CleanupFailedInitialization()
    {
        if (_context == nint.Zero)
        {
            return;
        }

        if (_nativeInitialized)
        {
            MpvNative.mpv_terminate_destroy(_context);
        }
        else
        {
            MpvNative.mpv_destroy(_context);
        }

        _nativeInitialized = false;
        _context = nint.Zero;
    }
}
