param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$viewPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$vmPath = Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$torrentViewPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'

foreach ($path in @($viewPath, $vmPath, $torrentViewPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "R5/P4.11.3a missing file: $path"
    }
}

$vmSource = [IO.File]::ReadAllText($vmPath, [Text.Encoding]::UTF8)
if ($vmSource -notmatch 'public\s+double\s+HandoffDisplayPercent\s*=>') {
    throw 'R5/P4.11.3a expected HandoffDisplayPercent to be a read-only computed property.'
}
Write-Host '[PASS] HandoffDisplayPercent is a read-only view model property' -ForegroundColor Green

# ProgressBar.Value uses a TwoWay binding by default in WPF. Binding it
# implicitly to a getter-only property throws InvalidOperationException
# during initial window layout, before the player is usable.
[xml]$discoverXml = [IO.File]::ReadAllText($viewPath, [Text.Encoding]::UTF8)
$bars = @($discoverXml.SelectNodes("//*[local-name()='ProgressBar']") |
    Where-Object { $_.GetAttribute('Value') -match 'HandoffDisplayPercent' })
if ($bars.Count -ne 1) {
    throw "R5/P4.11.3a expected one Discover handoff ProgressBar, found $($bars.Count)."
}

$expected = '{Binding Torrent.HandoffDisplayPercent, Mode=OneWay}'
foreach ($bar in $bars) {
    $binding = $bar.GetAttribute('Value')
    if ($binding -cne $expected) {
        throw "R5/P4.11.3a invalid read-only ProgressBar binding: $binding. Expected: $expected"
    }
}
Write-Host '[PASS] Discover read-only progress binding explicitly uses Mode=OneWay' -ForegroundColor Green

# Keep the old Torrent tab progress bindings read-only too.
[xml]$torrentXml = [IO.File]::ReadAllText($torrentViewPath, [Text.Encoding]::UTF8)
$existing = @($torrentXml.SelectNodes("//*[local-name()='ProgressBar']"))
if ($existing.Count -lt 1) { throw 'R5/P4.11.3a Torrent ProgressBars not found.' }
foreach ($bar in $existing) {
    $binding = $bar.GetAttribute('Value')
    if ($binding -match 'ProgressPercent' -and $binding -notmatch '\bMode=OneWay\b') {
        throw "R5/P4.11.3a Torrent ProgressBar must use OneWay: $binding"
    }
}
Write-Host '[PASS] Torrent tab read-only ProgressBar bindings remain OneWay' -ForegroundColor Green

$label = @($discoverXml.SelectNodes("//*[local-name()='TextBlock']") |
    Where-Object { $_.GetAttribute('Text') -match 'Torrent\.HandoffDisplayPercent' })
if ($label.Count -ne 1) { throw 'R5/P4.11.3a Discover percentage label missing.' }
Write-Host '[PASS] Discover numerical percentage label retained' -ForegroundColor Green
Write-Host '[PASS] R5/P4.11.3a startup XAML binding regression checks' -ForegroundColor Cyan
