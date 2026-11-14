param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string]$relative) {
  $path = Join-Path $root $relative
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.9 missing: $relative" }
  [IO.File]::ReadAllText($path)
}
function Require([string]$data, [string]$needle, [string]$message) {
  if (!$data.Contains($needle)) { throw "R5/P4.9 ${message}: $needle" }
  Write-Host "[PASS] $message" -ForegroundColor Green
}

$viewPath = 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$view = Read-Source $viewPath
$code = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml.cs'
$model = Read-Source 'src\AltyaziDB.Player.App\ViewModels\SourcePanelViewModel.cs'
$xml = New-Object System.Xml.XmlDocument
$xml.Load((Join-Path $root $viewPath))
foreach ($language in @('tr-TR', 'en-US')) {
  $file = "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"
  $resource = Read-Source $file
  $doc = New-Object System.Xml.XmlDocument
  $doc.Load((Join-Path $root $file))
  foreach ($key in @(
    'R5.Sources.CloudGroup','R5.Sources.OtherGroup',
    'R5.Sources.SharedLink','R5.Sources.SharedLinkHelp',
    'R5.Sources.TelegramHelp'
  )) { Require $resource ('x:Key="' + $key + '"') "$language translation" }
}

# The provider gallery is split into cloud accounts and other sources.
foreach ($name in @('SourcesProviderCards','SourcesOtherCards',
 'SourcesSavedSection','SourcesSavedTabs','SourcesSearchBox',
 'SourcesProviderOverlay','SourcesOAuthProviderFields',
 'SourcesWebDavProviderFields','SourcesLinkProviderFields',
 'SourcesLinkUrlBox')) {
  $node = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='$name']]")
  if ($null -eq $node) { throw "R5/P4.9 missing control: $name" }
}
foreach ($provider in @('GoogleDrive','Dropbox','OneDrive','PCloud',
                         'PCloudEurope','WebDAV','Telegram','SharedLink')) {
  Require $view ('Tag="' + $provider + '"') "provider: $provider"
}
foreach ($callback in @('CloudProviderCard_Click','WebDavProviderCard_Click',
  'TelegramProviderCard_Click','SharedLinkProviderCard_Click',
  'SourcesProviderConfirm_Click','SourcesWebDavConfirm_Click',
  'SourcesProviderCancel_Click','SourcesProviderOverlay_OnPreviewKeyDown',
  'SourcesSearchBox_OnTextChanged',
  'CloudOpen_Click','CloudDisconnect_Click','LinkOpen_Click',
  'LinkRemove_Click','WebDavOpen_Click','WebDavRemove_Click')) {
  Require $view $callback "existing/new action"
  Require $code $callback "event handler"
}

# The formerly embedded save-link form must exist ONLY inside the overlay.
$overlay = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='SourcesProviderOverlay']]")
$link = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='SourcesLinkProviderFields']]")
if ($null -eq $overlay -or $null -eq $link) { throw 'Missing provider overlay/link form' }
$insideOverlay = $link.SelectSingleNode("ancestor::*[@*[local-name()='Name' and .='SourcesProviderOverlay']]")
if ($null -eq $insideOverlay) { throw 'Shared-link form must stay inside the overlay' }
$manager = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='SourcesManagerLayout']]")
if ($null -eq $manager) { throw 'Saved sources section missing' }
if ($view.Contains('x:Name="SourcesAddSection"') -or
    $view.Contains('x:Name="SourcesManagerFirstColumn"')) {
  throw 'Embedded admin-style quick-link layout returned'
}
foreach ($token in @(
 'ItemsSource="{Binding FilteredCloudAccounts, ElementName=SourcesRoot}"',
 'ItemsSource="{Binding FilteredSavedLinks, ElementName=SourcesRoot}"',
 'ItemsSource="{Binding FilteredWebDavProfiles, ElementName=SourcesRoot}"',
 'Command="{Binding OpenPublicLinkCommand}"',
 'Command="{Binding SavePublicLinkCommand}"',
 'PasswordChanged="WebDavCardPassword_OnChanged"',
 'PasswordChanged',
 'x:Name="SourcesOAuthClientId"',
 'x:Name="SourcesOAuthClientSecret"',
 'Click="CloudDisconnect_Click"',
 'Click="LinkRemove_Click"',
 'Click="WebDavRemove_Click"')) {
  Require $view $token 'preserved source binding'
}
foreach ($token in @(
 'SourcesProviderCards.Columns =',
 'SourcesOtherCards.Columns =',
 'SourcesLinkProviderFields.Visibility = Visibility.Visible',
 'SourcesLinkProviderFields.Visibility = Visibility.Collapsed',
 'SourcesLinkUrlBox.Focus()',
 'model.SelectedSidebarIndex = 5;',
 'ConnectCloudProviderAsync(provider, model.Sources.CloudAccountName,',
 'ConnectNewWebDavFromCardAsync()',
 'CancelCloudAuthorization()',
 'SourcesOAuthClientSecret.Clear()',
 'ReferenceEquals(sender, _currentSources?.SavedPublicLinks)'
)) { Require $code $token 'provider behavior' }

foreach ($token in @(
 'public async Task<bool> ConnectCloudProviderAsync',
 'public async Task<bool> ConnectNewWebDavFromCardAsync',
 'SavePublicLinkCommand = new AsyncRelayCommand(SavePublicLinkAsync)',
 'OpenPublicLinkCommand = new AsyncRelayCommand(OpenPublicLinkAsync)',
 'await _secretStore.SetAsync(profile.SecretKey, WebDavPassword)')) {
  Require $model $token 'existing connection security'
}

Write-Host '[PASS] R5/P4.9 concise cloud source gallery and provider overlays' -ForegroundColor Cyan
