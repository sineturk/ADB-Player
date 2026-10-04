using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface IPlaybackEngine : IAsyncDisposable
{
    event EventHandler<PlaybackSnapshot>? SnapshotChanged;
    event EventHandler<IReadOnlyList<MediaTrack>>? TracksChanged;
    event EventHandler<IReadOnlyList<ChapterInfo>>? ChaptersChanged;
    event EventHandler? PlaybackEnded;
    event EventHandler<string>? ErrorOccurred;

    bool IsInitialized { get; }
    PlaybackSnapshot CurrentSnapshot { get; }
    IReadOnlyList<MediaTrack> CurrentTracks { get; }
    IReadOnlyList<ChapterInfo> CurrentChapters { get; }

    Task InitializeAsync(nint videoWindowHandle, CancellationToken cancellationToken = default);
    Task OpenAsync(
        string source,
        double resumePositionSeconds = 0,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        CancellationToken cancellationToken = default);

    void TogglePause();
    void Pause(bool paused);
    void SeekRelative(double seconds);
    void SeekAbsolute(double seconds);
    void SetVolume(double volume);
    void SetSpeed(double speed);
    void SetAudioDelay(double seconds);
    void SetAudioTempoCorrection(double factor);
    void ClearAudioTempoCorrection();
    void SelectAudioTrack(long? trackId);
    void SelectSubtitleTrack(long? trackId);
    void SelectSecondarySubtitleTrack(long? trackId);
    void SetSubtitleDelay(double seconds, bool secondary = false);
    void SetSubtitleScale(double scale, bool secondary = false);
    void SetSubtitlePosition(double position, bool secondary = false);
    void SetSubtitleAssOverride(bool preserveOriginal, bool secondary = false);
    void SelectChapter(long chapterId);
    void AddSubtitle(string path, bool select = true);
    void ReloadSubtitle();
    void AddAudio(string path, IReadOnlyDictionary<string, string>? httpHeaders = null);
    void SetShaderChain(IReadOnlyList<string> shaderPaths);
    void ApplyVideoProcessingSettings(VideoProcessingSettings settings);
    void SetVideoEqualizer(double brightness, double contrast, double saturation, double gamma);
    IReadOnlyList<AudioDeviceInfo> GetAudioDevices();
    void SetAudioDevice(string deviceName);
    void TakeScreenshot(string path);
    void StepFrame(bool backward = false);
}
