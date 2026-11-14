param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing R5/P4.11.2 file: $relative" }
    return [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([string]$text, [string]$needle, [string]$scope) {
    if (!$text.Contains($needle)) { throw "R5/P4.11.2 missing $scope : $needle" }
    Write-Host "[PASS] $scope" -ForegroundColor Green
}
$models = Read-Source 'src\AltyaziDB.Player.Core\Models\TorrentModels.cs'
$mono = Read-Source 'src\AltyaziDB.Player.Torrent\MonoTorrentStreamingService.cs'
$planner = Read-Source 'src\AltyaziDB.Player.Torrent\SmartTorrentHandoffPlanner.cs'
$client = Read-Source 'src\AltyaziDB.Player.App\Services\QBittorrentHandoffClient.cs'
$vm = Read-Source 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$viewPath = 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'
$view = Read-Source $viewPath

foreach ($needle in @(
 'bool UsePartialFiles = true',
 'UsePartialFiles = configuration.UsePartialFiles',
 'UsePartialFiles: string.Equals(directory, _paths.TorrentCacheDirectory')) {
 Require ($models + $mono + $vm) $needle 'safe file naming'
}
foreach ($needle in @(
 'MonoTorrent.Torrent.LoadAsync(source)',
 'metadata.InfoHashes.V1?.ToHex() ?? metadata.InfoHashes.V2?.ToHex()',
 'metadata.Files.Count != session.Files.Count',
 'Path.GetFullPath(file.Path)',
 'IsInside(saveDirectory, full)',
 'File.Exists(full)',
 'if (!anyExistingData)',
 'SmartTorrentHandoffPlan('
)) { Require $planner $needle 'read-only folder and hash preflight' }

foreach ($needle in @(
 'ValidateSameMachineWebUi(address)',
 'CheckSmartHandoffAsync(',
 'AddWithRecheckAsync(',
 'JobExistsAsync(',
 'api/v2/torrents/info?hashes=',
 'form.Add(new StringContent(plan.SaveDirectory, Encoding.UTF8), "savepath")',
 'form.Add(new StringContent("true"), "paused")',
 'form.Add(new StringContent(plan.ContentLayout), "contentLayout")',
 'form.Add(new StringContent("false"), "autoTMM")',
 'form.Add(new StringContent("false"), "skip_checking")',
 'api/v2/torrents/files?hash=',
 'SaveDirectory',
 'api/v2/torrents/recheck',
 'api/v2/torrents/stop',
 'api/v2/torrents/pause',
 'api/v2/torrents/start',
 'api/v2/torrents/resume',
 'await ChangeJobAsync(client, root, stopEndpoint,',
 'AllowAutoRedirect = false',
 'UseCookies = true'
)) { Require $client $needle 'qBittorrent recheck API' }
if ($client.Contains('skip_checking", "true"') -or
    $client.Contains('deleteFiles", "true"') -or
    $client.Contains('api/v2/torrents/delete')) {
 throw 'Destructive, unverified or skip-check torrent API use detected'
}

foreach ($needle in @(
 'SmartHandoffCommand = new AsyncRelayCommand(SmartHandoffAsync)',
 'SmartTorrentHandoffPlanner.CreateAsync(session, directory)',
 '_qbClient.CheckSmartHandoffAsync(',
 '_dialogs.Confirm(LocalizationManager.Get("R5.Torrent.Smart.Confirm"))',
 'await _service.RemoveAsync(session.SessionId, deleteFiles: false)',
 '_qbClient.AddWithRecheckAsync(',
 'QBittorrentStatus = ownershipReleased',
 'string.IsNullOrWhiteSpace(_settings.TorrentDownloadDirectory) &&',
 'return SmartHandoffAsync();'
)) { Require $vm $needle 'single-writer handoff' }

Require $view 'Command="{Binding SmartHandoffCommand}"' 'smart transfer button'
Require $view 'R5.Torrent.Smart.Hint' 'smart transfer tooltip'
$xml = New-Object System.Xml.XmlDocument
$xml.Load((Join-Path $root $viewPath))
foreach ($culture in @('tr-TR', 'en-US')) {
    $relative = "src\AltyaziDB.Player.App\Localization\Strings.$culture.xaml"
    $localized = Read-Source $relative
    $doc = New-Object System.Xml.XmlDocument
    $doc.Load((Join-Path $root $relative))
    foreach ($key in @('R5.Torrent.Smart.Button','R5.Torrent.Smart.Hint',
     'R5.Torrent.Smart.NoSession','R5.Torrent.Smart.ChooseFolder',
     'R5.Torrent.Smart.Checking','R5.Torrent.Smart.Confirm',
     'R5.Torrent.Smart.Submitted','R5.Torrent.Smart.SafeStopped',
     'R5.Torrent.Smart.Failed','R5.Torrent.Smart.UseSmart')) {
      Require $localized ('x:Key="' + $key + '"') "$culture localization"
    }
}
Write-Host '[PASS] R5/P4.11.2 safe single-writer Smart Handoff and qBittorrent recheck contracts' -ForegroundColor Cyan
