param([string]$ProjectRoot='')

$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){
    . "$PSScriptRoot\common.ps1"
    $ProjectRoot=Get-ProjectRoot
}
$root=(Resolve-Path $ProjectRoot).Path

function Need([string]$rel,[string]$text){
    $p=Join-Path $root $rel
    if(-not(Test-Path $p)){ throw "Dosya yok: $rel" }
    $c=[IO.File]::ReadAllText($p)
    if(-not $c.Contains($text)){ throw "Marker yok: $rel :: $text" }
}

Need 'src\AltyaziDB.Player.SubtitleSync\Models\SubtitleSyncModels.cs' 'ReferenceAudioStreamIndex'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\FfsubsyncSubtitleSyncService.cs' '--reference-stream'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\FfsubsyncSubtitleSyncService.cs' 'AssessSafeApplyAsync'
Need 'src\AltyaziDB.Player.SubtitleSync\Services\FfsubsyncSubtitleSyncService.cs' 'Subtitle Safe Apply Gate reddetti'
Need 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs' 'ResolveSubtitleSyncReferenceAudioStreamIndex'

Write-Host 'Subtitle Sync Safe Apply Gate static verification passed.' -ForegroundColor Green
