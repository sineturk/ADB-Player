$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string]$Relative) {
    $path = Join-Path $root $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "[P4.16] source missing: $Relative"
    }
    return [IO.File]::ReadAllText($path)
}

function Check([string]$Content, [string]$Marker, [string]$Label) {
    if (-not $Content.Contains($Marker)) { throw "[P4.16] $Label missing: $Marker" }
    Write-Host "[PASS] $Label" -ForegroundColor Green
}

$homeXaml = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml'
$homeCode = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml.cs'
$shell = Read-Source 'src\AltyaziDB.Player.App\Views\Shell\MediaCenterShell.xaml'
$shellCode = Read-Source 'src\AltyaziDB.Player.App\Views\Shell\MediaCenterShell.xaml.cs'
$cloud = Read-Source 'src\AltyaziDB.Player.Cloud\RcloneCloudService.cs'
$main = Read-Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$sources = Read-Source 'src\AltyaziDB.Player.App\ViewModels\SourcePanelViewModel.cs'
$sourcesXaml = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$sourcesCode = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml.cs'
$settingsXaml = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml'
$tr = Read-Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Read-Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
$byocGuide = Read-Source 'docs\CLOUD-BYOC-SETUP-TR.md'
$openUrlXaml = Read-Source 'src\AltyaziDB.Player.App\Views\OpenUrlDialog.xaml'
$themeTokens = Read-Source 'src\AltyaziDB.Player.App\Themes\R2.Tokens.xaml'
$playerSettings = Read-Source 'src\AltyaziDB.Player.Core\Models\PlayerSettings.cs'
$appPaths = Read-Source 'src\AltyaziDB.Player.Infrastructure\AppPaths.cs'
$settingsCode = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml.cs'
$cloudCache = Read-Source 'src\AltyaziDB.Player.App\ViewModels\CloudCachePanelViewModel.cs'
$trackExport = Read-Source 'src\AltyaziDB.Player.App\Services\EmbeddedTrackExportService.cs'
$mainWindow = Read-Source 'src\AltyaziDB.Player.App\MainWindow.xaml'
$mainWindowCode = Read-Source 'src\AltyaziDB.Player.App\MainWindow.xaml.cs'
$settingsPage = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml'

[xml]$null = $homeXaml
[xml]$null = $shell
[xml]$null = $sourcesXaml
[xml]$null = $tr
[xml]$null = $en
[xml]$null = $openUrlXaml
[xml]$null = $themeTokens
[xml]$null = $mainWindow
[xml]$null = $settingsPage
Check $homeXaml 'R516HeroPrimary' 'Quiet primary hero button'
Check $homeXaml 'R516HeroSecondary' 'Quiet secondary hero button'
Check $homeXaml 'HomeEmptyHeroBrandCard' 'Smaller single brand panel'
Check $homeXaml 'ContinueWatching.Count' 'Empty-hero hides stale resume backdrop'
Check $homeCode 'HomeEmptyHeroBrandCard.Visibility' 'Responsive home brand state'
Check $shell 'x:Name="NavigationRail"' 'Compact sidebar'
Check $shell 'Grid.ColumnSpan="2" Panel.ZIndex="20"' 'Hover rail spans both columns without clipping labels'
Check $shell 'x:Name="NavigationToggle"' 'Keyboard-accessible navigation toggle'
Check $shellCode 'CompactNavigationWidth = 64' '64px content-first navigation'
Check $shellCode 'NavigationRail_OnMouseEnter' 'Hover overlay navigation'
Check $shellCode 'NavigationColumn.Width = new GridLength(_navigationPinned' 'No content reflow during hover'
Check $shellCode 'NavigationButtons.Margin = expanded' 'Compact nav uses safe 5px margins'
Check $shellCode 'button.Padding = expanded' 'Compact nav centers full-width 33px glyph'
Check $main 'var url = _dialogs.PromptForUrl()' 'Open URL command opens actual input dialog'
Check $sources 'DescribeRemotePlaybackFailure(exception)' 'Cloud playback surfaces safe failure reason'
Check $sources '_dialogs.ShowError(StatusText)' 'Single-paragraph error survives dialog localization'
Check $cloud 'SharedDrivesVirtualRoot' 'Distinct nonpersisted shared-drive browsing namespace'
Check $cloud '"backend", "drives"' 'Rclone Google Team Drive discovery'
Check $cloud 'BuildSharedDriveRemoteSpec' 'Google Team Drive Rclone path override'
Check $cloud 'IsSafeSharedDriveId' 'Team Drive ID validation'
Check $cloud 'ProbeLocalMediaAsync' 'Local playback HTTP diagnostic probe'
Check $cloud 'new RangeHeaderValue(0, 0)' 'Minimal 1-byte read probe'
Check $cloud 'Timeout = TimeSpan.FromSeconds(45)' 'Bounded cold Drive first-range preflight'
Check $cloud 'probeTimer.ElapsedMilliseconds' 'Sanitized first-range latency telemetry'
Check $cloud 'attemptCts.CancelAfter(TimeSpan.FromMilliseconds(500))' 'Each TCP port probe is cancelled and awaited'
if ($cloud -match 'ConnectAsync\(IPAddress\.Loopback, port, cancellationToken\)\.AsTask\(\)\.WaitAsync') {
    throw '[P4.16.3] Abandoned TCP connect tasks must not be reintroduced.'
}
Check $cloud 'UseProxy = false' 'Loopback bypasses machine proxy'
# Regression: rclone serve must NOT set either encoding when redirectOutput=false.
# Prior RC2 threw InvalidOperationException before the HTTP server even started.
$processBuilder = [regex]::Match(
    $cloud, 'private ProcessStartInfo CreateStartInfo\([\s\S]*?return startInfo;',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant).Value
if ([string]::IsNullOrWhiteSpace($processBuilder)) {
    throw '[P4.16.2] Rclone process builder not found.'
}
Check $processBuilder 'if (redirectOutput)' 'Conditional stream encoding for CLI versus rclone serve'
Check $processBuilder 'startInfo.StandardOutputEncoding = Encoding.UTF8;' 'Captured stdout uses UTF8'
Check $processBuilder 'startInfo.StandardErrorEncoding = Encoding.UTF8;' 'Captured stderr uses UTF8'
if ($processBuilder -match '(?m)^\s*Standard(?:Output|Error)Encoding\s*=') {
    throw '[P4.16.2] Stream encoding must never be set in the nonredirected ProcessStartInfo initializer.'
}
Check $cloud 'requestedRemote.Equals(account.RemoteName' 'Cross-account open prevented'
Check $cloud 'StopServerAsync(string remoteName)' 'Shared drive server cleanup'
Check $main 'propagateErrors: true' 'Source panel receives actual failed playback result'
Check $sources 'await _cloudService.CreateOpenRequestAsync' 'Existing cloud open wiring retained'
# P4.16.4: new accounts must never silently use shared rclone OAuth apps.
Check $cloud 'if (provider == RcloneCloudProvider.GoogleDrive)' 'Google Drive requires user-owned OAuth on new links'
Check $cloud 'await RequireUserOwnedClientAsync(provider, cancellationToken)' 'Enforce Drive BYOC at the backend boundary'
Check $sources 'provider == RcloneCloudProvider.GoogleDrive && selectedId.Length == 0' 'Other providers retain documented rclone defaults'
Check $sourcesCode 'R5164.BYOC.MissingOptional' 'Shared fallback status is explicit in non-Drive UI'
Check $cloud 'cloud:{prefix}-client-id' 'Read provider Client ID from local secure store'
Check $cloud 'cloud:{prefix}-client-secret' 'Read provider Client Secret from local secure store'
Check $sources 'HasSavedCloudCredentialsAsync' 'Sources sheet checks existing settings without exposing secrets'
Check $sources 'R5164.BYOC.Required' 'New accounts have a localized mandatory BYOC validation'
Check $sources 'cloud:remote:{newAccount.RemoteName}:client-id' 'Each new remote pins its selected OAuth app'
Check $sources 'cloud:remote:{newAccount.RemoteName}:client-secret' 'Each new remote pins its selected OAuth secret'
Check $sources 'cloud:remote:{newAccount.RemoteName}:shared-client' 'Shared provider-default choice is pinned per account'
Check $cloud 'cloud:remote:{remoteName}:shared-client' 'Later Settings changes do not replace a shared account identity'
Check $cloud 'new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)' 'New non-BYOC provider fallback uses rclone defaults only'
Check $cloud 'HasUserOwnedClientAsync(provider, cancellationToken)' 'Shared fallback depends on actual saved user credentials'
Check $sources 'RestoreCredentialsAsync' 'Temporary overrides do not mutate provider defaults'
Check $sourcesXaml 'x:Name="SourcesOAuthExpander"' 'Existing OAuth sheet reused rather than a duplicate form'
Check $sourcesXaml 'x:Name="SourcesOAuthCredentialStatus"' 'Saved credential status shown without its secret'
Check $sourcesXaml 'Click="SourcesOpenCloudGuide_Click"' 'Cloud heading hosts the help guide'
Check $sourcesXaml 'x:Name="SourcesCloudGuideOverlay"' 'Separate guide overlay avoids cluttering the source modal'
Check $sourcesXaml 'x:Name="SourcesGuideStep3"' 'Provider-specific step-by-step help remains readable'
Check $sourcesCode 'SourcesGuideOpenOfficial_Click' 'Official provider documentation remains accessible'
Check $sourcesCode 'RefreshCloudGuideLanguage' 'Guide language updates dynamically'
Check $sourcesXaml 'x:Name="SourcesOAuthClientSecret"' 'Existing BYOC PasswordBox is unchanged'
Check $sourcesCode 'saved ? "R5164.BYOC.Stored"' 'Provider status drives inline onboarding'
Check $settingsXaml 'ApiSettings.GoogleClientId' 'Settings remains the same stored BYOC source'
Check $settingsXaml 'ApiSettings.PCloudClientId' 'pCloud settings retained'
Check $byocGuide 'https://rclone.org/drive/' 'BYOC user guide covers Google Drive'
Check $byocGuide 'https://rclone.org/dropbox/' 'BYOC user guide covers Dropbox'
Check $byocGuide 'https://rclone.org/onedrive/' 'BYOC user guide covers OneDrive'
Check $byocGuide 'https://rclone.org/pcloud/' 'BYOC user guide covers pCloud'
foreach ($key in @('R5164.BYOC.Checking','R5164.BYOC.Stored','R5164.BYOC.Missing',
                   'R5164.BYOC.Required','R5164.BYOC.PairRequired','R5164.BYOC.StorageError')) {
    Check $tr "x:Key=`"$key`"" "Turkish localization: $key"
    Check $en "x:Key=`"$key`"" "English localization: $key"
}
if (($tr.Contains('Normal kullanım için gerekli değildir.')) -or ($en.Contains("They're not required for regular use."))) {
    throw '[P4.16.4] Old optional BYOC message must not remain.'
}
# R5/P4.16.5: keep the connect sheet short and move setup help out.
Check $sourcesXaml 'x:Name="SourcesOAuthCredentialStatus"' 'Short BYOC status on the connect sheet'
Check $sourcesXaml 'Header="{DynamicResource R5165.Credentials}"' 'Optional credential details keep the connect sheet compact'
Check $sourcesXaml 'Width="495" MaxWidth="560" MaxHeight="530"' 'Compact responsive provider modal'
Check $sourcesXaml 'SourcesGuideProvider_Click' 'Guide switches between four cloud providers'
# R5/P4.16.7: WPF TabPanel adjusts selected tab into a content seam and
# clips bottom rounded borders. A horizontal items-host panel avoids the seam.
Check $themeTokens '<StackPanel IsItemsHost="True" Orientation="Horizontal"/>' 'Rounded source tab headers never overlap content'
Check $themeTokens 'CornerRadius="12" Padding="5" Margin="2,2,0,7"' 'Source selector has complete surrounding outline'
Check $themeTokens 'CornerRadius="9"' 'Each source tab has its own rounded rectangle'
if ($themeTokens.Contains('<TabPanel Grid.Row="0" IsItemsHost="True"')) {
    throw '[P4.16.7] Native TabPanel clips rounded saved-source tab borders.'
}
Check $openUrlXaml 'SizeToContent="Height"' 'Open URL dialog grows to show whole input box'
Check $openUrlXaml 'Height="88" MinHeight="88"' 'Multiline URL input is visible at scaled display DPI'
Check $openUrlXaml 'TextWrapping="Wrap"' 'Long URL is readable without horizontal clipping'
if ($openUrlXaml.Contains('Height="220"')) {
    throw '[P4.16.5] Fixed-height Open URL dialog regression.'
}
foreach ($provider in @('GoogleDrive','Dropbox','OneDrive','PCloud')) {
    foreach ($key in @('Title','Intro','Step1','Step2','Step3','Note')) {
        $resource = 'R5165.Guide.' + $provider + '.' + $key
        Check $tr ('x:Key="' + $resource + '"') "TR setup guide: $resource"
        Check $en ('x:Key="' + $resource + '"') "EN setup guide: $resource"
    }
}
# P4.16.6: protect OAuth state, user downloads, active attached files and local exports.
Check $playerSettings 'CloudCacheLimitGiB' 'Cloud cache quota persisted in settings'
Check $playerSettings 'CloudCacheAutoCleanup' 'Cloud auto cleanup persisted in settings'
Check $playerSettings 'CloudCacheDirectory' 'Custom cloud cache parent persisted'
Check $appPaths 'TryPrepareCloudCacheDirectory' 'Managed cloud cache folder policy is applied at startup'
Check $appPaths 'ADB_PLAYER_CLOUD_CACHE_V1' 'Marked child folder ownership must be verified'
Check $appPaths 'FileAttributes.ReparsePoint' 'Custom cloud cache refuses junctions'
Check $cloudCache '.adb-player-cache-owner' 'Cleanup preserves its ownership marker'
Check $settingsPage 'ChooseCloudCacheDirectory_Click' 'User may choose dedicated cloud cache location'
Check $settingsCode 'CloudCache.SetDirectoryAsync' 'Folder selection persists the next-startup setting'
Check $settingsPage 'ResetCloudCacheDirectory_Click' 'Default folder can be restored'
Check $cloudCache '_paths.RcloneCacheDirectory' 'Cleanup restricted to rclone/cache'
Check $cloudCache 'private List<FileInfo> ScanSafeFiles()' 'Cloud cleanup only sees vetted cache files'
Check $cloudCache 'FileAttributes.ReparsePoint' 'Cache scan never follows symlinks/junctions'
Check $cloudCache 'if (_mediaActive()) return;' 'Cache cleanup never runs during active playback'
Check $cloudCache 'AutomaticGrace = TimeSpan.FromDays(1)' 'Automatic cleanup avoids fresh cache files'
Check $cloudCache 'ManualGrace = TimeSpan.FromMinutes(15)' 'Manual cleanup protects recent files'
Check $settingsPage 'CloudCache.UsageLabel' 'Cache usage is visible in Settings'
# WPF TextBox.Text defaults to TwoWay even if IsReadOnly=true.
# DirectoryDisplay is a get-only property; a default TwoWay binding crashes
# the entire app at startup through WPF Dispatcher.
Check $settingsPage 'Text="{Binding CloudCache.DirectoryDisplay, Mode=OneWay}"' 'Read-only cloud cache path must bind OneWay'
if ($settingsPage.Contains('Text="{Binding CloudCache.DirectoryDisplay}"')) {
    throw '[P4.16.6 Fix2] WPF read-only DirectoryDisplay binding would crash at startup.'
}
Check $settingsPage 'CloudCache.ClearCommand' 'Cache clear action is wired'
Check $settingsPage 'CloudCache.AutoCleanup' 'Cloud cache auto cleaning is configurable'
Check $trackExport 'File.Exists(fullInput)' 'Export only local media'
Check $trackExport 'AudioExtension(MediaTrack track)' 'Codec-aware native audio extension'
Check $trackExport 'TextSubtitleCodecs' 'Text subtitle whitelist'
Check $trackExport 'File.Move(staging, fullOutput, overwrite: false)' 'Export never overwrites existing files'
Check $trackExport 'RedirectStandardError = true' 'ffmpeg errors captured and hidden from raw UI'
Check $mainWindow 'ExportEmbeddedAudio_Click' 'Audio export in player settings sheet'
Check $mainWindow 'ExportEmbeddedSubtitle_Click' 'SRT export in player settings sheet'
Check $mainWindowCode 'EmbeddedTrackExportService.ExportAsync' 'Local player export remains functional'
# R5/P4.16.8.1: remote extraction did produce empty SRT files.
# The release candidate must not expose that unverified implementation.
Check $mainWindowCode '!File.Exists(currentSource)' 'Remote media export is stopped before SaveFileDialog'
Check $mainWindowCode 'Player.TrackExport.RemoteNotSupported' 'Remote track export has a clear user-facing warning'
Check $trackExport 'new FileInfo(staging).Length == 0' 'A zero-byte export is rejected'
Check $trackExport 'ContainsReadableSrtCue(staging)' 'SRT must have a non-empty cue'
Check $trackExport 'SrtTimingLine.IsMatch(line)' 'SRT requires a real timing range'
Check $trackExport 'inputFile.StartsWith(@"\\", StringComparison.Ordinal)' 'UNC source export is blocked'
Check $trackExport 'File.Move(staging, fullOutput, overwrite: false)' 'Verified local exports do not overwrite'
Check $tr 'x:Key="Player.TrackExport.RemoteNotSupported"' 'Turkish disabled remote export notice'
Check $en 'x:Key="Player.TrackExport.RemoteNotSupported"' 'English disabled remote export notice'
if ($mainWindowCode.Contains('ExportRemoteSubtitleAsync(') -or
    $trackExport.Contains('ExportRemoteSubtitleAsync(') -or
    (Test-Path -LiteralPath (Join-Path $root 'src\AltyaziDB.Player.App\Services\RemoteSubtitleRangeProxyServer.cs'))) {
    throw '[P4.16.8.1] Experimental remote extraction must not ship with RC2.'
}
foreach ($key in @('Settings.CloudCache.Title','Settings.CloudCache.Help',
                   'Player.TrackExport.Title','Player.TrackExport.ExportAudio',
                   'Player.TrackExport.ExportSubtitle','Player.TrackExport.RemoteNotSupported')) {
    Check $tr ('x:Key="' + $key + '"') "TR P4166: $key"
    Check $en ('x:Key="' + $key + '"') "EN P4166: $key"
}
if (($cloudCache.Contains('File.Delete(_paths.SecretsFile)')) -or
    ($cloudCache.Contains('RcloneRuntimeDirectory')) -or
    ($trackExport.Contains('http-header-fields'))) {
    throw '[P4.16.6] Dangerous cache/token/remote FFmpeg pattern detected.'
}
Write-Host '[PASS] R5/P4.16.8.1 remote extraction safely disabled; local outputs validated' -ForegroundColor Green
Write-Host '[PASS] R5/P4.16.6 cloud cache, protected OAuth and local track export guards' -ForegroundColor Green
Write-Host '[PASS] R5/P4.16.5 compact cloud modal, help guide, source tabs and URL dialog' -ForegroundColor Green
Write-Host '[PASS] R5/P4.16.4 Cloud BYOC onboarding, per-remote identity and localization' -ForegroundColor Green
Write-Host '[PASS] R5/P4.16 cinematic home, adaptive nav and Google Shared Drives contracts' -ForegroundColor Green
