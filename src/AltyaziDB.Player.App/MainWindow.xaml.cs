using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Playback;
using Forms = System.Windows.Forms;

namespace AltyaziDB.Player.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".truehd",
        ".flac", ".mka", ".m4a", ".mp3", ".ogg", ".opus", ".wav"
    };

    private readonly MainViewModel _viewModel;
    private readonly MpvPreviewEngine _previewEngine;
    private readonly IAppLogger _logger;
    private readonly string? _startupSource;
    private bool _seekDragging;
    private double? _pendingSeekSeconds;
    private bool _previewReady;
    private CancellationTokenSource? _previewCts;
    private long _previewSequence;
    private bool _closingCompleted;
    private bool _closingStarted;
    private bool _isFullscreen;
    private bool _isMiniPlayer;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousWindowState;
    private Rect _previousBounds;
    private GridLength _previousHeaderHeight;
    private GridLength _previousControlsHeight;
    private GridLength _previousSidebarWidth;
    private Thickness _previousPlayerContentMargin;
    private Thickness _previousVideoBorderThickness;
    private Thickness _previousVideoMargin;
    private CornerRadius _previousVideoCornerRadius;
    private bool _fullscreenSeekDragging;
    private readonly DispatcherTimer _fullscreenControlsHideTimer;

    public MainWindow(MainViewModel viewModel, MpvPreviewEngine previewEngine, IAppLogger logger, string? startupSource = null)
    {
        _viewModel = viewModel;
        _previewEngine = previewEngine;
        _logger = logger;
        _startupSource = startupSource;
        DataContext = viewModel;
        InitializeComponent();

        _fullscreenControlsHideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(2.8)
        };
        _fullscreenControlsHideTimer.Tick += FullscreenControlsHideTimer_OnTick;
        FullscreenControlsPopup.DataContext = viewModel;

        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        VideoHost.HandleCreated += VideoHost_OnHandleCreated;
        VideoHost.PointerActivity += VideoHost_OnPointerActivity;
        PreviewVideoHost.HandleCreated += PreviewVideoHost_OnHandleCreated;
        Closing += Window_OnClosing;
        ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_OnThreadPreprocessMessage;
        ApplyMediaLayout();
    }

    private async void VideoHost_OnHandleCreated(object? sender, nint handle)
    {
        await _viewModel.PrepareAsync(handle);
        ApplyMediaLayout();

        if (string.IsNullOrWhiteSpace(_startupSource))
        {
            return;
        }

        if (File.Exists(_startupSource))
        {
            if (string.Equals(Path.GetExtension(_startupSource), ".torrent", StringComparison.OrdinalIgnoreCase))
            {
                await _viewModel.Torrent.OpenTorrentFileAsync(_startupSource);
            }
            else
            {
                await _viewModel.OpenPathAsync(_startupSource);
            }
        }
        else if (Uri.TryCreate(_startupSource, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
            {
                await _viewModel.Torrent.OpenMagnetAsync(_startupSource);
            }
            else if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                await _viewModel.Sources.OpenUrlAsync(_startupSource);
            }
        }
    }

    private async void PreviewVideoHost_OnHandleCreated(object? sender, nint handle)
    {
        try
        {
            await _previewEngine.InitializeAsync(handle);
            _previewReady = true;
        }
        catch (Exception exception)
        {
            _logger.Error("Zaman çizelgesi önizleme motoru başlatılamadı.", exception);
            _previewReady = false;
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMediaCenterVisible))
        {
            ApplyMediaLayout();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedSidebarIndex)
                 && _viewModel.IsMediaCenterVisible)
        {
            ApplyMediaLayout();
        }
    }

    public void ShowModernSources()
    {
        _viewModel.SelectedSidebarIndex = 0;
        ModernMediaCenter.ShowSources();
        ApplyMediaLayout();
    }

    public void ShowModernHome()
    {
        ModernMediaCenter.ShowHome();
        ApplyMediaLayout();
    }

    // The player header still contains this hidden compatibility action for
    // short-lived legacy player tool panels. Media-center routes never use it.
    private void ReturnToModern_Click(object sender, RoutedEventArgs e)
    {
        ShowModernHome();
    }

    private void ApplyMediaLayout()
    {
        var modernVisible = _viewModel.IsMediaCenterVisible;

        ModernMediaCenter.Visibility = modernVisible ? Visibility.Visible : Visibility.Collapsed;
        LegacyHeaderBorder.Visibility = modernVisible ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Height = new GridLength(modernVisible ? 0 : 76);
        ReturnToModernMediaCenterButton.Visibility = Visibility.Collapsed;

        if (!_viewModel.IsMediaCenterVisible)
        {
            MediaCenterHeaderButton.Visibility = Visibility.Visible;
            PlayerHeaderActions.Visibility = Visibility.Visible;
            MediaCenterDashboard.Visibility = Visibility.Collapsed;
            VideoBorder.Visibility = Visibility.Visible;
            VideoHost.Visibility = Visibility.Visible;
            System.Windows.Controls.Grid.SetColumn(SidebarBorder, 2);
            System.Windows.Controls.Grid.SetColumnSpan(SidebarBorder, 1);
            // Let the command deck measure itself. A fixed height clipped the
            // third control row on common 1080p/DPI-scaled desktop layouts.
            ControlsRow.Height = GridLength.Auto;
            SidebarBorder.Visibility = Visibility.Visible;
            ApplyPlaylistVisibility();
            VideoBorder.Margin = _viewModel.PlaylistVisible ? new Thickness(0, 0, 10, 0) : new Thickness(0);
            return;
        }

        MediaCenterHeaderButton.Visibility = Visibility.Collapsed;
        PlayerHeaderActions.Visibility = Visibility.Collapsed;
        VideoHost.Visibility = Visibility.Hidden;
        MediaCenterDashboard.Visibility = Visibility.Collapsed;
        VideoBorder.Visibility = Visibility.Hidden;
        ControlsRow.Height = new GridLength(0);
        SidebarColumn.Width = new GridLength(0);
        System.Windows.Controls.Grid.SetColumn(SidebarBorder, 0);
        System.Windows.Controls.Grid.SetColumnSpan(SidebarBorder, 3);
        MediaCenterToggleColumn.Width = new GridLength(0);
        SidebarBorder.Visibility = modernVisible ? Visibility.Collapsed : Visibility.Visible;
        MediaCenterEdgeToggleButton.Visibility = Visibility.Collapsed;
        VideoBorder.Margin = new Thickness(0);
    }

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = true;
        var target = UpdateSeekSliderFromPointer(e.GetPosition(SeekSlider));
        _pendingSeekSeconds = target;

        // Commit immediately so a simple click always seeks even when the Slider/Thumb
        // consumes the corresponding mouse-up event. Mouse-up commits once more after a drag.
        _viewModel.SeekTo(target);
        SeekSlider.CaptureMouse();
        e.Handled = true;
    }

    private void SeekSlider_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var point = e.GetPosition(SeekSlider);
        if (_seekDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            _pendingSeekSeconds = UpdateSeekSliderFromPointer(point);
        }

        _ = UpdateSeekPreviewAsync(point);
    }

    private void SeekSlider_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewCts?.Cancel();
        SeekPreviewPopup.IsOpen = false;
    }

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seekDragging) return;
        var target = UpdateSeekSliderFromPointer(e.GetPosition(SeekSlider));
        _pendingSeekSeconds = target;
        _seekDragging = false;
        _viewModel.SeekTo(target);
        _pendingSeekSeconds = null;
        if (SeekSlider.IsMouseCaptured) SeekSlider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SeekSlider_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        if (_pendingSeekSeconds is double target)
        {
            _viewModel.SeekTo(target);
        }
        _pendingSeekSeconds = null;
    }

    private void SeekSlider_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) _viewModel.SeekForwardCommand.Execute(null);
        else _viewModel.SeekBackwardCommand.Execute(null);
        e.Handled = true;
    }

    private double UpdateSeekSliderFromPointer(System.Windows.Point point)
    {
        if (SeekSlider.ActualWidth <= 0 || SeekSlider.Maximum <= SeekSlider.Minimum)
            return Math.Clamp(_viewModel.PositionSeconds, SeekSlider.Minimum, SeekSlider.Maximum);
        var ratio = Math.Clamp(point.X / SeekSlider.ActualWidth, 0, 1);
        var target = SeekSlider.Minimum + ((SeekSlider.Maximum - SeekSlider.Minimum) * ratio);
        SeekSlider.SetCurrentValue(RangeBase.ValueProperty, target);
        return target;
    }

    private async Task UpdateSeekPreviewAsync(System.Windows.Point point)
    {
        var source = _viewModel.CurrentPlaybackSource;
        if (string.IsNullOrWhiteSpace(source) || SeekSlider.ActualWidth <= 0 || SeekSlider.Maximum <= 0)
        {
            SeekPreviewPopup.IsOpen = false;
            return;
        }

        var ratio = Math.Clamp(point.X / SeekSlider.ActualWidth, 0, 1);
        var seconds = ratio * SeekSlider.Maximum;
        SeekPreviewTimeText.Text = FormatPreviewTime(seconds);
        SeekPreviewPopup.HorizontalOffset = Math.Clamp(point.X - 120, 0, Math.Max(0, SeekSlider.ActualWidth - 240));
        SeekPreviewPopup.VerticalOffset = -154;
        SeekPreviewPopup.IsOpen = true;

        var sequence = Interlocked.Increment(ref _previewSequence);
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        try
        {
            await Task.Delay(160, token);
            if (sequence != Volatile.Read(ref _previewSequence)) return;
            if (!await WaitForPreviewReadyAsync(token)) return;
            await _previewEngine.ShowFrameAsync(
                source,
                _viewModel.CurrentPlaybackHeaders,
                seconds,
                token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"Zaman çizelgesi önizlemesi oluşturulamadı: {exception.Message}");
        }
    }

    private async Task<bool> WaitForPreviewReadyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_previewReady) return true;
            await Task.Delay(25, cancellationToken);
        }
        return _previewReady;
    }

    private static string FormatPreviewTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }

    private void TimePill_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ToggleTimeDisplayPrecision();
        e.Handled = true;
    }

    private async void ContinueWatchingCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: ContinueWatchingItem item })
        {
            await _viewModel.PlayContinueWatchingAsync(item);
        }
    }

    private void ContinueWatchingPrevious_OnClick(object sender, RoutedEventArgs e) =>
        ScrollContinueWatching(-320);

    private void ContinueWatchingNext_OnClick(object sender, RoutedEventArgs e) =>
        ScrollContinueWatching(320);

    private void ContinueWatchingScrollViewer_OnPreviewMouseWheel(
        object sender,
        System.Windows.Input.MouseWheelEventArgs e)
    {
        ScrollContinueWatching(e.Delta > 0 ? -220 : 220);
        e.Handled = true;
    }

    private void ScrollContinueWatching(double delta)
    {
        var target = Math.Clamp(
            ContinueWatchingScrollViewer.HorizontalOffset + delta,
            0,
            Math.Max(0, ContinueWatchingScrollViewer.ScrollableWidth));
        ContinueWatchingScrollViewer.ScrollToHorizontalOffset(target);
    }

    private void MediaCenterEdgeToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen || _isMiniPlayer || _viewModel.IsMediaCenterVisible) return;
        _viewModel.PlaylistVisible = !_viewModel.PlaylistVisible;
        ApplyPlaylistVisibility();
    }

    private void ExternalAudioButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: not null } button)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Top;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void ExternalAudioSourceMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string source }) return;
        switch (source)
        {
            case "Local": _viewModel.AttachAudioCommand.Execute(null); break;
            case "Remote": _viewModel.BrowseRemoteAudioCommand.Execute(null); break;
            case "Telegram": _viewModel.BrowseTelegramAudioCommand.Execute(null); break;
        }
    }

    private void FullscreenButton_OnClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void VideoHost_OnPointerActivity(object? sender, EventArgs e)
    {
        if (_isFullscreen)
        {
            ShowFullscreenControls();
        }
    }

    private void Window_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isFullscreen)
        {
            ShowFullscreenControls();
        }
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreen && FullscreenControlsPopup.IsOpen)
        {
            PositionFullscreenControls();
        }
    }

    private void FullscreenControls_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isFullscreen)
        {
            RestartFullscreenControlsTimer();
        }
    }

    private void FullscreenControlsHideTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_isFullscreen)
        {
            HideFullscreenControls();
            return;
        }

        if (FullscreenControlPanel.IsMouseOver || _fullscreenSeekDragging)
        {
            RestartFullscreenControlsTimer();
            return;
        }

        _fullscreenControlsHideTimer.Stop();
        FullscreenControlsPopup.IsOpen = false;
    }

    private void ShowFullscreenControls()
    {
        if (!_isFullscreen || _viewModel.IsMediaCenterVisible)
        {
            return;
        }

        PositionFullscreenControls();
        FullscreenControlsPopup.IsOpen = true;
        RestartFullscreenControlsTimer();
    }

    private void HideFullscreenControls()
    {
        _fullscreenControlsHideTimer.Stop();
        _fullscreenSeekDragging = false;
        if (FullscreenSeekSlider.IsMouseCaptured)
        {
            FullscreenSeekSlider.ReleaseMouseCapture();
        }

        FullscreenControlsPopup.IsOpen = false;
    }

    private void RestartFullscreenControlsTimer()
    {
        _fullscreenControlsHideTimer.Stop();
        _fullscreenControlsHideTimer.Start();
    }

    private void PositionFullscreenControls()
    {
        var availableWidth = Math.Max(1, VideoBorder.ActualWidth);
        var availableHeight = Math.Max(1, VideoBorder.ActualHeight);
        var panelWidth = Math.Max(420, Math.Min(980, availableWidth - 32));

        FullscreenControlPanel.Width = panelWidth;
        FullscreenControlsPopup.HorizontalOffset = Math.Max(0, (availableWidth - panelWidth) / 2);
        FullscreenControlsPopup.VerticalOffset = Math.Max(0, availableHeight - FullscreenControlPanel.Height - 18);
    }

    private void FullscreenSeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;
        _fullscreenSeekDragging = true;
        SeekFromFullscreenSlider(slider, e.GetPosition(slider));
        slider.CaptureMouse();
        RestartFullscreenControlsTimer();
        e.Handled = true;
    }

    private void FullscreenSeekSlider_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_fullscreenSeekDragging || e.LeftButton != MouseButtonState.Pressed || sender is not Slider slider) return;
        SeekFromFullscreenSlider(slider, e.GetPosition(slider));
        RestartFullscreenControlsTimer();
        e.Handled = true;
    }

    private void FullscreenSeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_fullscreenSeekDragging || sender is not Slider slider) return;
        SeekFromFullscreenSlider(slider, e.GetPosition(slider));
        _fullscreenSeekDragging = false;
        if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
        RestartFullscreenControlsTimer();
        e.Handled = true;
    }

    private void SeekFromFullscreenSlider(Slider slider, System.Windows.Point point)
    {
        var width = Math.Max(1, slider.ActualWidth);
        var ratio = Math.Clamp(point.X / width, 0, 1);
        var seconds = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
        _viewModel.SeekTo(seconds);
    }

    private void PlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen || _isMiniPlayer)
        {
            return;
        }

        if (_viewModel.IsMediaCenterVisible)
        {
            ApplyMediaLayout();
            return;
        }

        _viewModel.PlaylistVisible = !_viewModel.PlaylistVisible;
        ApplyPlaylistVisibility();
    }

    private void MiniPlayerButton_OnClick(object sender, RoutedEventArgs e) => ToggleMiniPlayer();

    private void NextMonitorButton_OnClick(object sender, RoutedEventArgs e) => MoveToNextMonitor();

    private async void PlaylistList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await _viewModel.PlaySelectedAsync();
    }

    private async void RecentList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentSourceEntry entry)
        {
            await _viewModel.PlayRecentAsync(entry);
        }
    }

    private async void LibraryList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await _viewModel.Library.PlaySelectedAsync();
    }

    private async void SubtitleResultsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await _viewModel.Subtitles.DownloadAndAttachSelectedAsync();
    }

    private async void RemoteSourcesList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await _viewModel.Sources.OpenSelectedAsync();
    }

    private async void CloudAccountCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: SavedCloudAccount account })
        {
            await _viewModel.Sources.OpenCloudAccountAsync(account);
        }
    }

    private async void CloudAccountRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: SavedCloudAccount account })
        {
            await _viewModel.Sources.DisconnectCloudAsync(account);
        }
    }

    private async void SavedLinkCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: SavedPublicLink link })
        {
            await _viewModel.Sources.OpenSavedLinkAsync(link);
        }
    }

    private async void SavedLinkRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: SavedPublicLink link })
        {
            await _viewModel.Sources.DeleteSavedLinkAsync(link);
        }
    }

    private async void WebDavProfileCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: SavedWebDavProfile profile })
        {
            await _viewModel.Sources.OpenWebDavProfileAsync(profile);
        }
    }

    private async void WebDavProfileRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: SavedWebDavProfile profile })
        {
            await _viewModel.Sources.DeleteWebDavProfileAsync(profile);
        }
    }

    private void VideoBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void VideoBorder_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _viewModel.AdjustVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HandlePlayerKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    private void ComponentDispatcher_OnThreadPreprocessMessage(ref MSG message, ref bool handled)
    {
        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;
        if (handled || !IsActive || _viewModel.IsMediaCenterVisible ||
            (message.message != WmKeyDown && message.message != WmSysKeyDown)) return;

        var key = KeyInterop.KeyFromVirtualKey(message.wParam.ToInt32());
        handled = HandlePlayerKey(key, Keyboard.Modifiers);
    }

    private bool HandlePlayerKey(Key key, ModifierKeys modifiers)
    {
        if (IsTextInputFocused()) return false;

        // Dosya/bağlantı açma kısayolları uygulama güvenli varsayılanı olarak sabit kalır.
        if (modifiers == ModifierKeys.Control && key == Key.O)
        {
            _viewModel.OpenFileCommand.Execute(null);
            return true;
        }
        if (modifiers == ModifierKeys.Control && key == Key.L)
        {
            _viewModel.OpenUrlCommand.Execute(null);
            return true;
        }

        if (_viewModel.MatchesShortcut(PlayerShortcutAction.PlayPause, key, modifiers))
        {
            _viewModel.PlayPauseCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SeekBackward, key, modifiers))
        {
            _viewModel.SeekBackwardCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SeekForward, key, modifiers))
        {
            _viewModel.SeekForwardCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SeekBackwardMedium, key, modifiers))
        {
            _viewModel.SeekBackwardMediumCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SeekForwardMedium, key, modifiers))
        {
            _viewModel.SeekForwardMediumCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SeekBackwardLong, key, modifiers))
        {
            _viewModel.SeekBackwardLongCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SeekForwardLong, key, modifiers))
        {
            _viewModel.SeekForwardLongCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.Screenshot, key, modifiers))
        {
            _viewModel.ScreenshotCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.FrameBackward, key, modifiers))
        {
            _viewModel.FrameBackwardCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.FrameForward, key, modifiers))
        {
            _viewModel.FrameForwardCommand.Execute(null); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.Fullscreen, key, modifiers))
        {
            ToggleFullscreen(); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.MiniPlayer, key, modifiers))
        {
            ToggleMiniPlayer(); return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.Playlist, key, modifiers))
        {
            PlaylistButton_OnClick(this, new RoutedEventArgs()); return true;
        }

        if (modifiers == ModifierKeys.None)
        {
            if (key == Key.Up) { _viewModel.AdjustVolume(5); return true; }
            if (key == Key.Down) { _viewModel.AdjustVolume(-5); return true; }
            if (key == Key.PageUp) { _viewModel.PreviousCommand.Execute(null); return true; }
            if (key == Key.PageDown) { _viewModel.NextCommand.Execute(null); return true; }
            if (key == Key.Escape && _isFullscreen) { ToggleFullscreen(); return true; }
            if (key == Key.Escape && _isMiniPlayer) { ToggleMiniPlayer(); return true; }
        }

        return false;
    }

    private static bool IsTextInputFocused() => Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox or System.Windows.Controls.ComboBox;

    private void Window_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files ||
            files.Length == 0)
        {
            return;
        }

        var mediaSources = new List<string>();
        foreach (var file in files)
        {
            if (Directory.Exists(file))
            {
                mediaSources.Add(file);
                continue;
            }

            var extension = Path.GetExtension(file);
            if (string.Equals(extension, ".torrent", StringComparison.OrdinalIgnoreCase))
            {
                await _viewModel.Torrent.OpenTorrentFileAsync(file);
            }
            else if (SubtitleExtensions.Contains(extension))
            {
                _viewModel.AttachSubtitlePath(file);
            }
            else if (AudioExtensions.Contains(extension))
            {
                _viewModel.AttachAudioPath(file);
            }
            else
            {
                mediaSources.Add(file);
            }
        }

        if (mediaSources.Count > 0)
        {
            await _viewModel.OpenFilesAsync(mediaSources, replacePlaylist: false);
        }
    }

    private void ToggleFullscreen()
    {
        if (_isMiniPlayer)
        {
            ToggleMiniPlayer();
        }

        if (!_isFullscreen)
        {
            SaveWindowPresentationState();
            var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);

            HeaderRow.Height = new GridLength(0);
            ControlsRow.Height = new GridLength(0);
            MediaCenterToggleColumn.Width = new GridLength(0);
            SidebarColumn.Width = new GridLength(0);
            SidebarBorder.Visibility = Visibility.Collapsed;

            // v1.0.1 true fullscreen: remove both the player page inset and the
            // decorative blue viewport stroke. The old layout left a visible
            // 16/14 px frame even though the top-level HWND filled the monitor.
            PlayerContentGrid.Margin = new Thickness(0);
            VideoBorder.Margin = new Thickness(0);
            VideoBorder.BorderThickness = new Thickness(0);
            VideoBorder.CornerRadius = new CornerRadius(0);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;

            SetWindowPosition(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
            _isFullscreen = true;
            _ = Dispatcher.BeginInvoke(new Action(ShowFullscreenControls), DispatcherPriority.Loaded);
        }
        else
        {
            HideFullscreenControls();
            RestoreWindowPresentationState();
            _isFullscreen = false;
            ApplyPlaylistVisibility();
        }
    }

    private void ToggleMiniPlayer()
    {
        if (_isFullscreen)
        {
            ToggleFullscreen();
        }

        if (!_isMiniPlayer)
        {
            SaveWindowPresentationState();
            HeaderRow.Height = new GridLength(0);
            MediaCenterToggleColumn.Width = new GridLength(0);
            SidebarColumn.Width = new GridLength(0);
            SidebarBorder.Visibility = Visibility.Collapsed;
            ControlsRow.Height = new GridLength(174);
            VideoBorder.Margin = new Thickness(0, 0, 0, 0);
            VideoBorder.CornerRadius = new CornerRadius(8);
            WindowState = WindowState.Normal;
            Width = 760;
            Height = 520;
            MinWidth = 640;
            MinHeight = 420;
            _isMiniPlayer = true;
        }
        else
        {
            MinWidth = 1180;
            MinHeight = 600;
            RestoreWindowPresentationState();
            _isMiniPlayer = false;
            ApplyPlaylistVisibility();
        }
    }

    private void SaveWindowPresentationState()
    {
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousWindowState = WindowState;
        _previousBounds = RestoreBounds;
        _previousHeaderHeight = HeaderRow.Height;
        _previousControlsHeight = ControlsRow.Height;
        _previousSidebarWidth = SidebarColumn.Width;
        _previousPlayerContentMargin = PlayerContentGrid.Margin;
        _previousVideoBorderThickness = VideoBorder.BorderThickness;
        _previousVideoMargin = VideoBorder.Margin;
        _previousVideoCornerRadius = VideoBorder.CornerRadius;
    }

    private void RestoreWindowPresentationState()
    {
        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        HeaderRow.Height = _previousHeaderHeight;
        ControlsRow.Height = _previousControlsHeight;
        SidebarColumn.Width = _previousSidebarWidth;
        MediaCenterToggleColumn.Width = new GridLength(30);
        PlayerContentGrid.Margin = _previousPlayerContentMargin;
        VideoBorder.Margin = _previousVideoMargin;
        VideoBorder.BorderThickness = _previousVideoBorderThickness;
        VideoBorder.CornerRadius = _previousVideoCornerRadius;
        WindowState = WindowState.Normal;
        Left = _previousBounds.Left;
        Top = _previousBounds.Top;
        Width = Math.Max(MinWidth, _previousBounds.Width);
        Height = Math.Max(MinHeight, _previousBounds.Height);
        WindowState = _previousWindowState;
    }

    private void ApplyPlaylistVisibility()
    {
        if (_isFullscreen || _isMiniPlayer)
        {
            MediaCenterToggleColumn.Width = new GridLength(0);
            MediaCenterEdgeToggleButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (_viewModel.IsMediaCenterVisible)
        {
            System.Windows.Controls.Grid.SetColumn(SidebarBorder, 0);
            System.Windows.Controls.Grid.SetColumnSpan(SidebarBorder, 3);
            MediaCenterToggleColumn.Width = new GridLength(0);
            SidebarColumn.Width = new GridLength(0);
            SidebarBorder.Visibility = Visibility.Visible;
            VideoHost.Visibility = Visibility.Hidden;
            VideoBorder.Visibility = Visibility.Hidden;
            MediaCenterEdgeToggleButton.Visibility = Visibility.Collapsed;
            VideoBorder.Margin = new Thickness(0);
            return;
        }

        System.Windows.Controls.Grid.SetColumn(SidebarBorder, 2);
        System.Windows.Controls.Grid.SetColumnSpan(SidebarBorder, 1);
        MediaCenterToggleColumn.Width = new GridLength(30);
        SidebarColumn.Width = _viewModel.PlaylistVisible ? new GridLength(460) : new GridLength(0);
        SidebarBorder.Visibility = _viewModel.PlaylistVisible ? Visibility.Visible : Visibility.Collapsed;
        VideoHost.Visibility = Visibility.Visible;
        VideoBorder.Visibility = Visibility.Visible;
        MediaCenterEdgeToggleButton.Visibility = Visibility.Visible;
        MediaCenterEdgeToggleButton.Content = _viewModel.PlaylistVisible ? "›" : "‹";
        MediaCenterEdgeToggleButton.ToolTip = Localization.LocalizationManager.Get(
            _viewModel.PlaylistVisible ? "Player.MediaCenter.Close" : "Player.MediaCenter.Open");
        VideoBorder.Margin = new Thickness(0);
    }

    private void MoveToNextMonitor()
    {
        var helper = new WindowInteropHelper(this);
        var screens = Forms.Screen.AllScreens;
        if (screens.Length < 2)
        {
            return;
        }

        var current = Forms.Screen.FromHandle(helper.Handle);
        var currentIndex = Array.IndexOf(screens, current);
        var next = screens[(currentIndex + 1 + screens.Length) % screens.Length];

        if (_isFullscreen)
        {
            SetWindowPosition(next.Bounds.Left, next.Bounds.Top, next.Bounds.Width, next.Bounds.Height);
            return;
        }

        WindowState = WindowState.Normal;
        var width = (int)Math.Min(ActualWidth, next.WorkingArea.Width);
        var height = (int)Math.Min(ActualHeight, next.WorkingArea.Height);
        var left = next.WorkingArea.Left + Math.Max(0, (next.WorkingArea.Width - width) / 2);
        var top = next.WorkingArea.Top + Math.Max(0, (next.WorkingArea.Height - height) / 2);
        SetWindowPosition(left, top, width, height);
    }

    private void SetWindowPosition(int left, int top, int width, int height)
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, nint.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    private async void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingCompleted)
        {
            return;
        }

        // async Closing sırasında ikinci bir Close çağrısı aynı WPF kapanış
        // çevrimine girerse Window.VerifyNotClosing InvalidOperationException
        // üretir. İlk kapanış temizliği bitene kadar diğer talepleri iptal et.
        if (_closingStarted)
        {
            e.Cancel = true;
            return;
        }

        _closingStarted = true;
        e.Cancel = true;
        IsEnabled = false;

        try
        {
            ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_OnThreadPreprocessMessage;
            _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            VideoHost.PointerActivity -= VideoHost_OnPointerActivity;
            _fullscreenControlsHideTimer.Stop();
            _fullscreenControlsHideTimer.Tick -= FullscreenControlsHideTimer_OnTick;
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            await _previewEngine.DisposeAsync();
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Uygulama kapatılırken hata oluştu.", exception);
        }
        finally
        {
            _closingCompleted = true;

            // Mevcut Closing event'i tamamen stack'ten çıktıktan sonra ikinci
            // ve son Close talebini çalıştır. Bu çağrıda _closingCompleted=true
            // olduğu için event iptal edilmeden normal kapanış gerçekleşir.
            _ = Dispatcher.BeginInvoke(
                new Action(Close),
                DispatcherPriority.ApplicationIdle);
        }
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
