using System.IO;
using System.Windows.Input;
using AltyaziDB.Player.App.Commands;
using AltyaziDB.Player.App.Services;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Infrastructure;

namespace AltyaziDB.Player.App.ViewModels;

/// <summary>
/// Only owns rclone/cache. rclone/runtime contains the live OAuth config, while
/// remote-cache may include attached audio/subtitle files: never clean either.
/// This service does not enable rclone VFS disk cache or media downloads.
/// </summary>
public sealed class CloudCachePanelViewModel : ObservableObject
{
    private static readonly TimeSpan AutomaticGrace = TimeSpan.FromDays(1);
    private static readonly TimeSpan ManualGrace = TimeSpan.FromMinutes(15);
    private readonly PlayerSettings _settings;
    private readonly AppPaths _paths;
    private readonly Func<Task> _saveSettings;
    private readonly Func<bool> _mediaActive;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private string _usageLabel = "Bulut önbelleği ölçülüyor…";
    private string _status = string.Empty;

    public CloudCachePanelViewModel(
        PlayerSettings settings, AppPaths paths, Func<Task> saveSettings,
        Func<bool> mediaActive, IUserDialogService dialogs, IAppLogger logger)
    {
        _settings = settings;
        _paths = paths;
        _saveSettings = saveSettings;
        _mediaActive = mediaActive;
        _dialogs = dialogs;
        _logger = logger;
        RefreshCommand = new AsyncRelayCommand(RefreshUsageAsync);
        ClearCommand = new AsyncRelayCommand(ClearAsync);
        OpenCommand = new RelayCommand(OpenDirectory);
        _ = InitializeAsync();
    }

    public IReadOnlyList<int> LimitOptions { get; } = [1, 2, 5, 10];

    public int LimitGiB
    {
        get => _settings.CloudCacheLimitGiB is 1 or 2 or 5 or 10
            ? _settings.CloudCacheLimitGiB : 2;
        set
        {
            if (_settings.CloudCacheLimitGiB == value || !LimitOptions.Contains(value)) return;
            _settings.CloudCacheLimitGiB = value;
            OnPropertyChanged();
            _ = SaveAndApplyAsync();
        }
    }

    public bool AutoCleanup
    {
        get => _settings.CloudCacheAutoCleanup;
        set
        {
            if (_settings.CloudCacheAutoCleanup == value) return;
            _settings.CloudCacheAutoCleanup = value;
            OnPropertyChanged();
            _ = SaveAndApplyAsync();
        }
    }

    public string DirectoryDisplay => string.IsNullOrWhiteSpace(_settings.CloudCacheDirectory)
        ? Path.Combine(_paths.RootDirectory, "rclone", "cache")
        : Path.Combine(_settings.CloudCacheDirectory, "ADB Player Cloud Cache");
    public string ExtraFilesDirectory => _paths.RemoteCacheDirectory;
    public async Task SetDirectoryAsync(string selectedFolder)
    {
        if (_mediaActive())
        {
            Status = "Oynatma sırasında önbellek konumu değiştirilemez.";
            return;
        }
        if (!AppPaths.TryPrepareCloudCacheDirectory(selectedFolder, out _))
        {
            Status = "Geçersiz veya korumalı klasör. Boş, erişilebilir bir klasör seçin.";
            return;
        }
        _settings.CloudCacheDirectory = Path.GetFullPath(selectedFolder);
        OnPropertyChanged(nameof(DirectoryDisplay));
        await _saveSettings().ConfigureAwait(true);
        Status = "Yeni bulut önbelleği konumu uygulama yeniden açıldığında kullanılacak. Eski dosyalar otomatik taşınmaz.";
    }

    public async Task ResetDirectoryAsync()
    {
        if (_mediaActive())
        {
            Status = "Oynatma sırasında önbellek konumu değiştirilemez.";
            return;
        }
        _settings.CloudCacheDirectory = string.Empty;
        OnPropertyChanged(nameof(DirectoryDisplay));
        await _saveSettings().ConfigureAwait(true);
        Status = "Varsayılan konum, uygulama yeniden başlatıldığında kullanılacak.";
    }
    public string UsageLabel { get => _usageLabel; private set => SetProperty(ref _usageLabel, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenCommand { get; }

    private async Task InitializeAsync()
    {
        try
        {
            if (AutoCleanup) await CleanupAsync(automatic: true).ConfigureAwait(true);
            else await RefreshUsageAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Bulut önbelleği başlatılamadı: {ex.GetType().Name}");
        }
    }

    private async Task SaveAndApplyAsync()
    {
        try
        {
            await _saveSettings().ConfigureAwait(true);
            if (AutoCleanup) await CleanupAsync(automatic: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Bulut önbelleği ayarları güncellenemedi: {ex.GetType().Name}");
        }
    }

    public async Task RefreshUsageAsync()
    {
        if (!await _operationGate.WaitAsync(0).ConfigureAwait(true)) return;
        try
        {
            var usage = await Task.Run(() => ScanSafeFiles().Sum(x => x.Length)).ConfigureAwait(true);
            UsageLabel = $"rclone geçici dosyaları: {SizeLabel(usage)}";
        }
        catch (Exception ex)
        {
            Status = "Önbellek klasörü okunamadı.";
            _logger.Warning($"Bulut önbelleği ölçümü başarısız: {ex.GetType().Name}");
        }
        finally { _operationGate.Release(); }
    }

    public async Task ClearAsync()
    {
        if (_mediaActive())
        {
            Status = "Oynatma devam ederken önbellek silinmez. Videoyu kapatıp tekrar deneyin.";
            return;
        }
        if (!_dialogs.Confirm(
            "Yalnız rclone/cache içindeki eski geçici dosyalar temizlensin mi? " +
            "OAuth oturumları, indirmeler ve eklenmiş ses/altyazı dosyaları korunur."))
            return;
        await CleanupAsync(automatic: false).ConfigureAwait(true);
    }

    private async Task CleanupAsync(bool automatic)
    {
        // Do not prune any cache while media is active, even if an automatic
        // run is requested. Remote rclone workers can still hold open handles.
        if (_mediaActive()) return;
        if (!await _operationGate.WaitAsync(0).ConfigureAwait(true)) return;
        try
        {
            var before = await Task.Run(() => ScanSafeFiles()).ConfigureAwait(true);
            var maximumBytes = (long)LimitGiB * 1024 * 1024 * 1024;
            var usage = before.Sum(x => x.Length);
            var cutoff = DateTime.UtcNow - (automatic ? AutomaticGrace : ManualGrace);
            var removedBytes = 0L;
            var removedCount = 0;
            foreach (var file in before.OrderBy(x => x.LastWriteTimeUtc))
            {
                if (_mediaActive()) break;
                if (automatic && usage <= maximumBytes) break;
                if (file.LastWriteTimeUtc >= cutoff) continue;
                try
                {
                    // Do not follow junctions/reparse points or touch nested
                    // OAuth/runtime/config folders. Deleted files are only
                    // regular files directly under the known rclone/cache tree.
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var length = file.Length;
                    file.Delete();
                    usage -= length;
                    removedBytes += length;
                    removedCount++;
                }
                catch (IOException) { /* in-use file: skip */ }
                catch (UnauthorizedAccessException) { /* locked file: skip */ }
            }
            UsageLabel = $"rclone geçici dosyaları: {SizeLabel(Math.Max(0, usage))}";
            if (!automatic)
                Status = $"{removedCount} eski geçici dosya temizlendi ({SizeLabel(removedBytes)}).";
            _logger.Info($"Bulut önbelleği denetlendi: mode={(automatic ? "auto" : "manual")}; removed={removedCount}.");
        }
        catch (Exception ex)
        {
            Status = "Önbellek temizliği tamamlanamadı. Diğer kullanıcı dosyaları korunuyor.";
            _logger.Warning($"Bulut önbelleği temizleme hatası: {ex.GetType().Name}");
        }
        finally { _operationGate.Release(); }
    }

    private List<FileInfo> ScanSafeFiles()
    {
        var root = new DirectoryInfo(_paths.RcloneCacheDirectory);
        if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            return [];
        var files = new List<FileInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        var seen = 0;
        while (stack.Count > 0 && seen < 100000)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var child in dir.EnumerateFileSystemInfos())
                {
                    if (++seen > 100000) break;
                    try
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (child is DirectoryInfo sub) stack.Push(sub);
                        else if (child is FileInfo f &&
                                 !string.Equals(f.Name, ".adb-player-cache-owner", StringComparison.OrdinalIgnoreCase))
                            files.Add(f);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }

    private void OpenDirectory()
    {
        try
        {
            Directory.CreateDirectory(_paths.RcloneCacheDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", _paths.RcloneCacheDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Önbellek klasörü açılamadı.";
            _logger.Warning($"Bulut önbellek klasörü açılamadı: {ex.GetType().Name}");
        }
    }

    private static string SizeLabel(long bytes)
    {
        var size = (double)Math.Max(0L, bytes);
        var units = new[] { "B", "KB", "MB", "GB" };
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.##} {units[index]}";
    }
}
