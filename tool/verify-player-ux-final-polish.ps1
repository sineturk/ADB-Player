param([string]$ProjectRoot='')

$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ProjectRoot)){
    . "$PSScriptRoot\common.ps1"
    $ProjectRoot=Get-ProjectRoot
}
$root=(Resolve-Path $ProjectRoot).Path

function Need([string]$rel,[string]$text){
    $p=Join-Path $root $rel
    if(-not(Test-Path $p)){ throw "Missing file: $rel" }
    $c=[IO.File]::ReadAllText($p)
    if(-not $c.Contains($text)){ throw "Missing marker: $rel :: $text" }
}

function Forbid([string]$rel,[string]$text){
    $p=Join-Path $root $rel
    $c=[IO.File]::ReadAllText($p)
    if($c.Contains($text)){ throw "Forbidden marker: $rel :: $text" }
}

Need 'src\AltyaziDB.Player.App\MainWindow.xaml' 'PlayerSettingsScrollBarStyle'
Need 'src\AltyaziDB.Player.App\MainWindow.xaml' 'AudioSyncActionText'
Need 'src\AltyaziDB.Player.App\MainWindow.xaml' 'SubtitleSyncActionText'
Need 'src\AltyaziDB.Player.App\MainWindow.xaml' 'IsAudioSyncActionComplete'
Need 'src\AltyaziDB.Player.App\MainWindow.xaml' 'IsSubtitleSyncActionComplete'
Need 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs' 'public string AudioSyncActionText'
Need 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs' 'public string SubtitleSyncActionText'
Forbid 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs' 'if (!AutoAudioSyncEnabled) return;'
Need 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml' 'Player.AudioSync.Action.Running'
Need 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml' 'Player.SubtitleSync.Action.Running'
Need 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml' 'Player.AudioSync.Action.Resync'
Need 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml' 'Player.SubtitleSync.Action.Resync'

Write-Host 'Player UX Final Polish static verification passed.' -ForegroundColor Green
