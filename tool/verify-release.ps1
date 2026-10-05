param(
    [switch]$AllowMissingInstaller
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$version = Get-ReleaseVersion
$portableDir = Join-Path $root "artifacts\portable"
$portableZip = Join-Path $root "artifacts\release\ADB-Player-Portable-v$version-x64.zip"
$installer = Join-Path $root "artifacts\installer\ADB-Player-Setup-v$version-x64.exe"
$errors = New-Object System.Collections.Generic.List[string]

foreach ($required in @(
    (Join-Path $portableDir "ADB.Player.exe"),
    (Join-Path $portableDir "portable.flag"),
    (Join-Path $portableDir "README.md"),
    (Join-Path $portableDir "README_TR.md"),
    (Join-Path $portableDir "PRIVACY.md"),
    (Join-Path $portableDir "SECURITY.md"),
    (Join-Path $portableDir "THIRD_PARTY_NOTICES.md"),
    (Join-Path $portableDir "LICENSE"),
    (Join-Path $portableDir "RELEASE_NOTES_v1.0.1.md"),
    $portableZip
)) {
    if (-not (Test-Path -LiteralPath $required)) { $errors.Add("Eksik: $required") }
}

$mpv = Get-ChildItem -LiteralPath $portableDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("libmpv-2.dll", "mpv-2.dll", "libmpv.dll") } |
    Select-Object -First 1
if (-not $mpv) { $errors.Add("Portable klasörde libmpv bulunamadı.") }
$rclone = Join-Path $portableDir "native\rclone\rclone.exe"
if (-not (Test-Path -LiteralPath $rclone)) { $errors.Add("Portable klasörde rclone.exe bulunamadı.") }

$ffmpeg = Join-Path $portableDir "native\ffmpeg\bin\ffmpeg.exe"
$ffprobe = Join-Path $portableDir "native\ffmpeg\bin\ffprobe.exe"
if (-not (Test-Path -LiteralPath $ffmpeg)) { $errors.Add("Portable klasörde ffmpeg.exe bulunamadı.") }
if (-not (Test-Path -LiteralPath $ffprobe)) { $errors.Add("Portable klasörde ffprobe.exe bulunamadı.") }

$audioSyncPython = Join-Path $portableDir "native\audiosynctool\python\python.exe"
$audioSyncWorker = Join-Path $portableDir "native\audiosynctool\player_worker.py"
$audioSyncAnalyzer = Join-Path $portableDir "native\audiosynctool\source\audio_sync\core\analyzer.py"
if (-not (Test-Path -LiteralPath $audioSyncPython)) { $errors.Add("Portable klasörde AudioSyncTool python.exe bulunamadı.") }
if (-not (Test-Path -LiteralPath $audioSyncWorker)) { $errors.Add("Portable klasörde AudioSyncTool worker bulunamadı.") }
if (-not (Test-Path -LiteralPath $audioSyncAnalyzer)) { $errors.Add("Portable klasörde AudioSyncTool analyzer bulunamadı.") }

$subtitleSyncRoot = Join-Path $portableDir "native\subtitlesync"
$subtitleSyncExe = $null
if (Test-Path -LiteralPath $subtitleSyncRoot) {
    $subtitleSyncExe = Get-ChildItem -LiteralPath $subtitleSyncRoot -Include @("ffsubsync.exe", "ffs.exe", "subsync.exe") -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $subtitleSyncExe) { $errors.Add("Portable klasörde ffsubsync runtime bulunamadı.") }
if (-not $AllowMissingInstaller -and -not (Test-Path -LiteralPath $installer)) { $errors.Add("Kurulum EXE eksik: $installer") }

if (Test-Path -LiteralPath $portableZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($portableZip)
    try {
        if (-not ($zip.Entries | Where-Object { $_.FullName -eq "ADB.Player.exe" })) { $errors.Add("Portable ZIP içinde EXE yok.") }
        if (-not ($zip.Entries | Where-Object { $_.FullName -eq "portable.flag" })) { $errors.Add("Portable ZIP içinde portable.flag yok.") }
        if (-not ($zip.Entries | Where-Object { $_.FullName -eq "native/rclone/rclone.exe" -or $_.FullName -eq "native\rclone\rclone.exe" })) { $errors.Add("Portable ZIP içinde rclone.exe yok.") }
        if (-not ($zip.Entries | Where-Object { $_.FullName -eq "native/ffmpeg/bin/ffmpeg.exe" -or $_.FullName -eq "native\ffmpeg\bin\ffmpeg.exe" })) { $errors.Add("Portable ZIP içinde ffmpeg.exe yok.") }
        if (-not ($zip.Entries | Where-Object { $_.FullName -eq "native/ffmpeg/bin/ffprobe.exe" -or $_.FullName -eq "native\ffmpeg\bin\ffprobe.exe" })) { $errors.Add("Portable ZIP içinde ffprobe.exe yok.") }
        if (-not ($zip.Entries | Where-Object { $_.FullName -match "^native[\\/]audiosynctool[\\/]python[\\/]python\.exe$" })) { $errors.Add("Portable ZIP içinde AudioSyncTool python.exe yok.") }
        if (-not ($zip.Entries | Where-Object { $_.FullName -match "^native[\\/]audiosynctool[\\/]player_worker\.py$" })) { $errors.Add("Portable ZIP içinde AudioSyncTool worker yok.") }
        if (-not ($zip.Entries | Where-Object { $_.FullName -match "^native[\\/]subtitlesync[\\/].*(ffsubsync|ffs|subsync)\.exe$" })) { $errors.Add("Portable ZIP içinde ffsubsync runtime yok.") }
    } finally { $zip.Dispose() }
}

if ($errors.Count -gt 0) { throw "Yayın doğrulaması başarısız:`n - $($errors -join "`n - ")" }
Write-Host "Build W8 yayın doğrulaması başarılı." -ForegroundColor Green
Write-W5NativeOutputStatus -OutputDirectory $portableDir
