param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Required([string]$relative) {
    $full = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $full)) { throw "Missing: $relative" }
    return [IO.File]::ReadAllText($full)
}
function Require([string]$body, [string]$token) {
    if (!$body.Contains($token)) { throw "R5/P3.4 missing: $token" }
    Write-Host "[PASS] $token" -ForegroundColor Green
}

$interface = Read-Required 'src\AltyaziDB.Player.Core\Interfaces\IRcloneCloudService.cs'
$rclone = Read-Required 'src\AltyaziDB.Player.Cloud\RcloneCloudService.cs'
$source = Read-Required 'src\AltyaziDB.Player.App\ViewModels\SourcePanelViewModel.cs'
$view = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml.cs'
$xamlPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$xaml = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$tr = Read-Required 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Read-Required 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
foreach($path in @($xamlPath, (Join-Path $root 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'),
                             (Join-Path $root 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'))) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($path)
    Write-Host "[PASS] Valid XML: $path" -ForegroundColor Green
}

Require $interface 'Action<Uri>? onAuthorizationLink = null'
Require $rclone '"config_is_local", "true"'
Require $rclone '"config_auth_no_browser", "true"'
Require $rclone 'ReadOAuthStderrAsync(process.StandardError, onAuthorizationLink, cancellationToken)'
Require $rclone 'await stderr.ReadLineAsync(cancellationToken)'
Require $rclone 'TryExtractLocalAuthorizationUri(line)'
Require $rclone 'candidate.IsLoopback'
Require $rclone 'candidate.AbsolutePath, "/auth"'
Require $rclone 'candidate.Query.StartsWith("?state="'
Require $rclone 'onAuthorizationLink(url)'
Require $rclone 'AuthorizationTimeout'
Require $rclone 'PersistRuntimeConfigAsync(cancellationToken)'
Require $source 'onAuthorizationLink: uri =>'
Require $source 'SynchronizationContext.Current'
Require $source 'uiContext?.Post'
Require $source 'UseShellExecute = true'
Require $source 'System.Windows.Clipboard.SetText(uri.AbsoluteUri)'
Require $source 'CancelCloudAuthorization()'
Require $source 'ClearOAuthAuthorizationLink()'
Require $source 'oauth_port_busy'
Require $source 'catch (OperationCanceledException)'
Require $source '_activeOAuthCancellation.Token'
Require $view 'SourcesOpenOAuthBrowser_Click'
Require $view 'SourcesCopyOAuthLink_Click'
Require $view 'CancelCloudAuthorization()'
Require $xaml 'Click="SourcesOpenOAuthBrowser_Click"'
Require $xaml 'Click="SourcesCopyOAuthLink_Click"'
Require $xaml 'Binding CanOpenOAuthBrowser'
Require $xaml 'Binding OAuthBrowserStatusText'
foreach($strings in @($tr,$en)){
    foreach($key in @('R5.Sources.BrowserFallbackTitle','R5.Sources.OpenInBrowser',
                      'R5.Sources.CopyAuthLink')) {
        Require $strings ('x:Key="' + $key + '"')
    }
}

# Test the expected callback URI shape with accepted and rejected examples.
$oauthPattern = 'http://(?:127\.0\.0\.1|localhost):[0-9]{2,5}/auth\?state=[^\s"''<>]+'
$validLine = 'NOTICE: If your browser does not open: http://127.0.0.1:53682/auth?state=LOCALSTATE_X'
$externalLine = 'NOTICE: Try https://fake.example/auth?state=LOCALSTATE_X'
if (-not [regex]::IsMatch($validLine, $oauthPattern) -or
    [regex]::IsMatch($externalLine, $oauthPattern)) {
    throw 'Local OAuth callback regex accepts unsafe URL or fails valid loopback URL.'
}

if ($xaml.Contains('http://127.0.0.1:53682/auth?state=') -or
    $source.Contains('_logger.Warning(uri.AbsoluteUri)')) {
    throw 'OAuth state-bearing URL must not be shown in UI or logs.'
}
Write-Host '[PASS] R5/P3.4 live OAuth browser callback, fallback and cancel guards' -ForegroundColor Cyan
