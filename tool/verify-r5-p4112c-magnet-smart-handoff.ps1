param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$name) {
  $path = Join-Path $root $name
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.11.2c missing $name" }
  [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([string]$s,[string]$part,[string]$label) {
  if (!$s.Contains($part)) { throw "R5/P4.11.2c missing $label : $part" }
  Write-Host "[PASS] $label" -ForegroundColor Green
}
$planner = Source 'src\AltyaziDB.Player.Torrent\SmartTorrentHandoffPlanner.cs'
$client = Source 'src\AltyaziDB.Player.App\Services\QBittorrentHandoffClient.cs'
$vm = Source 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$main = Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$mono = Source 'src\AltyaziDB.Player.Torrent\MonoTorrentStreamingService.cs'
$sourceModel = Source 'src\AltyaziDB.Player.Core\Models\TorrentModels.cs'
foreach($token in @(
 'source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)',
 'hash = ReadMagnetInfoHash(source);',
 'pair[..equal].Equals("xt", StringComparison.OrdinalIgnoreCase)',
 'value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase)',
 'encoded.Length == 40 && encoded.All(Uri.IsHexDigit)',
 'encoded.Length == 32',
 'DecodeBase32(encoded)',
 'MonoTorrent.Torrent.LoadAsync(source)',
 'metadata.Files.Count != session.Files.Count',
 'new FileInfo(full).Length',
 'IsInside(saveDirectory, full)',
 'files.All(path => IsInside(namedRoot, path))',
 'useSubfolder ? "Subfolder" : "NoSubfolder"',
 'if (!anyExistingData)'
)) { Require $planner $token 'magnet, source identity and actual disk layout' }
if($planner.Contains('!session.Source.EndsWith(".torrent"')) {
 throw 'Regression: magnet sources blocked by original-torrent-only preflight'
}
foreach($token in @(
 'if (plan.IsMagnet)',
 'form.Add(new StringContent(plan.TorrentFile, Encoding.UTF8), "urls")',
 'form.Add(attachment, "torrents", "adb-player.torrent")',
 'form.Add(new StringContent(plan.SaveDirectory, Encoding.UTF8), "savepath")',
 'form.Add(new StringContent("true"), "paused")',
 'form.Add(new StringContent("true"), "stopped")',
 'form.Add(new StringContent(plan.ContentLayout), "contentLayout")',
 'form.Add(new StringContent("false"), "skip_checking")',
 'ReadJobFilesAsync(',
 'api/v2/torrents/files?hash=',
 'await ChangeJobAsync(client, root, startEndpoint, plan.TorrentHash',
 'await ChangeJobAsync(client, root, stopEndpoint, plan.TorrentHash',
 'if (!MatchExpectedFiles(plan, fileNames))',
 'MatchExpectedFiles(',
 'if (names.Count != plan.ExpectedFiles.Count) return false;',
 'api/v2/torrents/recheck',
 'api/v2/torrents/stop',
 'api/v2/torrents/pause',
 'api/v2/torrents/start',
 'api/v2/torrents/resume',
 'await ChangeJobAsync(client, root, startEndpoint,',
 'AllowAutoRedirect = false',
 'ValidateSameMachineWebUi(address)',
 'JobExistsAsync('
)) { Require $client $token 'paused add, metadata recovery, recheck' }
if($client.Contains('api/v2/torrents/delete') -or $client.Contains('"skip_checking", "true"')) {
 throw 'Unsafe deletion or forced skip-checking present'
}
foreach($token in @(
 'return SmartHandoffAsync();',
 'await _service.RemoveAsync(session.SessionId, deleteFiles: false)',
 'var plan = await SmartTorrentHandoffPlanner.CreateAsync(session, directory)',
 'await _qbClient.CheckSmartHandoffAsync(',
 'await _qbClient.AddWithRecheckAsync(',
 'LocalizationManager.TranslateMessage(exception.Message)',
 '_dialogs.Confirm(LocalizationManager.Get("R5.Torrent.Smart.Confirm"))'
)) { Require $vm $token 'shared-folder single-writer handoff' }
Require $main 'Torrent.OpenMagnetOrThrowAsync(' 'discover opens shared torrent viewmodel'
Require $mono 'CreateContainingDirectory = true' 'mono source paths preserved'
Require $mono 'file.FullPath' 'actual disk file paths in snapshot'
Require $sourceModel 'string Source,' 'snapshot source preserved'
Write-Host '[PASS] R5/P4.11.2c magnet and actual-path smart transfer regression contract' -ForegroundColor Cyan
