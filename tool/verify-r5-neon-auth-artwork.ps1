param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$generatedPath = Join-Path $root 'src\AltyaziDB.Player.Infrastructure\Generated\GeneratedAdbCloudConfig.g.cs'
$accountPath = Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\AccountPanelViewModel.cs'
$mainPath = Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$authServicePath = Join-Path $root 'src\AltyaziDB.Player.Infrastructure\NeonCloudAccountService.cs'
$metadataPath = Join-Path $root 'src\AltyaziDB.Player.Infrastructure\AltyaziDbPlayerMetadataService.cs'

foreach ($path in @($generatedPath, $accountPath, $mainPath, $authServicePath, $metadataPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing file: $path" }
}

$config = [IO.File]::ReadAllText($generatedPath)
$account = [IO.File]::ReadAllText($accountPath)
$main = [IO.File]::ReadAllText($mainPath)
$service = [IO.File]::ReadAllText($authServicePath)
$metadata = [IO.File]::ReadAllText($metadataPath)

function Require([string]$content, [string]$needle) {
    if (-not $content.Contains($needle)) { throw "P3.1 Auth/artwork source guard failed: $needle" }
    Write-Host "[PASS] $needle" -ForegroundColor Green
}

function ParsePublicUrl([string]$name) {
    $pattern = 'public const string ' + [Regex]::Escape($name) + ' = "([^"]*)";'
    $match = [regex]::Match($config, $pattern)
    if (-not $match.Success) { throw "Missing generated $name" }
    $url = $match.Groups[1].Value
    $parsed = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -ne 'https' -or
        -not $parsed.Host.EndsWith('.neon.tech', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::IsNullOrEmpty($parsed.UserInfo) -or
        -not [string]::IsNullOrEmpty($parsed.Query) -or
        -not [string]::IsNullOrEmpty($parsed.Fragment)) {
        throw "Unsafe or unset $name; configure the public Neon HTTPS URL (no secrets)."
    }
    return $parsed
}

$auth = ParsePublicUrl 'AuthBaseUrl'
$api = ParsePublicUrl 'ApiBaseUrl'
if ($auth.Host -notmatch '(?i)\.neonauth\.' -or
    $auth.AbsolutePath.TrimEnd('/') -notmatch '/auth$') {
    throw 'Neon Auth base URL is not a canonical auth route.'
}
if ($api.Host -notmatch '(?i)-adbcloudapi\.compute\.') {
    throw 'ADB Cloud API function URL is not configured.'
}
Write-Host "[PASS] Neon production Auth URL: $($auth.Host)" -ForegroundColor Green
Write-Host "[PASS] Neon Cloud API URL: $($api.Host)" -ForegroundColor Green

# Auth is an identity check. Core bootstrap and future Membership entitlements
# must not gate the already-authenticated metadata request.
Require $account 'public event EventHandler? CloudAuthReady;'
Require $account 'CloudAuthReady?.Invoke(this, EventArgs.Empty);'
$notifyCount = ([regex]::Matches($account, [regex]::Escape('CloudAuthReady?.Invoke(this, EventArgs.Empty);'))).Count
if ($notifyCount -ne 3) { throw "Neon Auth must notify on restore, sign-in, and sign-up. Count: $notifyCount" }
Require $account 'await BootstrapCloudCoreAsync(result.Snapshot).ConfigureAwait(true);'
Require $main 'Account.CloudAuthReady += AccountOnCloudAuthReady;'
Require $main 'Account.CloudAuthReady -= AccountOnCloudAuthReady;'
Require $main 'private async void AccountOnCloudAuthReady(object? sender, EventArgs e)'
Require $main 'await EnrichContinueWatchingArtworkAsync(ContinueWatching.ToArray())'
Require $main 'await EnrichHistoryArtworkAsync(HistoryItems.ToArray())'
Require $main 'await Library.RefreshArtworkAsync(retryMisses: true)'
Require $main 'private async void AccountOnCloudSessionReady(object? sender, EventArgs e)'
Require $main 'await EnrichContinueWatchingArtworkAsync(ContinueWatching.ToArray())'
Require $main 'await EnrichHistoryArtworkAsync(HistoryItems.ToArray())'
Require $main '_continueWatchingArtworkCts = new CancellationTokenSource(TimeSpan.FromSeconds(28))'
Require $main '_historyArtworkCts = new CancellationTokenSource(TimeSpan.FromSeconds(28))'
Require $main 'ADB poster metadata servisi: {error}'
Require $service 'public async Task<PlayerApiAuthContext?> GetPlayerApiAuthAsync('
Require $metadata 'var auth = await _authProvider.GetPlayerApiAuthAsync(cancellationToken)'
Require $metadata 'request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken)'
Require $metadata 'request.Headers.TryAddWithoutValidation("X-ADB-Device-Key", auth.DeviceKey)'
Require $metadata 'private static MediaMetadataResult ParseResult(JsonElement root)'
if ($main.Contains('MembershipGate') -or $account.Contains('MembershipGate')) {
    throw 'Membership/Premium must not be introduced into normal Neon Auth login.'
}
Write-Host '[PASS] R5/P3.1 Neon login + authenticated artwork source guards complete' -ForegroundColor Cyan
