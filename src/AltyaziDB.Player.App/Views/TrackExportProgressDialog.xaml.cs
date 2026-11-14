using System.ComponentModel;
using System.Windows;
using AltyaziDB.Player.App.Localization;

namespace AltyaziDB.Player.App.Views;

/// <summary>Modal async FFmpeg progress view with process-safe cancellation.</summary>
public partial class TrackExportProgressDialog : Window
{
    private readonly Func<IProgress<double>, CancellationToken, Task> _export;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly bool _hasDuration;
    private bool _started;
    private bool _finished;

    public bool Success { get; private set; }
    public bool WasCancelled { get; private set; }
    public Exception? Error { get; private set; }

    public TrackExportProgressDialog(
        bool audio, bool hasDuration,
        Func<IProgress<double>, CancellationToken, Task> export)
    {
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _hasDuration = hasDuration;
        InitializeComponent();
        StateText.Text = LocalizationManager.Get(audio
            ? "Player.TrackExport.ProgressAudio"
            : "Player.TrackExport.ProgressSubtitle");
        PercentText.Text = LocalizationManager.Get("Player.TrackExport.ProgressUnknown");
        Loaded += Dialog_OnLoaded;
        Closing += Dialog_OnClosing;
    }

    private async void Dialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        var progress = new Progress<double>(percent =>
        {
            if (_finished || Success || !_hasDuration || _cancellation.IsCancellationRequested) return;
            ExportProgress.IsIndeterminate = false;
            ExportProgress.Value = Math.Clamp(percent, 0, 100);
            PercentText.Text = $"{Math.Floor(percent):0}%";
        });
        try
        {
            await _export(progress, _cancellation.Token);
            Success = true;
            ExportProgress.IsIndeterminate = false;
            ExportProgress.Value = 100;
            PercentText.Text = "100%";
            StateText.Text = LocalizationManager.Get("Player.TrackExport.ProgressDone");
            CancelButton.IsEnabled = false;
            await Task.Delay(950);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            WasCancelled = true;
        }
        catch (Exception exception)
        {
            Error = exception;
        }
        finally
        {
            _finished = true;
            _cancellation.Dispose();
            Close();
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => RequestCancel();

    private void Dialog_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished) return;
        e.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        CancelButton.IsEnabled = false;
        StateText.Text = LocalizationManager.Get("Player.TrackExport.ProgressCancelling");
    }
}
