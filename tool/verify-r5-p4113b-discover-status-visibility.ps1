param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vmPath = Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs'
$torrentPath = Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$discoverPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'

foreach ($path in @($vmPath, $torrentPath, $discoverPath)) {
    if (!(Test-Path -LiteralPath $path)) {
        throw "R5/P4.11.3b file missing: $path"
    }
}
$vm = [IO.File]::ReadAllText($vmPath, [Text.Encoding]::UTF8)
$torrent = [IO.File]::ReadAllText($torrentPath, [Text.Encoding]::UTF8)
[xml]$xml = [IO.File]::ReadAllText($discoverPath, [Text.Encoding]::UTF8)

function Require([bool]$condition, [string]$message) {
    if (!$condition) { throw "R5/P4.11.3b FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

Require ($vm.Contains('public bool SelectedStreamIsTorrent => SelectedStream?.Kind == CatalogStreamKind.Torrent;')) 'strongly typed torrent selection (no enum-string trigger comparison)'
Require ($vm.Contains('OnPropertyChanged(nameof(SelectedStreamIsTorrent));')) 'visibility updates when source selection changes'
Require ($torrent.Contains('public bool HasTorrentActivity => Session is not null ||')) 'activity survives reselecting a different stream'

$panel = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='DiscoverTorrentHandoffPanel']]")
Require ($null -ne $panel) 'Discover torrent panel exists'
# In P4.11.3c a runtime screenshot showed that the source/session trigger
# could still hide the entire status panel. Keep the status visible regardless
# of selection while the Discover detail modal is open.
Require ($panel.GetAttribute('Visibility') -eq 'Visible') 'Discover status is unconditionally visible, including when idle'
Require ($null -eq $panel.SelectSingleNode("./*[local-name()='Border.Style']")) 'no fragile source/session conditional Visibility triggers'
$bars = @($panel.SelectNodes(".//*[local-name()='ProgressBar']"))
Require ($bars.Count -eq 1 -and $bars[0].GetAttribute('Value') -eq '{Binding Torrent.HandoffDisplayPercent, Mode=OneWay}') 'read-only progress is crash-safe in WPF'
Require ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and contains(@Text,'Torrent.AutomaticHandoffStatus')]")) 'transfer stage visible'
Require ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and contains(@Text,'Torrent.QBittorrentConnectionStatus')]")) 'qBittorrent connectivity visible'
Require ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and contains(@Text,'Torrent.QBittorrentStatus')]")) 'qBittorrent operation status visible'
Write-Host '[PASS] R5/P4.11.3b Discover torrent status visibility regression checks' -ForegroundColor Cyan
