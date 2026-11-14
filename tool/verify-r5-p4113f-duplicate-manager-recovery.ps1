param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "R5/P4.11.3f missing file: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw "R5/P4.11.3f FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}
$service = Read 'src\AltyaziDB.Player.Torrent\MonoTorrentStreamingService.cs'
$vm = Read 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'

Require ($service.Contains('private readonly SemaphoreSlim _registryGate = new(1, 1);')) 'Add/Remove use a single ownership gate'
Require ($service.Contains('magnet.InfoHashes, magnetUri.Trim()')) 'magnet re-open is keyed by real info hashes'
Require ($service.Contains('torrent.InfoHashes, torrentFilePath')) '.torrent re-open is keyed by real info hashes'
Require ($service.Contains('var existing = engine.Torrents.FirstOrDefault(')) 'searches registered engine managers before adding'
Require ($service.Contains('candidate => candidate.InfoHashes.Equals(infoHashes)')) 'compares exact torrent hashes, not a filename or title'
Require ($service.Contains('ReferenceEquals(session.Manager, existing)')) 'reuses tracked manager and session instead of duplicating it'
Require ($service.Contains('if (existing.StreamProvider is null)')) 'rejects an existing manager that cannot stream'
Require ($service.Contains('var recovered = new SessionState(')) 'orphaned registered streaming manager is recoverable'
Require ($service.Contains('if (session.Manager.State == TorrentState.Stopped)')) 'already downloading manager is not restarted'
Require ($service.Contains('if (created)') -and $service.Contains('await RemoveFailedSessionAsync(')) 'reused manager is never deleted after an independent timeout'
Require ($service.Contains('await _registryGate.WaitAsync(cancellationToken)')) 'registration and removal are serialized'
Require ($service.Contains('if (!_sessions.TryGetValue(sessionId, out var session)) return;')) 'remove reads the active registered session'
Require ($service.Contains('_sessions.TryRemove(sessionId, out _);')) 'session removed from registry only after engine succeeds'
Require ($service.IndexOf('await _engine.RemoveAsync(') -lt $service.IndexOf('_sessions.TryRemove(sessionId, out _);')) 'engine unregister precedes registry cleanup'
Require ($service.Contains('if (removed)') -and $service.Contains('session.StreamGate.Dispose();')) 'stream gate is retained when engine cleanup fails'
Require (-not $service.Contains('if (!_sessions.TryRemove(session.Id, out _))')) 'old orphan-causing eager removal deleted'
Require ($service.Contains('RemoveMode.KeepAllData')) 'normal replacement leaves files on disk'
Require ($vm.Contains('if (_torrentPlaybackActive)')) 'active mpv playback cannot be discarded by source reselection'
Require ($vm.Contains('R5.Torrent.Discover.ActivePlayback')) 'source replacement communicates why it is blocked'
Require ($vm.Contains('R5.Torrent.Discover.CleanupFailed')) 'engine unregister failure is surfaced'
Require ($vm.Contains('if (Session is null) ClearSessionUi();')) 'failed retries do not hide still-registered torrent sessions'
Require ($vm.Contains('deleteFiles: false')) 'replacement preserves downloaded data'
foreach ($language in @('tr-TR','en-US')) {
    $loc = Read "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"
    [xml]$xml = $loc
    foreach ($key in @('ActivePlayback','CleanupFailed')) {
        Require ($loc.Contains('x:Key="R5.Torrent.Discover.' + $key + '"')) "$language explanation $key"
    }
}
Write-Host '[PASS] R5/P4.11.3f MonoTorrent duplicate manager recovery and data safety' -ForegroundColor Cyan
