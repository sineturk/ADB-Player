param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$target = Join-Path $ProjectRoot "native\ffmpeg\bin"
$ffmpeg = Join-Path $target "ffmpeg.exe"
$ffprobe = Join-Path $target "ffprobe.exe"

if ((Test-Path -LiteralPath $ffmpeg) -and (Test-Path -LiteralPath $ffprobe)) {
    Write-Host "FFmpeg bileşeni zaten hazır." -ForegroundColor Green
    exit 0
}

$url = "https://github.com/mifi/ffmpeg-builds/releases/download/8.0-1/ffmpeg-n8.0-latest-win64-gpl-shared-8.0.zip"
$expectedHash = "0836CF94503B3497BFF0497EEBDA4E74F34017AB6D85C2D8E69F3F9900D5A496"
$tempRoot = Join-Path $env:TEMP "altyazidb-ffmpeg-$([Guid]::NewGuid().ToString('N'))"
$zip = Join-Path $tempRoot "ffmpeg.zip"
$extract = Join-Path $tempRoot "extract"

try {
    New-Item -ItemType Directory -Path $tempRoot, $extract -Force | Out-Null
    Write-Host "FFmpeg 8.0 indiriliyor..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "FFmpeg SHA-256 doğrulaması başarısız. Beklenen: $expectedHash Bulunan: $actualHash"
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $foundFfmpeg = Get-ChildItem -LiteralPath $extract -Filter "ffmpeg.exe" -File -Recurse | Select-Object -First 1
    if (-not $foundFfmpeg) { throw "FFmpeg arşivinde ffmpeg.exe bulunamadı." }
    $binDir = $foundFfmpeg.Directory.FullName
    if (-not (Test-Path -LiteralPath (Join-Path $binDir "ffprobe.exe"))) {
        throw "FFmpeg arşivinde ffprobe.exe bulunamadı."
    }

    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath $binDir -File | Copy-Item -Destination $target -Force

    & $ffmpeg -hide_banner -version | Select-Object -First 1 | Write-Host
    Write-Host "FFmpeg bileşeni hazır: $target" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
