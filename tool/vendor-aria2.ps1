param(
    [string]$Version = "1.37.0",
    [switch]$Force
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$expectedVersion = "1.37.0"
$expectedZipHash = "67D015301EEF0B612191212D564C5BB0A14B5B9C4796B76454276A4D28D9B288"
if ($Version -ne $expectedVersion) {
    throw "Bu paket yalnız doğrulanmış aria2 $expectedVersion sürümünü kabul eder."
}

$target = Join-Path $root "native\torrent"
$exe = Join-Path $target "aria2c.exe"
if ((Test-Path -LiteralPath $exe) -and -not $Force) {
    $versionOutput = & $exe --version 2>$null | Select-Object -First 1
    Write-Host "aria2 zaten mevcut: $versionOutput" -ForegroundColor Green
    exit 0
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("altyazidb-aria2-" + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $temp "aria2.zip"
$extract = Join-Path $temp "extract"
$archiveName = "aria2-$Version-win-64bit-build1.zip"
$url = "https://github.com/aria2/aria2/releases/download/release-$Version/$archiveName"

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Write-Host "aria2 indiriliyor: $url" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedZipHash) {
        throw "aria2 ZIP SHA-256 doğrulaması başarısız. Beklenen: $expectedZipHash, bulunan: $actualHash"
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $sourceExe = Get-ChildItem -LiteralPath $extract -Filter "aria2c.exe" -File -Recurse | Select-Object -First 1
    if (-not $sourceExe) { throw "İndirilen arşivde aria2c.exe bulunamadı." }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $sourceExe.FullName -Destination $exe -Force
    foreach ($pattern in @('COPYING*', 'LICENSE*', 'README*', 'NOTICE*')) {
        Get-ChildItem -LiteralPath $extract -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 4 |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name) -Force }
    }

    $result = & $exe --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "aria2c.exe çalışma doğrulaması başarısız: $result" }
    Write-Host "aria2 doğrulandı ve eklendi: $exe" -ForegroundColor Green
    Write-Host ($result | Select-Object -First 1) -ForegroundColor DarkGray
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
