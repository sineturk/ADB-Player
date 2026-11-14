param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$relative) {
  $path = Join-Path $root $relative
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.8 missing source $relative" }
  return [IO.File]::ReadAllText($path)
}
function Require([string]$body, [string]$token, [string]$label) {
  if (!$body.Contains($token)) { throw "R5/P4.8 $label missing $token" }
  Write-Host "[PASS] $label" -ForegroundColor Green
}
$xamlPath = 'src\AltyaziDB.Player.App\Views\Pages\ConnectionsView.xaml'
$vmPath = 'src\AltyaziDB.Player.App\ViewModels\ConnectionsPanelViewModel.cs'
$servicePath = 'src\AltyaziDB.Player.Connections\Services\ExternalConnectionService.cs'
$x = Read-Source $xamlPath
$vm = Read-Source $vmPath
$service = Read-Source $servicePath
foreach ($path in @($xamlPath,
 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml',
 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml')) {
  $doc = New-Object System.Xml.XmlDocument
  $doc.Load((Join-Path $root $path))
  Write-Host "[PASS] XML parses $path"
}
foreach ($token in @(
 'x:Name="ConnectionAddressBox"',
 'x:Name="ConnectionApiKeyBox"',
 'x:Name="ConnectionRemoteHttpWarning"',
 'x:Name="ConnectionAdvancedSettings"',
 'Header="{DynamicResource Connections.Setup.Advanced}"',
 'IsExpanded="False"',
 'IsRemoteHttpAddress',
 'IsLocalHttpAddress'
)) {
 Require $x $token 'Progressive connection UX'
}
foreach ($token in @(
 'ConnectionName, UpdateSourceTrigger=PropertyChanged',
 'SelectedConnectionType, Mode=TwoWay',
 'BaseUrl, UpdateSourceTrigger=PropertyChanged',
 'ApiKey_OnChanged',
 'UseLocalProwlarrCommand',
 'TestConnectionCommand',
 'SaveConnectionCommand',
 'LegalNoticeAccepted, Mode=TwoWay',
 'UserOwnsServiceConfirmed, Mode=TwoWay',
 'MovieCategories, UpdateSourceTrigger=PropertyChanged',
 'SeriesCategories, UpdateSourceTrigger=PropertyChanged',
 'AnimeCategories, UpdateSourceTrigger=PropertyChanged',
 'OtherCategories, UpdateSourceTrigger=PropertyChanged',
 'ResultLimit, UpdateSourceTrigger=LostFocus',
 'Binding HasProfiles'
)) { Require $x $token 'Existing connection actions kept' }
$server = $x.IndexOf('x:Name="ConnectionAddressBox"')
$key = $x.IndexOf('x:Name="ConnectionApiKeyBox"')
$advanced = $x.IndexOf('x:Name="ConnectionAdvancedSettings"')
$consent = $x.IndexOf('Connections.Legal.Accept')
$test = $x.IndexOf('Connections.Action.Test')
if ($server -lt 0 -or $key -lt $server -or $advanced -lt $key -or
 $consent -lt $advanced -or $test -lt $consent) { throw 'Three-step setup ordering changed' }
foreach ($token in @(
 'public bool IsLocalHttpAddress',
 'public bool IsRemoteHttpAddress',
 'OnPropertyChanged(nameof(IsLocalHttpAddress))',
 'OnPropertyChanged(nameof(IsRemoteHttpAddress))',
 'ConnectionName = "Prowlarr"',
 'BaseUrl = string.Empty',
 'UseLocalProwlarr()',
 'BaseUrl = "http://localhost:9696"',
 'var baseUrl = BaseUrl',
 'AllowLocalHttp = false',
 'IPAddress.TryParse(uri.Host'
)) { Require $vm $token 'URL guidance and explicit localhost' }
if ($vm.Contains('if (type == ExternalConnectionType.Prowlarr && string.IsNullOrWhiteSpace(baseUrl))')) {
  throw 'Empty server URL must not silently become localhost'
}
foreach ($token in @(
 'ValidateRemoteUri(uri, allowLocalHttp)',
 '!allowLocalHttp || !IsLocalNetworkHost(uri)',
 'X-Api-Key',
 'LegalNoticeAccepted',
 'UserOwnsServiceConfirmed'
)) { Require $service $token 'Existing backend safeguards' }
foreach ($language in @('tr-TR','en-US')) {
  $content = Read-Source "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"
  foreach ($key in @('Connections.Setup.IntroTitle','Connections.Setup.RemoteHttpTitle',
                     'Connections.Setup.RemoteHttpSolution','Connections.Setup.ProwlarrApiKeyHelp',
                     'Connections.Setup.Advanced','Connections.Setup.SaveHelp')) {
    Require $content ('x:Key="' + $key + '"') "$language localization"
  }
}
Write-Host '[PASS] R5/P4.8 connection setup preserves API secret rules and improves discovery' -ForegroundColor Cyan
