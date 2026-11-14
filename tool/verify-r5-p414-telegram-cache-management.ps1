param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relativePath) {
    $file = Join-Path $root $relativePath
    if (!(Test-Path -LiteralPath $file)) { throw "Missing: $relativePath" }
    [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8)
}
function Assert([bool]$condition, [string]$message) {
    if (!$condition) { throw "R5/P4.14 FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

$settings = Source 'src\AltyaziDB.Player.Core\Models\PlayerSettings.cs'
$service = Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$interfaces = Source 'src\AltyaziDB.Player.Core\Interfaces\ITelegramService.cs'
$models = Source 'src\AltyaziDB.Player.Core\Models\TelegramModels.cs'
$telegram = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$main = Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$view = Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml'
$codeBehind = Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml.cs'
$store = Source 'src\AltyaziDB.Player.Telegram\TelegramSevenZipStoredRangeResolver.cs'
$turkish = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$english = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'

Assert ($settings.Contains('public int TelegramCacheLimitGiB { get; set; } = 10;')) 'Safe 10-GiB default'
Assert ($settings.Contains('public bool TelegramCacheAutoCleanup { get; set; } = true;')) 'Automatic cleanup enabled by default'
Assert ($settings.Contains('public string TelegramCacheDirectory { get; set; }')) 'Cache path persists without moving login secrets'
Assert ($interfaces.Contains('GetCacheUsageAsync') -and $interfaces.Contains('OptimizeCacheAsync')) 'TDLib cache API exposed'
Assert ($models.Contains('public sealed record TelegramCacheUsage(')) 'Cache usage separated from database'
Assert ($service.Contains('"getStorageStatisticsFast"')) 'TDLib official statistics used'
Assert ($service.Contains('"optimizeStorage"')) 'TDLib official optimizer used'
Assert ($service.Contains('["size"] = targetBytes')) 'Selected size quota passed to TDLib'
Assert ($service.Contains('["immunity_delay"] = manual ? 0 : 3600')) 'Auto cleanup preserves recently accessed chunks'
Assert ($service.Contains('GetArchiveTemporarySize(directory)')) 'App-owned archive temporary usage shown'
Assert ($service.Contains('CleanupOrphanedArchiveCache()')) 'Only abandoned extraction caches considered for deletion'
Assert ($service.Contains('active.Contains(full)')) 'Live archive extraction folders protected'
Assert ($service.Contains('DateTime.UtcNow.AddDays(-1)')) 'Recent other-process cache files protected'
Assert (-not ($service.Contains('Directory.Delete(configuration.DatabaseDirectory'))) 'Encrypted Telegram database not deleted'
Assert ($telegram.Contains('_paths.TelegramDatabaseDirectory,') -and $telegram.Contains('filesDirectory,')) 'TDLib session data stays in existing database directory'
Assert ($telegram.Contains('() => true') -and $main.Contains('() => Snapshot.IsIdle')) 'Caller supplies playback-idle guard'
Assert ($telegram.Contains('if (!_canMaintainCache())')) 'Manual delete blocked during active playback'
Assert ($telegram.Contains('CacheLimitGiB * 1024L * 1024L * 1024L')) 'Automatic byte quota uses GiB'
Assert ($telegram.Contains('TimeSpan.FromMinutes(30)')) 'Idle-triggered auto optimization is throttled'
Assert ($telegram.Contains('Path.Combine(selectedBaseFolder, "ADB-Player-Telegram-Cache")')) 'Selected folder is app-specific'
Assert ($telegram.Contains('database.StartsWith(newDirectory') -and $telegram.Contains('CacheLocationHint')) 'No database path collision and restart-only folder notice'
Assert ($view.Contains('Telegram.CacheUsageLabel') -and $view.Contains('Telegram.CacheAutoCleanup')) 'Settings show size and auto-clean toggle'
Assert ($view.Contains('Telegram.ClearCacheCommand') -and $view.Contains('Telegram.CacheLimitOptions')) 'Settings include clear button and quota'
Assert ($codeBehind.Contains('ChooseTelegramCacheDirectory_Click')) 'Folder picker wired'
Assert ($turkish.Contains('Settings.TelegramCache.Clear') -and $english.Contains('Settings.TelegramCache.Clear')) 'Turkish and English UI labels present'
Assert ($store.Contains('TryResolveAsync') -and $store.Contains('decoded.AsSpan().SequenceEqual(direct)')) 'Validated 7z Store Range unaffected'

Write-Host '[PASS] R5/P4.14 cache management contracts; P4.13j 7z Store untouched' -ForegroundColor Cyan
