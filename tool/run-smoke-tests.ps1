param(
    [switch]$Launch
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
& "$PSScriptRoot\verify-environment.ps1"
& "$PSScriptRoot\build-debug.ps1"
& "$PSScriptRoot\verify-w5-native.ps1"

$binRoot = Join-Path $root "src\AltyaziDB.Player.App\bin"
$exe = Get-LatestDebugExe -BinRoot $binRoot
if (-not $exe) { throw "Smoke test: Debug EXE bulunamadı." }
$mpv = Get-ChildItem -LiteralPath $exe.Directory.FullName -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("libmpv-2.dll", "mpv-2.dll", "libmpv.dll") } | Select-Object -First 1
if (-not $mpv) { throw "Smoke test: libmpv bulunamadı." }
$rclone = Join-Path $exe.Directory.FullName "native\rclone\rclone.exe"
if (-not (Test-Path -LiteralPath $rclone)) { throw "Smoke test: rclone.exe bulunamadı." }
$tdjsonCandidates = @(
    (Join-Path $exe.Directory.FullName "tdjson.dll"),
    (Join-Path $exe.Directory.FullName "runtimes\win-x64\native\tdjson.dll")
)
if (-not ($tdjsonCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)) {
    throw "Smoke test: tdjson.dll bulunamadı."
}

$required = @(
    "ADB.Player.Addons.dll",
    "ADB.Player.Connections.dll",
    "ADB.Player.Core.dll",
    "ADB.Player.Cloud.dll",
    "ADB.Player.Infrastructure.dll",
    "ADB.Player.Playback.dll",
    "ADB.Player.Library.dll",
    "ADB.Player.Subtitles.dll",
    "ADB.Player.Sources.dll",
    "ADB.Player.Telegram.dll",
    "ADB.Player.Torrent.dll",
    "MonoTorrent.Client.dll"
)
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $exe.Directory.FullName $file))) { throw "Smoke test: eksik çıktı $file" }
}

Write-Host "Build W8 otomatik smoke kontrolleri başarılı." -ForegroundColor Green
Write-Host "EXE: $($exe.FullName)"
if ($Launch) { & $exe.FullName }
