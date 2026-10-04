param(
    [string]$Version = "1.74.4",
    [switch]$Force
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$expectedVersion = "1.74.4"
$expectedZipHash = "EF097EF9DE37A57FEB7D9F9C7AFB34148AD3C65BE8025F1D8F7F521554A701EA"
if ($Version -ne $expectedVersion) {
    throw "Bu paket yalnız doğrulanmış rclone $expectedVersion sürümünü kabul eder."
}

$target = Join-Path $root "native\rclone"
$exe = Join-Path $target "rclone.exe"
if ((Test-Path -LiteralPath $exe) -and -not $Force) {
    $versionOutput = & $exe version 2>$null | Select-Object -First 1
    Write-Host "rclone zaten mevcut: $versionOutput" -ForegroundColor Green
    exit 0
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("altyazidb-rclone-" + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $temp "rclone.zip"
$extract = Join-Path $temp "extract"
$url = "https://downloads.rclone.org/v$Version/rclone-v$Version-windows-amd64.zip"

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Write-Host "rclone indiriliyor: $url" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedZipHash) {
        throw "rclone ZIP SHA-256 doğrulaması başarısız. Beklenen: $expectedZipHash, bulunan: $actualHash"
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $sourceExe = Get-ChildItem -LiteralPath $extract -Filter "rclone.exe" -File -Recurse | Select-Object -First 1
    if (-not $sourceExe) { throw "İndirilen arşivde rclone.exe bulunamadı." }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $sourceExe.FullName -Destination $exe -Force
    foreach ($name in @('README.txt', 'README.html', 'rclone.1', 'git-log.txt')) {
        $file = Get-ChildItem -LiteralPath $extract -Filter $name -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($file) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $name) -Force }
    }

    $result = & $exe version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "rclone.exe çalışma doğrulaması başarısız: $result" }
    Write-Host "rclone doğrulandı ve eklendi: $exe" -ForegroundColor Green
    Write-Host ($result | Select-Object -First 1) -ForegroundColor DarkGray
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
