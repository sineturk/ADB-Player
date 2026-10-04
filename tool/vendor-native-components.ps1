param(
    [string]$TelegramDirectory = "",
    [string]$TorrentDirectory = "",
    [string]$TdJsonSha256 = "",
    [string]$Aria2Sha256 = ""
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$native = Join-Path $root "native"

function Copy-Component {
    param(
        [string]$Source,
        [string]$Target,
        [string]$MainFile,
        [string]$ExpectedHash
    )
    if ([string]::IsNullOrWhiteSpace($Source)) { return }
    $sourcePath = [IO.Path]::GetFullPath($Source)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) { throw "Native kaynak klasörü bulunamadı: $sourcePath" }
    $main = Join-Path $sourcePath $MainFile
    if (-not (Test-Path -LiteralPath $main)) { throw "Ana native dosya bulunamadı: $main" }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedHash)) {
        $actual = Get-Sha256 -Path $main
        if ($actual -ne ($ExpectedHash -replace '\s','').ToUpperInvariant()) { throw "$MainFile SHA-256 doğrulaması başarısız. Beklenen $ExpectedHash, bulunan $actual" }
    }
    if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Recurse -Force }
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    Copy-Item -Path (Join-Path $sourcePath '*') -Destination $Target -Recurse -Force
    $license = Get-ChildItem -LiteralPath $Target -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)' } | Select-Object -First 1
    if (-not $license) { Write-Host "$MainFile kopyalandı ancak lisans/NOTICE dosyası bulunamadı." -ForegroundColor Yellow }
    Write-Host "Native bileşen eklendi: $(Join-Path $Target $MainFile)" -ForegroundColor Green
}

Copy-Component -Source $TelegramDirectory -Target (Join-Path $native "telegram") -MainFile "tdjson.dll" -ExpectedHash $TdJsonSha256
Copy-Component -Source $TorrentDirectory -Target (Join-Path $native "torrent") -MainFile "aria2c.exe" -ExpectedHash $Aria2Sha256
& "$PSScriptRoot\verify-w5-native.ps1"
