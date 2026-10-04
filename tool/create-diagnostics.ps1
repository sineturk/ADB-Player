param(
    [string]$OutputDirectory = ""
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\diagnostics"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$temp = Join-Path $env:TEMP "adb-player-diag-$stamp"
$zip = Join-Path $OutputDirectory "ADB-Player-Developer-Diagnostics-$stamp.zip"
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $dotnet = Get-DotNetExecutable
    & $dotnet --info 2>&1 | Out-File -LiteralPath (Join-Path $temp "dotnet-info.txt") -Encoding utf8
    & "$PSScriptRoot\verify-w5-native.ps1" 2>&1 | Out-File -LiteralPath (Join-Path $temp "native-status.txt") -Encoding utf8
    Get-ChildItem Env: | Sort-Object Name | Where-Object { $_.Name -notmatch 'TOKEN|KEY|SECRET|PASSWORD|PASS|AUTH' } |
        Format-Table -AutoSize | Out-String | Out-File -LiteralPath (Join-Path $temp "environment.txt") -Encoding utf8

    $localLogs = Join-Path $env:LOCALAPPDATA "AltyaziDB\Player\logs"
    if (Test-Path -LiteralPath $localLogs) {
        Copy-Item -LiteralPath $localLogs -Destination (Join-Path $temp "logs") -Recurse -Force
    }
    $portableLogs = Join-Path $root "artifacts\portable\data\logs"
    if (Test-Path -LiteralPath $portableLogs) {
        Copy-Item -LiteralPath $portableLogs -Destination (Join-Path $temp "portable-logs") -Recurse -Force
    }

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $temp '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Tanılama paketi hazır: $zip" -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
