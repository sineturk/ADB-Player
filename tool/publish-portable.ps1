param(
    [switch]$SkipZip
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$dotnet = Get-DotNetExecutable
$config = Get-NuGetConfigPath
$version = Get-ReleaseVersion
$project = Join-Path $root "src\AltyaziDB.Player.App\AltyaziDB.Player.App.csproj"
$output = Join-Path $root "artifacts\portable"
$release = Join-Path $root "artifacts\release"
$zip = Join-Path $release "ADB-Player-Portable-v$version-x64.zip"
Set-Location $root

& "$PSScriptRoot\verify-environment.ps1"
$rclone = Join-Path $root "native\rclone\rclone.exe"
if (-not (Test-Path -LiteralPath $rclone)) {
    & "$PSScriptRoot\vendor-rclone.ps1"
}
if (-not (Test-Path -LiteralPath $rclone)) { throw "Doğrulanmış rclone.exe eklenemedi." }

# Public V1.0 release must be reproducible with the advertised sync engines.
$ffmpeg = Join-Path $root "native\ffmpeg\bin\ffmpeg.exe"
$ffprobe = Join-Path $root "native\ffmpeg\bin\ffprobe.exe"
if (-not ((Test-Path -LiteralPath $ffmpeg) -and (Test-Path -LiteralPath $ffprobe))) {
    & "$PSScriptRoot\vendor-ffmpeg.ps1" -ProjectRoot $root
}
if (-not ((Test-Path -LiteralPath $ffmpeg) -and (Test-Path -LiteralPath $ffprobe))) {
    throw "V1.0 release icin FFmpeg/FFprobe runtime hazirlanamadi."
}

& "$PSScriptRoot\vendor-audiosynctool.ps1" -ProjectRoot $root
$audioSyncPython = Join-Path $root "native\audiosynctool\python\python.exe"
$audioSyncWorker = Join-Path $root "native\audiosynctool\player_worker.py"
$audioSyncAnalyzer = Join-Path $root "native\audiosynctool\source\audio_sync\core\analyzer.py"
if (-not ((Test-Path -LiteralPath $audioSyncPython) -and (Test-Path -LiteralPath $audioSyncWorker) -and (Test-Path -LiteralPath $audioSyncAnalyzer))) {
    throw "V1.0 release icin AudioSyncTool runtime hazirlanamadi."
}

& "$PSScriptRoot\vendor-subtitlesync.ps1" -ProjectRoot $root
$subtitleSyncExe = Get-ChildItem -LiteralPath (Join-Path $root "native\subtitlesync") -Include @("ffsubsync.exe", "ffs.exe", "subsync.exe") -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $subtitleSyncExe) {
    throw "V1.0 release icin ffsubsync runtime hazirlanamadi."
}

& "$PSScriptRoot\generate-app-credentials.ps1"

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path $release -Force | Out-Null

& $dotnet restore $project --configfile $config --runtime win-x64 --force-evaluate
if ($LASTEXITCODE -ne 0) { throw "Portable yayın için NuGet geri yüklemesi başarısız oldu." }

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $output `
    -p:Platform=x64 `
    -p:UseAppHost=true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Portable yayın derlemesi başarısız oldu." }

$exe = Join-Path $output "ADB.Player.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Portable EXE üretilemedi: $exe" }

Copy-LibMpvNativeRuntime -Destination $output -Required | Out-Null
Copy-W5OptionalNativeRuntime -Destination $output | Out-Null

foreach ($feature in @("ffmpeg", "audiosynctool", "subtitlesync")) {
    $source = Join-Path (Join-Path $root "native") $feature
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Release native runtime eksik: $source"
    }
    $target = Join-Path (Join-Path $output "native") $feature
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force
    }
}
$mpv = Get-ChildItem $output -Recurse -File | Where-Object { $_.Name -in @('libmpv-2.dll', 'mpv-2.dll', 'libmpv.dll') } | Select-Object -First 1
if (-not $mpv) { throw "Yayın klasöründe libmpv DLL bulunamadı." }

Set-Content -LiteralPath (Join-Path $output "portable.flag") -Value "ADB Player portable data mode" -Encoding UTF8
Copy-Item "$root\README_PUBLIC.md" "$output\README.md" -Force
Copy-Item "$root\README_PUBLIC_TR.md" "$output\README_TR.md" -Force
Copy-Item "$root\PRIVACY.md" "$output\PRIVACY.md" -Force
Copy-Item "$root\SECURITY.md" "$output\SECURITY.md" -Force
Copy-Item "$root\RELEASE_NOTES_v1.0.1.md" "$output\RELEASE_NOTES_v1.0.1.md" -Force
Copy-Item "$root\THIRD_PARTY_NOTICES.md" "$output\THIRD_PARTY_NOTICES.md" -Force
Copy-Item "$root\LICENSE" "$output\LICENSE" -Force
Copy-Item "$root\LICENSE_GPL-3.0.txt" "$output\LICENSE_GPL-3.0.txt" -Force

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Portable ZIP: $zip" -ForegroundColor Green
    Write-Host "SHA-256: $(Get-Sha256 -Path $zip)" -ForegroundColor DarkGray
}

Write-Host "Portable yayın hazır." -ForegroundColor Green
Write-Host "EXE: $exe"
Write-Host "libmpv: $($mpv.FullName)"
Write-W5NativeOutputStatus -OutputDirectory $output
