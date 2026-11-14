param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$torrentVm = Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
if (!(Test-Path -LiteralPath $path) -or !(Test-Path -LiteralPath $torrentVm)) {
    throw 'R5/P4.11.3c required source files missing.'
}
[xml]$xml = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
$vm = [IO.File]::ReadAllText($torrentVm, [Text.Encoding]::UTF8)
function Assert([bool]$condition,[string]$message) {
    if (!$condition) { throw "R5/P4.11.3c FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}
$panel = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='DiscoverTorrentHandoffPanel']]")
Assert ($null -ne $panel) 'Discover handoff status panel present'
Assert ($panel.GetAttribute('Visibility') -eq 'Visible') 'panel always visible, even before first torrent starts'
Assert ($null -eq $panel.SelectSingleNode("./*[local-name()='Border.Style']")) 'no WPF Visibility gates hide the status'
Assert ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and contains(@Text, 'Torrent.SessionTitle')]")) 'source session is displayed'
Assert ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and contains(@Text, 'Torrent.AutomaticHandoffStatus')]")) 'handoff stage is displayed'
Assert ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and contains(@Text, 'Torrent.QBittorrentStatus')]")) 'qB transfer result is displayed'
Assert ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and contains(@Text, 'Torrent.QBittorrentConnectionStatus')]")) 'qB connection state is displayed'
$bars = @($panel.SelectNodes(".//*[local-name()='ProgressBar']"))
Assert ($bars.Count -eq 1) 'one download/handoff progress bar'
Assert ($bars[0].GetAttribute('Value') -eq '{Binding Torrent.HandoffDisplayPercent, Mode=OneWay}') 'WPF ProgressBar read-only binding cannot throw on startup'
Assert ($vm.Contains('public double HandoffDisplayPercent => Session?.ProgressPercent ?? _handoffProgressPercent;')) 'percentage is linked to torrent session or last handoff'
Assert ($vm.Contains('public string AutomaticHandoffStatus =>')) 'idle and handoff status available for UI'
Write-Host '[PASS] R5/P4.11.3c permanently visible Discover transfer status source tests' -ForegroundColor Cyan
