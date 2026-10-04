param([string]$ProjectRoot='')

$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){
    . "$PSScriptRoot\common.ps1"
    $ProjectRoot=Get-ProjectRoot
}
$root=(Resolve-Path $ProjectRoot).Path

function Need([string]$rel,[string]$text){
    $p=Join-Path $root $rel
    if(-not(Test-Path $p)){throw "Dosya yok: $rel"}
    $c=[IO.File]::ReadAllText($p)
    if(-not $c.Contains($text)){throw "Marker yok: $rel :: $text"}
}

Need 'src\AltyaziDB.Player.App\Services\OriginalAudioResolver.cs' 'OriginalAudioResolver'
Need 'src\AltyaziDB.Player.Core\Models\MediaTrack.cs' 'IsDefault = false'
Need 'src\AltyaziDB.Player.Playback\MpvPlaybackEngine.cs' '$"{prefix}/default"'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'AnalyzeWindows'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'WeightedLinearRegression'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' '25.0 * 1001.0 / 24000.0'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'TryBuildKnownFrameRateModel'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'TryResolveKnownFrameRateHypothesis'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'FindBestGlobalOffset'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'finalAccepted'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'AcceptanceMode'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'canonical cadence'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'VerificationResult'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'MinimumVerificationCoverage = 0.55'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'VerifyLegacyCadence'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\SubtitleTimelineAnalyzer.cs' 'legacy-crosscheck'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\FfsubsyncSubtitleSyncService.cs' 'ADB Subtitle Timeline v2'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\FfsubsyncSubtitleSyncService.cs' 'AssessSafeApplyAsync'

Write-Host 'Subtitle Sync v2 Original Audio + Timeline Engine static verification passed.' -ForegroundColor Green
