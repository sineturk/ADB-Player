param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Required([string]$file) {
    $path = Join-Path $root $file
    if (!(Test-Path -LiteralPath $path)) { throw "Missing $path" }
    return [IO.File]::ReadAllText($path)
}

$auth = Read-Required 'src\AltyaziDB.Player.Infrastructure\NeonCloudAccountService.cs'
$accountView = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\AccountView.xaml'
$sourcesPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$sourcesView = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$sourcesCode = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml.cs'
$sourcesModel = Read-Required 'src\AltyaziDB.Player.App\ViewModels\SourcePanelViewModel.cs'
$stringsTr = Read-Required 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$stringsEn = Read-Required 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'

foreach ($path in @($sourcesPath, (Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\AccountView.xaml'),
    (Join-Path $root 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'),
    (Join-Path $root 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'))) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($path)
    Write-Host "[PASS] XML: $path"
}

function Require([string]$text, [string]$value) {
    if (!$text.Contains($value)) { throw "R5/P3.2 guard failed: $value" }
    Write-Host "[PASS] $value"
}

# Username/email follows the real v0.8 production API contract, not Neon Auth's
# email-only route. The API resolves the username entirely server-side.
Require $auth 'new Uri(_apiBaseUri, "v1/auth/sign-in")'
Require $auth 'new { identifier, password, rememberMe }'
Require $auth '"authCookieHeader"'
Require $auth 'TryApplyAuthCookieHeader'
Require $auth 'new Uri(_baseUri, "get-session")'
Require $auth 'CookieSecretKey'
Require $auth 'if (rememberMe)'
if ($auth.Contains('new Uri(_baseUri, "sign-in/email")')) {
    throw 'Old email-only login must not be active.'
}
Require $accountView 'Value="{DynamicResource R5.Auth.Identifier}"'
Require $accountView 'Binding IsRegisterMode'
foreach ($str in @($stringsTr, $stringsEn)) {
    Require $str 'x:Key="R5.Auth.Identifier"'
    Require $str 'x:Key="R5.Sources.ProviderTitle"'
    Require $str 'x:Key="R5.Sources.SecureBrowserHelp"'
    Require $str 'x:Key="R5.Sources.WebDavPassword"'
}

# Every provider card opens a dedicated connection pane, existing account
# records and provider OAuth engine remain untouched.
Require $sourcesView 'x:Name="SourcesProviderCards"'
Require $sourcesView 'x:Name="SourcesProviderOverlay"'
Require $sourcesView 'CloudProviderCard_Click'
Require $sourcesView 'WebDavProviderCard_Click'
Require $sourcesView 'SourcesProviderConfirm_Click'
Require $sourcesView 'SourcesWebDavConfirm_Click'
Require $sourcesView 'PasswordChanged="WebDavCardPassword_OnChanged"'
Require $sourcesView 'ItemsSource="{Binding FilteredCloudAccounts, ElementName=SourcesRoot}"'
Require $sourcesView 'Click="CloudDisconnect_Click"'
Require $sourcesView 'Click="LinkRemove_Click"'
Require $sourcesView 'Click="WebDavRemove_Click"'
foreach ($provider in @('GoogleDrive', 'Dropbox', 'OneDrive', 'PCloud', 'PCloudEurope', 'WebDAV')) {
    Require $sourcesView ('Tag="' + $provider + '"')
}
Require $sourcesCode 'ConnectCloudProviderAsync(provider, model.Sources.CloudAccountName,'
Require $sourcesCode 'ConnectNewWebDavFromCardAsync()'
Require $sourcesCode 'SourcesProviderOverlay.Visibility = Visibility.Collapsed'
Require $sourcesCode 'SourcesProviderCards.Columns ='
Require $sourcesModel 'public async Task<bool> ConnectCloudProviderAsync'
Require $sourcesModel 'public async Task<bool> ConnectNewWebDavFromCardAsync'
Require $sourcesModel 'onAuthorizationLink: uri =>'
Require $sourcesModel 'await _secretStore.SetAsync(profile.SecretKey, WebDavPassword)'
Write-Host '[PASS] R5/P3.2 username and provider cards source checks complete' -ForegroundColor Cyan
