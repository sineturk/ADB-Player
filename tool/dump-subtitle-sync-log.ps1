param(
    [int]$Tail = 400,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$patterns = @(
    'SubtitleSync',
    'Subtitle Timeline',
    'Original Audio Resolver',
    'ffsubsync',
    'Safe Apply Gate',
    'model=',
    'coverage=',
    'residual=',
    'score=',
    'fps='
)

$searchRoots = @(
    (Join-Path $env:LOCALAPPDATA 'ADB\Player\logs'),
    (Join-Path $env:LOCALAPPDATA 'AltyaziDB\Player\logs'),
    (Join-Path $root 'src\AltyaziDB.Player.App\bin'),
    (Join-Path $root 'artifacts')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

$logs = foreach ($searchRoot in $searchRoots) {
    if ((Get-Item -LiteralPath $searchRoot).PSIsContainer) {
        Get-ChildItem -LiteralPath $searchRoot -Filter 'player-*.log' -File -Recurse -ErrorAction SilentlyContinue
    }
}

$logs = @($logs | Sort-Object LastWriteTime -Descending)
if ($logs.Count -eq 0) {
    Write-Host 'No ADB Player log file was found.' -ForegroundColor Yellow
    Write-Host 'Searched:' -ForegroundColor DarkGray
    $searchRoots | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    exit 2
}

$selected = if ($All) { $logs } else { @($logs[0]) }

foreach ($log in $selected) {
    Write-Host ''
    Write-Host "LOG: $($log.FullName)" -ForegroundColor Cyan
    Write-Host "LastWrite: $($log.LastWriteTime.ToString('O'))" -ForegroundColor DarkGray

    $lines = Get-Content -LiteralPath $log.FullName -Tail $Tail -ErrorAction Stop
    $matched = $lines | Select-String -Pattern $patterns -CaseSensitive:$false

    if (-not $matched) {
        Write-Host 'No Subtitle Sync diagnostics found in the selected tail.' -ForegroundColor Yellow
        continue
    }

    $matched | ForEach-Object { $_.Line }
}
