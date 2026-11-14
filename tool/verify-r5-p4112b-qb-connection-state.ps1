param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string]$name) {
  $path = Join-Path $root $name
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.11.2b missing: $name" }
  [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([string]$source, [string]$needle, [string]$label) {
  if (!$source.Contains($needle)) { throw "R5/P4.11.2b $label missing: $needle" }
  Write-Host "[PASS] $label" -ForegroundColor Green
}
$vm = Read-Source 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$codeBehind = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml.cs'
$viewPath = 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'
$view = Read-Source $viewPath
$client = Read-Source 'src\AltyaziDB.Player.App\Services\QBittorrentHandoffClient.cs'
$settings = Read-Source 'src\AltyaziDB.Player.Core\Models\PlayerSettings.cs'

foreach ($token in @(
  '_qbConnectionState = string.IsNullOrWhiteSpace(settings.QBittorrentUsername)',
  'private const string PasswordKey = "qbittorrent.webui.password.v1"',
  'public string QBittorrentConnectionStatus =>',
  'public string QBittorrentConnectionState =>',
  'public bool QBittorrentHasSavedPassword',
  'public async Task RefreshQBittorrentConnectionAsync()',
  'CheckQBittorrentCommand = new AsyncRelayCommand(RefreshQBittorrentConnectionAsync)',
  '_secretStore.GetAsync(PasswordKey)',
  '_qbClient.TestAsync(',
  '_settings.QBittorrentUsername',
  'SetQBittorrentConnectionState("Connected")',
  'SetQBittorrentConnectionState("Unavailable")',
  'SetQBittorrentConnectionState("PasswordMissing")',
  'SetQBittorrentConnectionState("Edited")',
  'OnPropertyChanged(nameof(QBittorrentConnectionStatus))',
  'QBittorrentStatus = string.Empty;',
  'QBittorrentHasSavedPassword = true;',
  'QBittorrentAutoHandoffRestricted =>',
  '_secretStore.SetAsync(PasswordKey, password)'
)) { Require $vm $token 'VM connection state and secure recheck' }
if ($vm.Contains('QBittorrentStatus = LocalizationManager.Get("R5.Torrent.Qb.NotConnected");')) {
  throw 'Reintroduced incorrect connection status in operation field'
}

foreach ($token in @(
 'Loaded="TorrentView_OnLoaded"',
 'Unloaded="TorrentView_OnUnloaded"',
 'Text="{Binding QBittorrentConnectionStatus}"',
 'Binding="{Binding QBittorrentConnectionState}" Value="Connected"',
 'Binding="{Binding QBittorrentConnectionState}" Value="Unavailable"',
 'Binding="{Binding QBittorrentHasSavedPassword}" Value="True"',
 'Command="{Binding CheckQBittorrentCommand}"',
 'Binding="{Binding QBittorrentAutoHandoffRestricted}" Value="True"',
 'Command="{Binding SaveQBittorrentCommand}"',
 'Command="{Binding TransferToQBittorrentCommand}"',
 'Command="{Binding SmartHandoffCommand}"',
 'Text="{Binding QBittorrentStatus, Converter={StaticResource UserFacingTextConverter}}"'
)) { Require $view $token 'Visible connection status and preserved transfer buttons' }

$doc = New-Object Xml.XmlDocument
$doc.Load((Join-Path $root $viewPath))
if ($doc.SelectSingleNode("//*[local-name()='Expander']/*[local-name()='Expander.Header']") -eq $null) {
 throw 'Status must be visible even when settings Expander is collapsed'
}
foreach ($token in @(
 'torrent.RefreshQBittorrentConnectionAsync()',
 'torrent.PropertyChanged += Torrent_OnPropertyChanged',
 '_boundTorrent.PropertyChanged -= Torrent_OnPropertyChanged',
 'QbPasswordBox_PasswordChanged'
)) { Require $codeBehind $token 'On-open recheck and password draft cleanup' }

Require $client 'api/v2/auth/login' 'qBittorrent WebUI login check'
Require $client 'api/v2/app/version' 'qBittorrent live version check'
Require $settings 'public string QBittorrentUsername { get; set; }' 'Stored username'
if ($settings.Contains('QBittorrentPassword')) { throw 'Password leaked into JSON settings' }

foreach ($lang in @('tr-TR','en-US')) {
  $name = "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
  $text = Read-Source $name
  $dict = New-Object Xml.XmlDocument
  $dict.Load((Join-Path $root $name))
  foreach ($key in @(
   'R5.Torrent.Qb.State.NotConfigured','R5.Torrent.Qb.State.Unchecked',
   'R5.Torrent.Qb.State.Checking','R5.Torrent.Qb.State.Connected',
   'R5.Torrent.Qb.State.Unavailable','R5.Torrent.Qb.State.PasswordMissing',
   'R5.Torrent.Qb.State.Edited','R5.Torrent.Qb.PasswordStored','R5.Torrent.Qb.Recheck',
   'R5.Torrent.Qb.AutoSharedFolderWarning')) {
    Require $text ('x:Key="' + $key + '"') "$lang status translation"
  }
}
Write-Host '[PASS] R5/P4.11.2b persisted settings, secure DPAPI recheck, and clear connection status' -ForegroundColor Cyan
