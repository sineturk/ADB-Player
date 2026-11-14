using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.App.Views;
using AltyaziDB.Player.App.Localization;
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
    private bool _trackExportBusy;
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
    private bool _miniSeekDragging;
    private double _previousMinWidth;
    private double _previousMinHeight;
    private bool _previousTopmost;
    private readonly DispatcherTimer _fullscreenControlsHideTimer;
    private readonly DispatcherTimer _miniControlsHideTimer;
    private readonly DispatcherTimer _syncFeedbackHideTimer;
    private readonly DispatcherTimer _focusedActionsHideTimer;
    private bool _focusedPlaybackActive;
    private bool? _previousMediaCenterVisible;
    private readonly DispatcherTimer _overlayControlsHideTimer;
    private bool _overlayDeckDetached;
    private bool _switchingDeckMode;
    private bool _playbackDeckSuspended;
    private double _volumeBeforeMute = 75;
    private bool _videoCursorHidden;
    private bool _lastAudioSurfaceVisible;
    private bool _lastAudioMeterPlaying;

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
            Interval = TimeSpan.FromSeconds(1.7)
        };
        _fullscreenControlsHideTimer.Tick += FullscreenControlsHideTimer_OnTick;
        _miniControlsHideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1.8)
        };
        _miniControlsHideTimer.Tick += MiniControlsHideTimer_OnTick;
        _syncFeedbackHideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _syncFeedbackHideTimer.Tick += SyncFeedbackHideTimer_OnTick;
        _focusedActionsHideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(2.6)
        };
        _focusedActionsHideTimer.Tick += FocusedActionsHideTimer_OnTick;
        _overlayControlsHideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1.8)
        };
        _overlayControlsHideTimer.Tick += OverlayControlsHideTimer_OnTick;
        OverlayPlaybackControlsPopup.DataContext = viewModel;
        FullscreenControlsPopup.DataContext = viewModel;
        FocusedPlaybackActionsPopup.DataContext = viewModel;
        PlaybackCenterSheet.DataContext = viewModel;
        DualSubtitleCenterSheet.DataContext = viewModel;
        MiniPlayerHeaderPopup.DataContext = viewModel;
        MiniPlayerControlsPopup.DataContext = viewModel;

        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        VideoHost.HandleCreated += VideoHost_OnHandleCreated;
        VideoHost.PointerActivity += VideoHost_OnPointerActivity;
        PreviewVideoHost.HandleCreated += PreviewVideoHost_OnHandleCreated;
        Closing += Window_OnClosing;
        ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_OnThreadPreprocessMessage;
        Loaded += Window_OnLoaded;
        ApplyMediaLayout();
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        // v1.0.2: keep the first window fully inside the active monitor's
        // working area. A fixed 1500x860 window could otherwise open partly
        // above the desktop on smaller or DPI-scaled displays.
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var screen = Forms.Screen.FromHandle(handle);
            var source = PresentationSource.FromVisual(this);
            var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;

            var topLeft = fromDevice.Transform(new System.Windows.Point(
                screen.WorkingArea.Left,
                screen.WorkingArea.Top));
            var bottomRight = fromDevice.Transform(new System.Windows.Point(
                screen.WorkingArea.Right,
                screen.WorkingArea.Bottom));

            var workArea = new Rect(
                topLeft,
                bottomRight);

            var requestedWidth = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
            var requestedHeight = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;

            if (requestedWidth > workArea.Width || requestedHeight > workArea.Height)
            {
                WindowState = WindowState.Maximized;
                _logger.Info(
                    $"Başlangıç penceresi çalışma alanından büyük olduğu için büyütüldü: " +
                    $"{workArea.Width:0}x{workArea.Height:0} DIP");
                return;
            }

            Left = workArea.Left + Math.Max(0, (workArea.Width - requestedWidth) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - requestedHeight) / 2);
        }
        catch (Exception exception)
        {
            _logger.Warning($"Başlangıç pencere konumu doğrulanamadı: {exception.Message}");
        }
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
        else if (e.PropertyName == nameof(MainViewModel.HasMedia))
        {
            var shouldFocus = !_viewModel.IsMediaCenterVisible && _viewModel.HasMedia;
            if (shouldFocus != _focusedPlaybackActive)
                UpdatePlaybackFocusChrome();
            else
                UpdatePlaybackDeckMode();
            UpdateAudioOnlySurface();
        }
        else if (e.PropertyName is nameof(MainViewModel.IsAudioOnly)
                 or nameof(MainViewModel.IsAudioOnlyPlaying))
        {
            UpdateAudioOnlySurface();
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
        var previousMediaCenterVisible = _previousMediaCenterVisible;
        _previousMediaCenterVisible = modernVisible;

        ModernMediaCenter.Visibility = modernVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlaybackFocusChrome();
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
            UpdatePlaybackDeckMode();
            UpdateAudioOnlySurface();
            if (previousMediaCenterVisible == true && !_overlayDeckDetached)
                AnimateShellEntrance(PlaybackControlDeck);
            return;
        }

        MediaCenterHeaderButton.Visibility = Visibility.Collapsed;
        PlayerHeaderActions.Visibility = Visibility.Collapsed;
        VideoHost.Visibility = Visibility.Hidden;
        AudioOnlySurface.Visibility = Visibility.Collapsed;
        AnimateAudioMeter(false);
        _lastAudioSurfaceVisible = false;
        _lastAudioMeterPlaying = false;
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
        if (previousMediaCenterVisible == false)
            AnimateShellEntrance(ModernMediaCenter);
    }

    // Subtle 180 ms transitions are limited to WPF chrome. Never fade, blur or
    // transform the native MpvVideoHost — it lives in a separate HwndHost.
    private static bool GlassMotionEnabled => SystemParameters.MenuAnimation;

    private void AnimateShellEntrance(FrameworkElement target)
    {
        if (!GlassMotionEnabled || _isFullscreen || _isMiniPlayer) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!target.IsVisible) return;
            target.Opacity = 1;
            target.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        }), DispatcherPriority.Loaded);
    }

    private void GlassPopup_OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup || popup.Child is not FrameworkElement surface)
            return;

        // Honor the Windows reduced-motion / menu-animation setting.
        // PopupAnimation handles dismissal; the animation below affects just
        // the translucent WPF surface during its first opening frame.
        if (!GlassMotionEnabled)
        {
            surface.Opacity = 1;
            return;
        }

        var isCenterSheet = popup == PlaybackCenterSheet || popup == DualSubtitleCenterSheet;
        var duration = TimeSpan.FromMilliseconds(isCenterSheet ? 220 : 160);

        surface.Opacity = 1;
        surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });

        // Scale *inside* the popup bounds to avoid clipping native WPF popup
        // surfaces. Only center sheets scale; seek/volume HUD hit areas remain
        // unchanged while their opacity fades in.
        if (isCenterSheet)
        {
            surface.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            var scale = surface.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
            surface.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        }
    }

    private void UpdatePlaybackFocusChrome()
    {
        if (_isFullscreen || _isMiniPlayer) return;

        // The app shell is useful in the media center or without a loaded
        // movie. During playback, give the video its full vertical space.
        var modern = _viewModel.IsMediaCenterVisible;
        var focused = !modern && _viewModel.HasMedia;
        _focusedPlaybackActive = focused;
        var showHeader = !modern && !focused;

        var headerHeight = new GridLength(showHeader ? 76 : 0);
        if (HeaderRow.Height != headerHeight)
            HeaderRow.Height = headerHeight;

        var headerVisibility = showHeader ? Visibility.Visible : Visibility.Collapsed;
        if (LegacyHeaderBorder.Visibility != headerVisibility)
            LegacyHeaderBorder.Visibility = headerVisibility;

        var margin = focused ? new Thickness(0) : new Thickness(8, 6, 8, 0);
        if (PlayerContentGrid.Margin != margin)
            PlayerContentGrid.Margin = margin;

        // Avoid a decorative frame around the moving picture.
        VideoBorder.BorderThickness = focused ? new Thickness(0) : new Thickness(1);
        VideoBorder.CornerRadius = focused ? new CornerRadius(0) : new CornerRadius(9);

        if (!focused)
        {
            HideFocusedPlaybackActions();
            CloseCenterSheets();
        }

        UpdatePlaybackDeckMode();
    }

    // R4: the *same* interactive playback deck is docked in the ordinary Grid
    // or hosted in its own Popup HWND above libmpv. Never duplicate bindings,
    // commands, sliders or their interaction state.
    private void UpdatePlaybackDeckMode()
    {
        if (_switchingDeckMode || _playbackDeckSuspended) return;
        if (_isFullscreen || _isMiniPlayer)
        {
            HideOverlayPlaybackControls();
            return;
        }

        // A playlist can narrow the video below a usable three-row toolbar.
        // Fall back to the full-width dock rather than clipping CC² and tracks.
        var shouldOverlay = _focusedPlaybackActive &&
            VideoBorder.ActualWidth >= 940 &&
            !_viewModel.IsMediaCenterVisible;

        if (shouldOverlay == _overlayDeckDetached)
        {
            PlaybackControlDeck.Background = (System.Windows.Media.Brush)FindResource(shouldOverlay
                ? "PlayerOverlayGlassBrush" : "PlayerGlassShellBrush");
            OverlayControlsPinToggle.Visibility = shouldOverlay
                ? Visibility.Visible : Visibility.Collapsed;
            ControlsRow.Height = shouldOverlay ? new GridLength(0) :
                _viewModel.IsMediaCenterVisible ? new GridLength(0) : GridLength.Auto;
            if (shouldOverlay && OverlayPlaybackControlsPopup.IsOpen)
                PositionOverlayPlaybackControls();
            return;
        }

        _switchingDeckMode = true;
        try
        {
            HideOverlayPlaybackControls();
            DismissDeckTransientPopups();

            PlaybackControlDeck.Background = (System.Windows.Media.Brush)FindResource(shouldOverlay
                ? "PlayerOverlayGlassBrush" : "PlayerGlassShellBrush");
            OverlayControlsPinToggle.Visibility = shouldOverlay
                ? Visibility.Visible : Visibility.Collapsed;

            if (shouldOverlay)
            {
                if (!RootLayoutGrid.Children.Contains(PlaybackControlDeck))
                    throw new InvalidOperationException("Playback deck dock parent is missing.");

                RootLayoutGrid.Children.Remove(PlaybackControlDeck);
                OverlayPlaybackControlsPopup.Child = PlaybackControlDeck;
                _overlayDeckDetached = true;
                ControlsRow.Height = new GridLength(0);
            }
            else
            {
                OverlayPlaybackControlsPopup.Child = null;
                Grid.SetRow(PlaybackControlDeck, 2);
                RootLayoutGrid.Children.Add(PlaybackControlDeck);
                _overlayDeckDetached = false;
                ControlsRow.Height = _viewModel.IsMediaCenterVisible
                    ? new GridLength(0) : GridLength.Auto;
            }
        }
        finally
        {
            _switchingDeckMode = false;
        }
    }

    // R4.1.1: the normal transport deck must have NO visual parent while a
    // dedicated fullscreen/mini player HUD is active. Closing the R4 Popup
    // alone was not sufficient: its child could remain displayed as a second
    // bottom bar in the compact window.
    private void SuspendNormalPlaybackDeck()
    {
        if (_playbackDeckSuspended) return;

        HideOverlayPlaybackControls();
        DismissDeckTransientPopups();
        PlaybackControlDeck.Visibility = Visibility.Collapsed;

        if (OverlayPlaybackControlsPopup.Child == PlaybackControlDeck)
            OverlayPlaybackControlsPopup.Child = null;

        if (RootLayoutGrid.Children.Contains(PlaybackControlDeck))
            RootLayoutGrid.Children.Remove(PlaybackControlDeck);

        _overlayDeckDetached = false;
        _playbackDeckSuspended = true;
        ControlsRow.Height = new GridLength(0);
    }

    private void RestoreNormalPlaybackDeck()
    {
        if (!_playbackDeckSuspended) return;

        // Restore a single normal parent, then let the R4 width-aware layout
        // decide whether that parent should be dock or floating Popup.
        if (OverlayPlaybackControlsPopup.Child == PlaybackControlDeck)
            OverlayPlaybackControlsPopup.Child = null;

        if (!RootLayoutGrid.Children.Contains(PlaybackControlDeck))
        {
            Grid.SetRow(PlaybackControlDeck, 2);
            RootLayoutGrid.Children.Add(PlaybackControlDeck);
        }

        _overlayDeckDetached = false;
        _playbackDeckSuspended = false;
        PlaybackControlDeck.Visibility = Visibility.Visible;
        UpdatePlaybackDeckMode();
    }

    private void DismissDeckTransientPopups()
    {
        // Moving an open Popup's PlacementTarget tears down its WPF HWND.
        // Close transient children before detaching/re-attaching the deck.
        SeekPreviewPopup.IsOpen = false;
        SyncControlsToggle.IsChecked = false;
        SubtitleSearchToggle.IsChecked = false;
        AudioSyncDetailsToggle.IsChecked = false;
        CloseCenterSheets();
        _seekDragging = false;
        if (SeekSlider.IsMouseCaptured)
            SeekSlider.ReleaseMouseCapture();
    }

    private void PositionOverlayPlaybackControls()
    {
        if (!_overlayDeckDetached) return;

        var videoWidth = Math.Max(1, VideoBorder.ActualWidth);
        var videoHeight = Math.Max(1, VideoBorder.ActualHeight);
        // The Border has 8 px left/right margins outside its explicit Width.
        var contentWidth = Math.Max(0, videoWidth - 36);
        PlaybackControlDeck.Width = contentWidth;
        OverlayPlaybackControlsPopup.HorizontalOffset = Math.Max(0, (videoWidth - contentWidth - 16) / 2);
        var deckHeight = Math.Max(148, PlaybackControlDeck.ActualHeight);
        OverlayPlaybackControlsPopup.VerticalOffset = Math.Max(0, videoHeight - deckHeight - 14);
    }

    private void ShowOverlayPlaybackControls(bool force = false)
    {
        if (!_overlayDeckDetached || !_focusedPlaybackActive || _isFullscreen ||
            _isMiniPlayer || _viewModel.IsMediaCenterVisible ||
            IsCenterSheetOpen)
            return;

        if (!force)
        {
            // PreviewMouseMove also fires over window chrome and the playlist.
            // Open the bottom overlay only for the actual video viewport.
            var pointer = Forms.Cursor.Position;
            System.Windows.Point point;
            try
            {
                point = VideoBorder.PointFromScreen(new System.Windows.Point(pointer.X, pointer.Y));
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (point.X < 0 || point.X > VideoBorder.ActualWidth ||
                point.Y < 0 || point.Y > VideoBorder.ActualHeight)
                return;
        }

        PositionOverlayPlaybackControls();
        OverlayPlaybackControlsPopup.IsOpen = true;
        _overlayControlsHideTimer.Stop();
        if (OverlayControlsPinToggle.IsChecked != true)
            _overlayControlsHideTimer.Start();
    }

    private void OverlayPlaybackControls_OnOpened(object? sender, EventArgs e)
    {
        PositionOverlayPlaybackControls();
        GlassPopup_OnOpened(sender, e);
    }

    private void PlaybackControlDeck_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_overlayDeckDetached && OverlayPlaybackControlsPopup.IsOpen)
            PositionOverlayPlaybackControls();
    }

    private void OverlayControlsPin_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_overlayDeckDetached || !OverlayPlaybackControlsPopup.IsOpen)
            return;

        _overlayControlsHideTimer.Stop();
        if (OverlayControlsPinToggle.IsChecked != true)
            _overlayControlsHideTimer.Start();
    }

    private void OverlayControlsHideTimer_OnTick(object? sender, EventArgs e)
    {
        if (OverlayControlsPinToggle.IsChecked == true)
        {
            _overlayControlsHideTimer.Stop();
            return;
        }

        if (IsCenterSheetOpen)
        {
            HideOverlayPlaybackControls();
            return;
        }

        if (PlaybackControlDeck.IsMouseOver ||
            PlaybackSpeedComboBox.IsDropDownOpen ||
            PlaybackAudioTrackComboBox.IsDropDownOpen ||
            PlaybackSubtitleTrackComboBox.IsDropDownOpen ||
            ExternalAudioButton.ContextMenu?.IsOpen == true ||
            PlaybackSettingsToggle.IsChecked == true || SubtitleSettingsToggle.IsChecked == true ||
            SyncControlsToggle.IsChecked == true || SubtitleSearchToggle.IsChecked == true ||
            AudioSyncDetailsToggle.IsChecked == true || _seekDragging ||
            SeekPreviewPopup.IsOpen)
        {
            _overlayControlsHideTimer.Stop();
            _overlayControlsHideTimer.Start();
            return;
        }

        HideOverlayPlaybackControls();
        HideVideoCursorWhenIdle();
    }

    private void HideOverlayPlaybackControls()
    {
        _overlayControlsHideTimer.Stop();
        OverlayPlaybackControlsPopup.IsOpen = false;
    }

    private bool IsCenterSheetOpen =>
        PlaybackSettingsToggle?.IsChecked == true || SubtitleSettingsToggle?.IsChecked == true;

    private void UpdateCenterSheetChrome()
    {
        if (IsCenterSheetOpen)
        {
            // The controls may be pinned: sheets always take visual priority.
            HideOverlayPlaybackControls();
            HideFocusedPlaybackActions();
            if (!_overlayDeckDetached)
                PlaybackControlDeck.Visibility = Visibility.Collapsed;
            return;
        }

        PlaybackControlDeck.Visibility = Visibility.Visible;
        // Do not immediately redisplay the overlay. The next pointer activity
        // or F6 will reveal it naturally.
    }

    private void CloseCenterSheets()
    {
        if (PlaybackSettingsToggle?.IsChecked == true)
            PlaybackSettingsToggle.IsChecked = false;
        if (SubtitleSettingsToggle?.IsChecked == true)
            SubtitleSettingsToggle.IsChecked = false;
        UpdateCenterSheetChrome();
    }

    private void CenterSheetToggle_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender == PlaybackSettingsToggle)
            SubtitleSettingsToggle.IsChecked = false;
        else if (sender == SubtitleSettingsToggle)
            PlaybackSettingsToggle.IsChecked = false;

        SyncControlsToggle.IsChecked = false;
        // Adapt center sheets to the currently available viewport height.
        PositionCenterSheets();
        UpdateCenterSheetChrome();
    }

    private void CenterSheetToggle_OnUnchecked(object sender, RoutedEventArgs e)
    {
        UpdateCenterSheetChrome();
    }

    private void CenterSheetClose_OnClick(object sender, RoutedEventArgs e) => CloseCenterSheets();

    private void CenterSheet_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseCenterSheets();
        e.Handled = true;
    }

    private void PositionCenterSheets()
    {
        var viewHeight = Math.Max(1, PlayerContentGrid.ActualHeight);
        PlaybackSettingsSheetPanel.MaxHeight = Math.Min(660, Math.Max(240, viewHeight - 26));
        DualSubtitleSheetPanel.MaxHeight = Math.Min(560, Math.Max(220, viewHeight - 26));
    }

    private void MaybeShowFocusedPlaybackActions()
    {
        if (!_focusedPlaybackActive || _isFullscreen || _isMiniPlayer ||
            PlaybackSettingsToggle.IsChecked == true || SubtitleSettingsToggle.IsChecked == true)
            return;

        // PointerActivity comes from libmpv's native HwndHost, so use screen
        // coordinates rather than relying on WPF MouseMove routing.
        var cursor = Forms.Cursor.Position;
        System.Windows.Point point;
        try
        {
            point = VideoBorder.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (point.X < 0 || point.X > VideoBorder.ActualWidth ||
            point.Y < 0 || point.Y > 74)
            return;

        ShowFocusedPlaybackActions();
    }

    private void ShowFocusedPlaybackActions()
    {
        var videoWidth = Math.Max(1, VideoBorder.ActualWidth);
        var width = Math.Min(580, Math.Max(320, videoWidth - 20));
        FocusedPlaybackActionPanel.Width = width;
        FocusedPlaybackActionsPopup.HorizontalOffset = Math.Max(0, videoWidth - width - 12);
        FocusedPlaybackActionsPopup.VerticalOffset = 12;
        FocusedPlaybackActionsPopup.IsOpen = true;
        RestartFocusedActionsTimer();
    }

    private void FocusedPlaybackActions_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        => RestartFocusedActionsTimer();

    private void RestartFocusedActionsTimer()
    {
        _focusedActionsHideTimer.Stop();
        _focusedActionsHideTimer.Start();
    }

    private void FocusedActionsHideTimer_OnTick(object? sender, EventArgs e)
    {
        if (FocusedPlaybackActionPanel.IsMouseOver)
        {
            RestartFocusedActionsTimer();
            return;
        }
        HideFocusedPlaybackActions();
    }

    private void HideFocusedPlaybackActions()
    {
        _focusedActionsHideTimer.Stop();
        FocusedPlaybackActionsPopup.IsOpen = false;
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

    private void VideoLayoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: not null } button) return;
        RevealVideoCursor();
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Top;
        button.ContextMenu.IsOpen = true;
    }

    private void VideoLayoutOption_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string mode })
            _viewModel.SetVideoFillMode(mode.Equals("fill", StringComparison.OrdinalIgnoreCase));
    }

    private void RevealVideoCursor()
    {
        if (!_videoCursorHidden) return;
        Forms.Cursor.Show();
        _videoCursorHidden = false;
    }

    private void HideVideoCursorWhenIdle()
    {
        if (_videoCursorHidden || _viewModel.IsMediaCenterVisible || !_viewModel.HasMedia ||
            PlaybackSettingsToggle.IsChecked == true || SubtitleSettingsToggle.IsChecked == true ||
            PlaybackControlDeck.IsMouseOver || FullscreenControlPanel.IsMouseOver ||
            MiniPlayerControlPanel.IsMouseOver || _seekDragging || _fullscreenSeekDragging || _miniSeekDragging)
            return;
        // libmpv uses an HwndHost. Balance WinForms cursor Hide/Show to avoid
        // leaving the system pointer invisible after switching windows.
        try
        {
            var p = Forms.Cursor.Position;
            var point = VideoBorder.PointFromScreen(new System.Windows.Point(p.X, p.Y));
            if (point.X >= 0 && point.X <= VideoBorder.ActualWidth &&
                point.Y >= 0 && point.Y <= VideoBorder.ActualHeight)
            {
                Forms.Cursor.Hide();
                _videoCursorHidden = true;
            }
        }
        catch (InvalidOperationException) { }
    }

    private void FullscreenButton_OnClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void VideoHost_OnPointerActivity(object? sender, EventArgs e)
    {
        RevealVideoCursor();
        if (_isFullscreen)
        {
            ShowFullscreenControls();
        }
        else if (_isMiniPlayer)
        {
            ShowMiniPlayerControls();
        }
        else
        {
            MaybeShowFocusedPlaybackActions();
            ShowOverlayPlaybackControls();
        }
    }

    private void Window_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        RevealVideoCursor();
        if (_isFullscreen)
        {
            ShowFullscreenControls();
        }
        else if (_isMiniPlayer)
        {
            ShowMiniPlayerControls();
        }
        else
        {
            MaybeShowFocusedPlaybackActions();
            ShowOverlayPlaybackControls();
        }
    }

    private void Window_OnDeactivated(object? sender, EventArgs e)
    {
        RevealVideoCursor();
        if (_isFullscreen && !FullscreenControlPanel.IsMouseOver)
            HideFullscreenControls();
        HideFocusedPlaybackActions();
        HideOverlayPlaybackControls();
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreen && FullscreenControlsPopup.IsOpen)
        {
            PositionFullscreenControls();
        }
        if (_isMiniPlayer && MiniPlayerControlsPopup.IsOpen)
        {
            PositionMiniPlayerControls();
        }

        // The sync HUD is anchored to the video, including when the window is
        // resized or transitions between regular and fullscreen presentation.
        if (SyncFeedbackPopup.IsOpen)
        {
            PositionSyncFeedback();
        }
        if (FocusedPlaybackActionsPopup.IsOpen)
            ShowFocusedPlaybackActions();
        if (PlaybackCenterSheet.IsOpen || DualSubtitleCenterSheet.IsOpen)
            PositionCenterSheets();
        UpdatePlaybackDeckMode();
    }

    private void VideoBorder_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Sidebar show/hide can resize the video without resizing MainWindow.
        if (SyncFeedbackPopup?.IsOpen == true)
        {
            PositionSyncFeedback();
        }
        if (_isFullscreen && FullscreenControlsPopup?.IsOpen == true)
        {
            PositionFullscreenControls();
        }
        if (_isMiniPlayer && MiniPlayerControlsPopup?.IsOpen == true)
        {
            PositionMiniPlayerControls();
        }
        if (FocusedPlaybackActionsPopup?.IsOpen == true)
            ShowFocusedPlaybackActions();
        UpdatePlaybackDeckMode();
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
        HideVideoCursorWhenIdle();
    }

    // A separate HWND Popup is necessary: WPF elements inside VideoBorder are
    // hidden by libmpv's native HwndHost. Repeated keypresses refresh this OSD.
    private void SyncFeedbackHideTimer_OnTick(object? sender, EventArgs e)
    {
        _syncFeedbackHideTimer.Stop();
        SyncFeedbackPopup.IsOpen = false;
    }

    private void PositionSyncFeedback()
    {
        // Popup is a separate HWND above libmpv. Relative placement uses video
        // coordinates; retain a safe inset from the upper/right video edges.
        const double toastWidth = 214;
        const double inset = 18;
        SyncFeedbackPopup.HorizontalOffset = Math.Max(0, VideoBorder.ActualWidth - toastWidth - inset);
        SyncFeedbackPopup.VerticalOffset = inset;
        // Tiny PiP windows have too little horizontal space for both upper
        // overlays. Lower the toast when necessary to avoid the restore bar.
        if (_isMiniPlayer && VideoBorder.ActualWidth < 440)
            SyncFeedbackPopup.VerticalOffset = 58;
    }

    private void ShowSyncFeedback(string titleKey, double? seconds)
    {
        if (_viewModel.IsMediaCenterVisible) return;

        SyncFeedbackTitle.Text = Localization.LocalizationManager.Get(titleKey);
        if (seconds.HasValue)
        {
            var milliseconds = (int)Math.Round(seconds.Value * 1000, MidpointRounding.AwayFromZero);
            SyncFeedbackValue.Text = $"{milliseconds:+0;-0;0} ms";
            SyncFeedbackValue.Visibility = Visibility.Visible;
        }
        else
        {
            SyncFeedbackValue.Text = string.Empty;
            SyncFeedbackValue.Visibility = Visibility.Collapsed;
        }

        PositionSyncFeedback();
        // Keep one HUD open while the user presses Z/X repeatedly; only update
        // its text and restart the auto-dismiss timer to avoid flicker.
        SyncFeedbackPopup.IsOpen = true;
        _syncFeedbackHideTimer.Stop();
        _syncFeedbackHideTimer.Start();
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
        // Avoid overflow on small displays: the HUD must always fit the video.
        var panelWidth = Math.Min(920, Math.Max(280, availableWidth - 24));

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

    private void ShowMiniPlayerControls()
    {
        if (!_isMiniPlayer || _viewModel.IsMediaCenterVisible) return;
        PositionMiniPlayerControls();
        MiniPlayerHeaderPopup.IsOpen = true;
        MiniPlayerControlsPopup.IsOpen = true;
        RestartMiniControlsTimer();
    }

    private void MiniPlayerControls_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isMiniPlayer) RestartMiniControlsTimer();
    }

    private void RestartMiniControlsTimer()
    {
        _miniControlsHideTimer.Stop();
        _miniControlsHideTimer.Start();
    }

    private void MiniControlsHideTimer_OnTick(object? sender, EventArgs e)
    {
        // The header and transport are one visual HUD: keep both visible while
        // the pointer is using either area or a mini seek drag is in progress.
        if (_isMiniPlayer &&
            (MiniPlayerControlPanel.IsMouseOver ||
             MiniPlayerHeaderPanel.IsMouseOver ||
             _miniSeekDragging))
        {
            RestartMiniControlsTimer();
            return;
        }

        _miniControlsHideTimer.Stop();
        MiniPlayerControlsPopup.IsOpen = false;
        MiniPlayerHeaderPopup.IsOpen = false;
        HideVideoCursorWhenIdle();
        // Moving over the native video reveals both again. M / Esc can still
        // restore the normal window when both overlays are hidden.
    }

    private void PositionMiniPlayerControls()
    {
        var availableWidth = Math.Max(1, VideoBorder.ActualWidth);
        var availableHeight = Math.Max(1, VideoBorder.ActualHeight);
        var controlsWidth = Math.Min(420, Math.Max(250, availableWidth - 16));
        MiniPlayerControlPanel.Width = controlsWidth;
        MiniPlayerControlsPopup.HorizontalOffset = Math.Max(0, (availableWidth - controlsWidth) / 2);
        MiniPlayerControlsPopup.VerticalOffset = Math.Max(0, availableHeight - MiniPlayerControlPanel.Height - 10);
        // Place the drag/restore affordance upper-left; the sync toast keeps
        // its existing upper-right position without covering Mini controls.
        MiniPlayerHeaderPopup.HorizontalOffset = 10;
        MiniPlayerHeaderPopup.VerticalOffset = 10;
    }

    private void HideMiniPlayerControls()
    {
        _miniControlsHideTimer.Stop();
        _miniSeekDragging = false;
        if (MiniPlayerSeekSlider.IsMouseCaptured)
            MiniPlayerSeekSlider.ReleaseMouseCapture();
        MiniPlayerControlsPopup.IsOpen = false;
        MiniPlayerHeaderPopup.IsOpen = false;
    }

    private void MiniPlayerRestore_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isMiniPlayer) ToggleMiniPlayer();
    }

    private void MiniPlayerHeader_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isMiniPlayer || e.LeftButton != MouseButtonState.Pressed ||
            e.GetPosition(MiniPlayerHeaderPanel).X >= MiniPlayerHeaderPanel.ActualWidth - 49)
            return;

        // The mini header lives in a separate Popup HWND above libmpv.
        // Delegate dragging to the actual owner HWND, not the Popup surface.
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, WmNcLeftButtonDown,
            (nint)HtCaption, nint.Zero);
        e.Handled = true;
    }

    private void MiniPlayerSeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isMiniPlayer || sender is not Slider slider) return;
        _miniSeekDragging = true;
        SeekFromFullscreenSlider(slider, e.GetPosition(slider));
        slider.CaptureMouse();
        e.Handled = true;
    }

    private void MiniPlayerSeekSlider_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_miniSeekDragging || e.LeftButton != MouseButtonState.Pressed || sender is not Slider slider) return;
        SeekFromFullscreenSlider(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private void MiniPlayerSeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_miniSeekDragging || sender is not Slider slider) return;
        SeekFromFullscreenSlider(slider, e.GetPosition(slider));
        _miniSeekDragging = false;
        if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MiniPlayerSeekSlider_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _miniSeekDragging = false;
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
        // ESC dismisses a focused center sheet before it is interpreted as a
        // player/window shortcut. The same behavior is wired inside the Popup.
        if (e.Key == Key.Escape &&
            (PlaybackSettingsToggle.IsChecked == true || SubtitleSettingsToggle.IsChecked == true))
        {
            CloseCenterSheets();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && OverlayPlaybackControlsPopup.IsOpen &&
            !_isFullscreen && !_isMiniPlayer)
        {
            HideOverlayPlaybackControls();
            e.Handled = true;
            return;
        }

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

        // F6 gives keyboard-only users direct access to the auto-hidden deck.
        if (key == Key.F6 && modifiers == ModifierKeys.None && _overlayDeckDetached &&
            !_isFullscreen && !_isMiniPlayer)
        {
            if (OverlayPlaybackControlsPopup.IsOpen)
                HideOverlayPlaybackControls();
            else
                ShowOverlayPlaybackControls(force: true);
            return true;
        }

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
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SubtitleDelayDecrease, key, modifiers))
        {
            if (_viewModel.CanAdjustSubtitleDelay)
            {
                _viewModel.PrimarySubtitleDelayDecreaseCommand.Execute(null);
                ShowSyncFeedback("Player.Sync.Feedback.Subtitle", _viewModel.PrimarySubtitleDelaySeconds);
            }
            else ShowSyncFeedback("Player.Sync.Feedback.NoSubtitle", null);
            return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.SubtitleDelayIncrease, key, modifiers))
        {
            if (_viewModel.CanAdjustSubtitleDelay)
            {
                _viewModel.PrimarySubtitleDelayIncreaseCommand.Execute(null);
                ShowSyncFeedback("Player.Sync.Feedback.Subtitle", _viewModel.PrimarySubtitleDelaySeconds);
            }
            else ShowSyncFeedback("Player.Sync.Feedback.NoSubtitle", null);
            return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.AudioDelayDecrease, key, modifiers))
        {
            if (_viewModel.HasExternalAudio)
            {
                _viewModel.AudioDelayDecreaseCommand.Execute(null);
                ShowSyncFeedback("Player.Sync.Feedback.Audio", _viewModel.ManualAudioDelaySeconds);
            }
            else ShowSyncFeedback("Player.Sync.Feedback.NoAudio", null);
            return true;
        }
        if (_viewModel.MatchesShortcut(PlayerShortcutAction.AudioDelayIncrease, key, modifiers))
        {
            if (_viewModel.HasExternalAudio)
            {
                _viewModel.AudioDelayIncreaseCommand.Execute(null);
                ShowSyncFeedback("Player.Sync.Feedback.Audio", _viewModel.ManualAudioDelaySeconds);
            }
            else ShowSyncFeedback("Player.Sync.Feedback.NoAudio", null);
            return true;
        }

        // Additional shortcuts share the editable ADB Player key map; core
        // mappings above take precedence when an assignment overlaps.
        if (_viewModel.HasMedia && !IsCenterSheetOpen &&
            HandleConfiguredExtraShortcut(key, modifiers))
            return true;

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

    // R4.1.1: all additional shortcuts are read from the same persisted,
    // editable map as the core player keys. "None" disables an action.
    private bool HandleConfiguredExtraShortcut(Key key, ModifierKeys modifiers)
    {
        bool Matches(PlayerShortcutAction action) => _viewModel.MatchesShortcut(action, key, modifiers);

        if (Matches(PlayerShortcutAction.SpeedIncrease)) { StepPlaybackSpeed(1); return true; }
        if (Matches(PlayerShortcutAction.SpeedDecrease)) { StepPlaybackSpeed(-1); return true; }
        if (Matches(PlayerShortcutAction.SpeedReset)) { _viewModel.SelectedSpeed = 1.0; return true; }
        if (Matches(PlayerShortcutAction.NextMedia))
        {
            if (_viewModel.NextCommand.CanExecute(null)) _viewModel.NextCommand.Execute(null);
            return true;
        }
        if (Matches(PlayerShortcutAction.CycleAudioTrack)) { CycleAudioTrack(); return true; }
        if (Matches(PlayerShortcutAction.CycleSubtitleTrack)) { CycleSubtitleTrack(); return true; }
        if (Matches(PlayerShortcutAction.Mute)) { ToggleShortcutMute(); return true; }
        if (Matches(PlayerShortcutAction.VolumeIncrease)) { _viewModel.AdjustVolume(5); return true; }
        if (Matches(PlayerShortcutAction.VolumeDecrease)) { _viewModel.AdjustVolume(-5); return true; }
        if (Matches(PlayerShortcutAction.ScreenshotAlternate))
        {
            if (_viewModel.ScreenshotCommand.CanExecute(null))
                _viewModel.ScreenshotCommand.Execute(null);
            return true;
        }

        if (Matches(PlayerShortcutAction.SubtitleDelayDecreaseAlternate) ||
            Matches(PlayerShortcutAction.SubtitleDelayIncreaseAlternate))
        {
            var decrease = Matches(PlayerShortcutAction.SubtitleDelayDecreaseAlternate);
            if (_viewModel.CanAdjustSubtitleDelay)
            {
                var command = decrease ? _viewModel.PrimarySubtitleDelayDecreaseCommand :
                    _viewModel.PrimarySubtitleDelayIncreaseCommand;
                if (command.CanExecute(null)) command.Execute(null);
                ShowSyncFeedback("Player.Sync.Feedback.Subtitle", _viewModel.PrimarySubtitleDelaySeconds);
            }
            else ShowSyncFeedback("Player.Sync.Feedback.NoSubtitle", null);
            return true;
        }

        if (Matches(PlayerShortcutAction.AudioDelayDecreaseAlternate) ||
            Matches(PlayerShortcutAction.AudioDelayIncreaseAlternate))
        {
            var decrease = Matches(PlayerShortcutAction.AudioDelayDecreaseAlternate);
            if (_viewModel.HasExternalAudio)
            {
                var command = decrease ? _viewModel.AudioDelayDecreaseCommand :
                    _viewModel.AudioDelayIncreaseCommand;
                if (command.CanExecute(null)) command.Execute(null);
                ShowSyncFeedback("Player.Sync.Feedback.Audio", _viewModel.ManualAudioDelaySeconds);
            }
            else ShowSyncFeedback("Player.Sync.Feedback.NoAudio", null);
            return true;
        }
        return false;
    }

    private void StepPlaybackSpeed(int direction)
    {
        // Use existing options, not arbitrary 1.1x steps: the speed ComboBox
        // still displays the selected value instead of becoming blank.
        var options = _viewModel.SpeedOptions;
        var current = _viewModel.SelectedSpeed;
        var next = direction > 0
            ? options.FirstOrDefault(speed => speed > current + 0.001, options[^1])
            : options.LastOrDefault(speed => speed < current - 0.001, options[0]);
        _viewModel.SelectedSpeed = next;
    }

    private void ToggleShortcutMute()
    {
        if (_viewModel.Volume > 0.01)
        {
            _volumeBeforeMute = _viewModel.Volume;
            _viewModel.Volume = 0;
        }
        else
        {
            _viewModel.Volume = Math.Clamp(_volumeBeforeMute, 1, _viewModel.VolumeBoostLimit);
        }
    }

    private void ExportEmbeddedAudio_Click(object sender, RoutedEventArgs e) =>
        _ = ExportEmbeddedTrackFromSettingsAsync(TrackType.Audio);

    private void ExportEmbeddedSubtitle_Click(object sender, RoutedEventArgs e) =>
        _ = ExportEmbeddedTrackFromSettingsAsync(TrackType.Subtitle);

    private async Task ExportEmbeddedTrackFromSettingsAsync(TrackType type)
    {
        if (_trackExportBusy) return;
        var currentSource = _viewModel.CurrentPlaybackSource;
        // P4.16.8.1 release safeguard: remote SRT export created empty files
        // on real MKV streams. Keep local track exports only until verified.
        if (string.IsNullOrWhiteSpace(currentSource) || !File.Exists(currentSource))
        {
            ShowTrackExportMessage(LocalizationManager.Get("Player.TrackExport.RemoteNotSupported"), false);
            return;
        }
        var audio = type == TrackType.Audio;
        var all = audio ? _viewModel.AudioTracks : _viewModel.SubtitleTracks;
        var selected = audio ? _viewModel.SelectedAudioTrack : _viewModel.SelectedSubtitleTrack;
        if (selected is null || selected.IsDisabled || selected.IsExternal)
        {
            ShowTrackExportMessage(LocalizationManager.Get("Player.TrackExport.ChooseEmbedded"), false);
            return;
        }
        if (!audio && !EmbeddedTrackExportService.CanExportTextSubtitle(selected))
        {
            ShowTrackExportMessage(LocalizationManager.Get("Player.TrackExport.BitmapUnsupported"), false);
            return;
        }
        var ordinal = all.Where(track => !track.IsExternal && !track.IsDisabled)
            .ToList().FindIndex(track => track.Id == selected.Id);
        if (ordinal < 0)
        {
            ShowTrackExportMessage(LocalizationManager.Get("Player.TrackExport.ChooseEmbedded"), false);
            return;
        }

        var ext = audio ? EmbeddedTrackExportService.AudioExtension(selected) : ".srt";
        var basename = Uri.TryCreate(currentSource, UriKind.Absolute, out var playbackUri) &&
                       playbackUri.Scheme is "http" or "https"
            ? Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(playbackUri.AbsolutePath))
            : Path.GetFileNameWithoutExtension(currentSource);
        if (string.IsNullOrWhiteSpace(basename)) basename = "ADB-Player";
        var language = string.IsNullOrWhiteSpace(selected.Language) ? "und" : selected.Language.Trim();
        var proposed = basename + "." + language + (audio ? ".audio" : ".subtitle") + ext;
        proposed = new string(proposed.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .Take(160).ToArray());
        if (!proposed.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            proposed = "track-" + ordinal + ext;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationManager.Get("Player.TrackExport.Title"),
            FileName = proposed,
            DefaultExt = ext,
            Filter = audio
                ? $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}|{LocalizationManager.Get("Player.TrackExport.AllFiles")}|*.*"
                : "SRT|*.srt",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (File.Exists(dialog.FileName))
        {
            ShowTrackExportMessage(LocalizationManager.Get("Player.TrackExport.Exists"), false);
            return;
        }

        _trackExportBusy = true;
        try
        {
            var durationSeconds = _viewModel.Snapshot.DurationSeconds;
            var progressDialog = new TrackExportProgressDialog(
                audio,
                double.IsFinite(durationSeconds) && durationSeconds > 0,
                (progress, ct) => EmbeddedTrackExportService.ExportAsync(
                    currentSource, dialog.FileName, type, ordinal,
                    progress, durationSeconds, ct))
            {
                Owner = this
            };
            progressDialog.ShowDialog();
            if (progressDialog.WasCancelled) return;
            if (progressDialog.Error is not null) throw progressDialog.Error;
            if (!progressDialog.Success)
                throw new InvalidOperationException(LocalizationManager.Get("Player.TrackExport.Failed"));
        }
        catch (Exception exception)
        {
            _logger.Warning($"Medya parçası dışa aktarılamadı: {exception.GetType().Name}");
            var message = exception is InvalidOperationException or FileNotFoundException
                ? exception.Message
                : LocalizationManager.Get("Player.TrackExport.Failed");
            ShowTrackExportMessage(message, false);
        }
        finally
        {
            _trackExportBusy = false;
        }
    }

    private void ShowTrackExportMessage(string message, bool success)
    {
        var dialog = new AppDialog(
            LocalizationManager.Get("Player.TrackExport.Title"),
            message,
            success ? AppDialogKind.Info : AppDialogKind.Warning);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void CycleAudioTrack()
    {
        var tracks = _viewModel.AudioTracks;
        if (tracks.Count == 0) return;
        var current = _viewModel.SelectedAudioTrack;
        var index = current is null ? -1 : tracks.IndexOf(current);
        _viewModel.SelectedAudioTrack = tracks[(index + 1) % tracks.Count];
    }

    private void CycleSubtitleTrack()
    {
        var tracks = _viewModel.SubtitleTracks;
        if (tracks.Count == 0) return;
        var current = _viewModel.SelectedSubtitleTrack;
        var index = current is null ? -1 : tracks.IndexOf(current);
        _viewModel.SelectedSubtitleTrack = tracks[(index + 1) % tracks.Count];
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
        HideFocusedPlaybackActions();
        HideOverlayPlaybackControls();
        CloseCenterSheets();
        if (_isMiniPlayer)
        {
            ToggleMiniPlayer();
        }

        if (!_isFullscreen)
        {
            SaveWindowPresentationState();
            SuspendNormalPlaybackDeck();
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
            RestoreNormalPlaybackDeck();
            ApplyPlaylistVisibility();
            UpdatePlaybackFocusChrome();
        }
    }

    private void ToggleMiniPlayer()
    {
        if (!_isMiniPlayer && (_viewModel.IsMediaCenterVisible || !_viewModel.HasMedia))
            return;

        HideFocusedPlaybackActions();
        HideOverlayPlaybackControls();
        CloseCenterSheets();
        if (_isFullscreen)
            ToggleFullscreen();

        if (!_isMiniPlayer)
        {
            SaveWindowPresentationState();
            SuspendNormalPlaybackDeck();
            var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var composition = PresentationSource.FromVisual(this)?.CompositionTarget;
            var fromDevice = composition?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var workArea = new Rect(
                fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top)),
                fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom)));

            // PiP viewport: no large desktop toolbar, no decorative video frame.
            // All essential buttons live in separate, libmpv-safe Popup HWNDs.
            HeaderRow.Height = new GridLength(0);
            ControlsRow.Height = new GridLength(0);
            MediaCenterToggleColumn.Width = new GridLength(0);
            SidebarColumn.Width = new GridLength(0);
            SidebarBorder.Visibility = Visibility.Collapsed;
            MediaCenterEdgeToggleButton.Visibility = Visibility.Collapsed;
            PlayerContentGrid.Margin = new Thickness(0);
            VideoBorder.Margin = new Thickness(0);
            VideoBorder.BorderThickness = new Thickness(0);
            VideoBorder.CornerRadius = new CornerRadius(0);

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            MinWidth = Math.Min(320, Math.Max(1, workArea.Width - 12));
            MinHeight = Math.Min(220, Math.Max(1, workArea.Height - 12));
            Width = Math.Max(MinWidth, Math.Min(520, workArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(324, workArea.Height - 24));
            Left = Math.Max(workArea.Left, workArea.Right - Width - 20);
            Top = Math.Max(workArea.Top, workArea.Bottom - Height - 20);
            SetCurrentValue(TopmostProperty, true);

            _isMiniPlayer = true;
            _ = Dispatcher.BeginInvoke(new Action(ShowMiniPlayerControls), DispatcherPriority.Loaded);
        }
        else
        {
            HideMiniPlayerControls();
            _isMiniPlayer = false;
            RestoreWindowPresentationState();
            RestoreNormalPlaybackDeck();
            ApplyPlaylistVisibility();
            UpdatePlaybackFocusChrome();
        }
    }

    private void SaveWindowPresentationState()
    {
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousWindowState = WindowState;
        _previousBounds = RestoreBounds;
        _previousMinWidth = MinWidth;
        _previousMinHeight = MinHeight;
        _previousTopmost = Topmost;
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
        MinWidth = _previousMinWidth;
        MinHeight = _previousMinHeight;
        SetCurrentValue(TopmostProperty, _previousTopmost);
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
        UpdateAudioOnlySurface();
        MediaCenterEdgeToggleButton.Visibility = Visibility.Visible;
        MediaCenterEdgeToggleButton.Content = _viewModel.PlaylistVisible ? "›" : "‹";
        MediaCenterEdgeToggleButton.ToolTip = Localization.LocalizationManager.Get(
            _viewModel.PlaylistVisible ? "Player.MediaCenter.Close" : "Player.MediaCenter.Open");
        VideoBorder.Margin = new Thickness(0);
    }

    private void UpdateAudioOnlySurface()
    {
        var visible = !_viewModel.IsMediaCenterVisible && _viewModel.IsAudioOnly;
        // The mpv host is an HWND: covering it with WPF visuals is not enough
        // (airspace issue). Keep the host alive but hide its native child only
        // for audio-only playback, then restore it for the next video.
        AudioOnlySurface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!_viewModel.IsMediaCenterVisible)
            VideoHost.Visibility = visible ? Visibility.Hidden : Visibility.Visible;

        var playing = visible && _viewModel.IsAudioOnlyPlaying && GlassMotionEnabled;
        if (visible != _lastAudioSurfaceVisible || playing != _lastAudioMeterPlaying)
        {
            _lastAudioSurfaceVisible = visible;
            _lastAudioMeterPlaying = playing;
            AnimateAudioMeter(playing);
        }
    }

    private void AnimateAudioMeter(bool playing)
    {
        var bars = new[] { AudioMeterBar1, AudioMeterBar2, AudioMeterBar3, AudioMeterBar4 };
        for (var index = 0; index < bars.Length; index++)
        {
            if (bars[index].RenderTransform is not ScaleTransform scale) continue;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (!playing)
            {
                scale.ScaleY = 0.36 + (index % 3) * 0.12;
                continue;
            }

            // Deliberate ambient motion, not audio sample capture or an FFT.
            var duration = TimeSpan.FromMilliseconds(740 + index * 160);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(
                0.22 + (index % 2) * 0.1, 0.75 + (index % 3) * 0.08, duration)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
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
            RevealVideoCursor();
            ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_OnThreadPreprocessMessage;
            _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            VideoHost.PointerActivity -= VideoHost_OnPointerActivity;
            _fullscreenControlsHideTimer.Stop();
            _fullscreenControlsHideTimer.Tick -= FullscreenControlsHideTimer_OnTick;
            HideFullscreenControls();
            HideMiniPlayerControls();
            _miniControlsHideTimer.Tick -= MiniControlsHideTimer_OnTick;
            HideFocusedPlaybackActions();
            _focusedActionsHideTimer.Tick -= FocusedActionsHideTimer_OnTick;
            HideOverlayPlaybackControls();
            _overlayControlsHideTimer.Tick -= OverlayControlsHideTimer_OnTick;
            CloseCenterSheets();
            _syncFeedbackHideTimer.Stop();
            _syncFeedbackHideTimer.Tick -= SyncFeedbackHideTimer_OnTick;
            SyncFeedbackPopup.IsOpen = false;
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

    private const uint WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

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
