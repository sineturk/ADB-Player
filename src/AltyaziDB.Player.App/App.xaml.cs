using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AltyaziDB.Player.Addons.Services;
using AltyaziDB.Player.AudioSync.Services;
using AltyaziDB.Player.Connections.Services;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.App.ViewModels;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Parsing;
using AltyaziDB.Player.Cloud;
using AltyaziDB.Player.Infrastructure;
using AltyaziDB.Player.Library;
using AltyaziDB.Player.Playback;
using AltyaziDB.Player.Subtitles;
using AltyaziDB.Player.SubtitleSync.Services;
using AltyaziDB.Player.Sources;
using AltyaziDB.Player.Telegram;
using AltyaziDB.Player.Torrent;

namespace AltyaziDB.Player.App;

public partial class App : System.Windows.Application
{
    private IAppLogger? _logger;
    private CrashReportService? _crashReports;
    // A fatal layout error may recur while a modal MessageBox pumps Dispatcher events.
    // Never open a second crash dialog (or generate dozens of duplicate crash files).
    private int _fatalDispatcherErrorShown;
    private IDisposable? _updateServiceDisposable;
    private IDisposable? _cloudAccountDisposable;
    private IDisposable? _metadataServiceDisposable;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();
        var logger = new FileAppLogger(paths);
        var crashReports = new CrashReportService(paths, logger);
        _logger = logger;
        _crashReports = crashReports;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var settings = new JsonSettingsService(paths, logger);
        var startupSettings = settings.LoadAsync().GetAwaiter().GetResult();
        LocalizationManager.ApplyLanguage(startupSettings.UiLanguage);
        var parser = new ReleaseParser();
        var mediaLibrary = new SqliteMediaLibrary(paths, logger, parser);
        var playback = new MpvPlaybackEngine(logger);
        var preview = new MpvPreviewEngine(logger);
        var audioSync = new FfmpegAudioSyncService(logger);
        var subtitles = new AltyaziDbSubtitleService(logger);
        var subtitleSync = new FfsubsyncSubtitleSyncService(logger);
        var secrets = new DpapiSecretStore(paths, logger);
        var sources = new RemoteSourceService(logger, secrets);
        var telegram = new TdJsonTelegramService(logger);
        var torrent = new MonoTorrentStreamingService(logger);
        var updates = new HttpUpdateService(logger);
        _updateServiceDisposable = updates;
        var cloudAccount = new NeonCloudAccountService(secrets, logger);
        _cloudAccountDisposable = cloudAccount;
        var metadata = new AltyaziDbPlayerMetadataService(cloudAccount, logger);
        _metadataServiceDisposable = metadata;
        var cloud = new RcloneCloudService(logger, secrets, paths);
        var connections = new ExternalConnectionService(paths.ConnectionRegistryFile, secrets, logger);
        var addons = new AddonCatalogService(paths.AddonRegistryFile, secrets, logger);
        var dialogs = new WpfDialogService();

        logger.Info($"ADB Player v{updates.CurrentVersion.ToString(4)} başlatılıyor. Dağıtım: {(paths.IsPortable ? "Portable" : "Kurulu/Geliştirici")}");
        var viewModel = new MainViewModel(
            playback,
            audioSync,
            subtitleSync,
            settings,
            mediaLibrary,
            logger,
            dialogs,
            paths,
            mediaLibrary,
            subtitles,
            sources,
            cloud,
            telegram,
            torrent,
            updates,
            secrets,
            cloudAccount,
            connections,
            addons,
            parser,
            crashReports,
            metadata);
        var startupSource = e.Args.FirstOrDefault();
        var window = new MainWindow(viewModel, preview, logger, startupSource);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _metadataServiceDisposable?.Dispose();
        _updateServiceDisposable?.Dispose();
        _cloudAccountDisposable?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The same invalid WPF binding can throw on every render cycle. A modal
        // MessageBox runs a nested Dispatcher loop, so re-entry must be blocked
        // before reporting or showing any UI.
        e.Handled = true;
        if (Interlocked.Exchange(ref _fatalDispatcherErrorShown, 1) != 0)
        {
            return;
        }

        try
        {
            var report = _crashReports?.Write(e.Exception, "WPF Dispatcher") ?? string.Empty;
            _logger?.Error("İşlenmeyen WPF hatası.", e.Exception);

            // Stop the failed visual tree from rendering again during the dialog.
            MainWindow?.Hide();
            System.Windows.MessageBox.Show(
                LocalizationManager.Get("Dialog.CrashMessage") +
                (string.IsNullOrWhiteSpace(report) ? string.Empty :
                    $"{Environment.NewLine}{Environment.NewLine}{LocalizationManager.Get("Dialog.Report")}: {report}"),
                "ADB Player",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Shutdown(-1);
        }
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Bilinmeyen hata");
        _crashReports?.Write(exception, "AppDomain");
        _logger?.Error("İşlenmeyen AppDomain hatası.", exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _crashReports?.Write(e.Exception, "TaskScheduler");
        _logger?.Error("Gözlemlenmeyen görev hatası.", e.Exception);
        e.SetObserved();
    }
}
