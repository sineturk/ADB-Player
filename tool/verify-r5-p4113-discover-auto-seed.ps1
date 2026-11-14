param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Src([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.11.3 missing $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([string]$source,[string]$token,[string]$label) {
    if (!$source.Contains($token)) { throw "R5/P4.11.3 $label missing: $token" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$vm = Src 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$main = Src 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$qb = Src 'src\AltyaziDB.Player.App\Services\QBittorrentHandoffClient.cs'
$discoverPath = 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$discover = Src $discoverPath
$torrentPath = 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'
$torrent = Src $torrentPath
$planner = Src 'src\AltyaziDB.Player.Torrent\SmartTorrentHandoffPlanner.cs'

foreach ($part in @(
    'private readonly HashSet<string> _autoHandoffAttemptedSessions',
    'private bool _torrentPlaybackActive;',
    'private bool SelectedDownloadComplete(TorrentSessionSnapshot session)',
    'session.TotalLength > 0 && session.CompletedLength >= session.TotalLength',
    'if (_torrentPlaybackActive)',
    'SetAutomaticHandoffStage("WaitingPlayback")',
    'if (_autoHandoffAttemptedSessions.Contains(current.SessionId)) return;',
    '_autoHandoffAttemptedSessions.Add(current.SessionId);',
    '_ = PerformSmartHandoffAsync(automatic: true, expectedSessionId: current.SessionId);',
    'public void NotifyPlayerSnapshot(string? source, bool isIdle)',
    'public void NotifyPlaybackEnded()',
    '_activeTorrentPlaybackSource = source.LocalPath;',
    '_torrentPlaybackActive = true;',
    'TryStartAutomaticHandoff();',
    'if (!automatic && !_dialogs.Confirm',
    'await _service.RemoveAsync(session.SessionId, deleteFiles: false)',
    'new Progress<QBittorrentHandoffProgress>',
    'SetAutomaticHandoffStage("Started")',
    'SetAutomaticHandoffStage("Failed")',
    'QBittorrentAutoHandoff',
    'public string AutomaticHandoffStatus =>',
    'public double HandoffDisplayPercent =>'
)) { Require $vm $part 'opt-in, completion-gated single-writer auto transfer' }
foreach ($part in @(
    'Torrent.NotifyPlayerSnapshot(snapshot.Source, snapshot.IsIdle);',
    'Torrent.NotifyPlaybackEnded()',
    'Torrent.OpenMagnetOrThrowAsync(',
    'Torrent.OpenTorrentFileOrThrowAsync('
)) { Require $main $part 'Discover and player lifetime integration' }
foreach ($part in @(
    'api/v2/torrents/recheck',
    'EnsureRecheckFinishedAndStartedAsync(',
    'var sawChecking = false;',
    'for (var attempt = 0; attempt < 360; attempt++)',
    'state.StartsWith("checking"',
    'state.StartsWith("stopped"',
    'state.StartsWith("paused"',
    'await ChangeJobAsync(client, root, startEndpoint, hash, token)',
    'state.StartsWith("stalled"',
    'state.Equals("uploading"',
    'state.Equals("downloading"',
    'progress?.Report(new QBittorrentHandoffProgress("Checking"',
    'AllowAutoRedirect = false',
    'ValidateSameMachineWebUi(address)',
    'JobExistsAsync('
)) { Require $qb $part 'qBittorrent finish-recheck and verified start state' }
if ($qb.Contains('api/v2/torrents/setForceStart') -or
    $qb.Contains('api/v2/torrents/delete') -or
    $qb.Contains('"skip_checking", "true"')) {
    throw 'Unsafe force start, deletion, or bypass-checking detected'
}
foreach ($part in @(
    'x:Name="DiscoverTorrentHandoffPanel"',
    'Visibility="Visible"',
    'Text="{Binding Torrent.AutomaticHandoffStatus}"',
    'Value="{Binding Torrent.HandoffDisplayPercent, Mode=OneWay}"',
    'Text="{Binding Torrent.QBittorrentStatus, Converter={StaticResource UserFacingTextConverter}}"',
    'Text="{Binding Torrent.QBittorrentConnectionStatus}"',
    'Command="{Binding ShowTorrentCommand}"'
)) { Require $discover $part 'visible Discover progress and qBittorrent state' }
foreach ($part in @(
    'Text="{Binding AutomaticHandoffStatus}"',
    'IsChecked="{Binding QBittorrentAutoHandoff, Mode=TwoWay}"',
    'Command="{Binding SmartHandoffCommand}"'
)) { Require $torrent $part 'Torrent tab transfer state and optional auto choice' }
Require $planner 'SmartTorrentHandoffPlan(' 'magnet and metadata preflight retained'
foreach ($p in @($discoverPath,$torrentPath)) {
    $xml = New-Object Xml.XmlDocument
    $xml.Load((Join-Path $root $p))
}
foreach ($lang in @('tr-TR','en-US')) {
    $path = "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
    $text = Src $path
    $xml = New-Object Xml.XmlDocument
    $xml.Load((Join-Path $root $path))
    foreach ($key in @('R5.Torrent.Auto.PanelTitle','R5.Torrent.Auto.GoToTorrent',
      'R5.Torrent.Auto.Off','R5.Torrent.Auto.Downloading',
      'R5.Torrent.Auto.WaitingPlayback','R5.Torrent.Auto.Checking',
      'R5.Torrent.Auto.Transferring','R5.Torrent.Auto.Verifying',
      'R5.Torrent.Auto.Starting','R5.Torrent.Auto.Seeding',
      'R5.Torrent.Auto.Started','R5.Torrent.Auto.Failed',
      'R5.Torrent.Qb.AutoSharedFolderWarning')) {
        Require $text ('x:Key="' + $key + '"') "$lang localized auto status"
    }
}
Write-Host '[PASS] R5/P4.11.3 Discover automatic safe handoff and qBittorrent recheck/start' -ForegroundColor Cyan
