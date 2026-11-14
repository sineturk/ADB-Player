param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Src([string]$name) {
  $path = Join-Path $root $name
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.11 missing file: $name" }
  [IO.File]::ReadAllText($path)
}
function Require([string]$content, [string]$needle) {
  if (!$content.Contains($needle)) { throw "R5/P4.11 missing: $needle" }
  Write-Host "[PASS] $needle"
}
$app = Src 'src\AltyaziDB.Player.App\Services\QBittorrentHandoffClient.cs'
$vm = Src 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$main = Src 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$settings = Src 'src\AltyaziDB.Player.Core\Models\PlayerSettings.cs'
$viewPath = 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'
$xaml = Src $viewPath
$behind = Src 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml.cs'

foreach ($needle in @(
 'ValidateBaseAddress(address)',
 'api/v2/auth/login', 'api/v2/app/version', 'api/v2/torrents/add',
 'MultipartFormDataContent', 'FormUrlEncodedContent', 'ByteArrayContent',
 'form.Add(new StringContent(source, Encoding.UTF8), "urls")',
 'form.Add(attachment, "torrents", "adb-player.torrent")',
 'Uri.UriSchemeHttp', 'Uri.UriSchemeHttps', 'IPAddress.IsLoopback',
 'AllowAutoRedirect = false', 'UseCookies = true',
 'CookieContainer = new CookieContainer()',
 'client.DefaultRequestHeaders.Referrer = root',
 '!body.Equals("Ok."',
 'source.Length > 65536',
 'MaximumTorrentFileBytes',
 'file.Length == 0',
 'source.EndsWith(".torrent"'
)) { Require $app $needle }

# No redirect to third-party origin, unsafe skip-checking, destructive seeding
# defaults or public-HTTP credential leaks.
if ($app.Contains('AllowAutoRedirect = true') -or
    $app.Contains('SkipHashCheck = true') -or
    $app.Contains('RemoveWithContent') -or
    $app.Contains('ratioLimit = 0') -or
    $app.Contains('AllowPublicHttp = true'))
{ throw 'Unsafe qBittorrent handoff default' }

foreach ($needle in @(
 'public string QBittorrentWebUiUrl { get; set; } = "http://127.0.0.1:8080";',
 'public string QBittorrentUsername { get; set; }',
 'public bool QBittorrentAutoHandoff { get; set; }'
)) { Require $settings $needle }
if ($settings.Contains('QBittorrentPassword')) { throw 'Password must NOT be in JSON settings' }

foreach ($needle in @(
 'private readonly ISecretStore _secretStore;',
 'private const string PasswordKey = "qbittorrent.webui.password.v1";',
 '_secretStore.GetAsync(PasswordKey',
 '_secretStore.SetAsync(PasswordKey, password)',
 'SaveQBittorrentCommand = new AsyncRelayCommand(SaveQBittorrentAsync)',
 'TransferToQBittorrentCommand = new AsyncRelayCommand(TransferToQBittorrentAsync)',
 'await AutoHandoffIfEnabledAsync(preparation.Token)',
 'if (!SetProperty(ref _qbAutoHandoff, value)) return;',
 '_settings.QBittorrentAutoHandoff = value;',
 'TransferSourceToQBittorrentAsync(Session?.Source, CancellationToken.None)',
 '_qbClient.AddAsync(', '_handedOff.Add(source)'
)) { Require $vm $needle }
Require $main '            secretStore);'

foreach ($needle in @(
 'R5.Torrent.Qb.Header', 'R5.Torrent.Qb.Help',
 'R5.Torrent.Qb.HandoffNote', 'R5.Torrent.Qb.Transfer',
 'Text="{Binding QBittorrentUrl, UpdateSourceTrigger=PropertyChanged}"',
 'Text="{Binding QBittorrentUsername, UpdateSourceTrigger=PropertyChanged}"',
 'PasswordChanged="QbPasswordBox_PasswordChanged"',
 'IsChecked="{Binding QBittorrentAutoHandoff, Mode=TwoWay}"',
 'Command="{Binding SaveQBittorrentCommand}"',
 'Command="{Binding TransferToQBittorrentCommand}"',
 'IsExpanded="False"'
)) { Require $xaml $needle }
Require $behind 'QBittorrentPasswordInput = QbPasswordBox.Password'
$document = New-Object System.Xml.XmlDocument
$document.Load((Join-Path $root $viewPath))
foreach ($language in @('tr-TR','en-US')) {
 $path = "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"
 $doc = New-Object System.Xml.XmlDocument
 $doc.Load((Join-Path $root $path))
 $text = Src $path
 foreach ($key in @(
   'R5.Torrent.Qb.Header','R5.Torrent.Qb.Url','R5.Torrent.Qb.Username',
   'R5.Torrent.Qb.Password','R5.Torrent.Qb.Auto','R5.Torrent.Qb.Transfer',
   'R5.Torrent.Qb.Sent','R5.Torrent.Qb.Failed','R5.Torrent.Qb.HandoffNote'))
 {
   Require $text ('x:Key="' + $key + '"')
 }
}
Write-Host '[PASS] R5/P4.11 qBittorrent secure handoff and opt-in auto transfer' -ForegroundColor Cyan
