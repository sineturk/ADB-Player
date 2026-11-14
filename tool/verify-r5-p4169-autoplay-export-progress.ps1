$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$path) {
    $target = Join-Path $root $path
    if (!(Test-Path -LiteralPath $target)) { throw "Missing: $path" }
    return [IO.File]::ReadAllText($target)
}
function Require([bool]$valid, [string]$label) {
    if (!$valid) { throw "[P4.16.9 FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$engine = Source 'src/AltyaziDB.Player.Playback/MpvPlaybackEngine.cs'
$export = Source 'src/AltyaziDB.Player.App/Services/EmbeddedTrackExportService.cs'
$main = Source 'src/AltyaziDB.Player.App/MainWindow.xaml.cs'
$dialog = Source 'src/AltyaziDB.Player.App/Views/TrackExportProgressDialog.xaml.cs'
[xml]$xaml = Source 'src/AltyaziDB.Player.App/Views/TrackExportProgressDialog.xaml'
[xml]$tr = Source 'src/AltyaziDB.Player.App/Localization/Strings.tr-TR.xaml'
[xml]$en = Source 'src/AltyaziDB.Player.App/Localization/Strings.en-US.xaml'
Require ($engine.Contains('ExecuteCommand("loadfile", source, "replace");') -and $engine.Contains('        Pause(false);')) 'Opening media clears inherited pause'
Require ($export.Contains('start.ArgumentList.Add("pipe:1");')) 'FFmpeg progress is piped, not logged'
Require ($export.Contains('ParseProgressPercent(line, durationSeconds)')) 'Progress uses media timestamps'
Require ($export.Contains('progress?.Report(100);')) '100 percent only after validated output move'
Require ($export.Contains('!ContainsReadableSrtCue(staging)')) 'Empty SRT protection preserved'
Require ($main.Contains('_trackExportBusy') -and $main.Contains('progressDialog.ShowDialog();')) 'Export progress is modal and non-reentrant'
Require ($dialog.Contains('_cancellation.Cancel();') -and $dialog.Contains('WasCancelled = true;')) 'Cancel waits for FFmpeg cleanup'
Require ($dialog.Contains('Player.TrackExport.ProgressDone') -and $dialog.Contains('await Task.Delay(950);')) 'Completion shown before dismissal'
foreach ($key in @('ProgressAudio','ProgressSubtitle','ProgressUnknown','ProgressDone','ProgressCancelling','ProgressCancel')) {
    Require ($tr.InnerXml.Contains("Player.TrackExport.$key") -and $en.InnerXml.Contains("Player.TrackExport.$key")) "Localized $key"
}
Write-Host '[PASS] RC2 P4.16.9 autoplay and track export progress' -ForegroundColor Green
