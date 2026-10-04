param(
    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    . "$PSScriptRoot\common.ps1"
    $ProjectRoot = Get-ProjectRoot
}

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$native = Join-Path $root "native\subtitlesync"
$version = "0.5.1"
$url = "https://github.com/smacke/ffsubsync/releases/download/0.5.1/windows-x86_64.zip"
$sha = "FA97D6923BB3444E61FB2D01FF649089F733798E01939BD5FA4C25A409323683"

function Find-FfsubsyncExecutable {
    param([Parameter(Mandatory = $true)][string]$Directory)
    foreach ($name in @("ffsubsync.exe", "ffs.exe", "subsync.exe")) {
        $found = Get-ChildItem -LiteralPath $Directory -Filter $name -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

if (Test-Path -LiteralPath $native -PathType Container) {
    $existing = Find-FfsubsyncExecutable -Directory $native
    if ($existing) {
        Write-Host "ffsubsync $version runtime hazır: $existing" -ForegroundColor Green
        exit 0
    }
}

$tempRoot = Join-Path $env:TEMP ("AltyaziDB-ffsubsync-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $zip = Join-Path $tempRoot "ffsubsync-windows-x86_64.zip"
    Write-Host "ffsubsync $version Windows runtime indiriliyor..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $sha) {
        throw "ffsubsync SHA-256 doğrulaması başarısız.`nBeklenen: $sha`nBulunan: $actual"
    }

    Remove-Item -LiteralPath $native -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $native -Force

    $exe = Find-FfsubsyncExecutable -Directory $native
    if (-not $exe) { throw "ffsubsync Windows executable bulunamadı." }

    @"
ffsubsync $version for ADB Player
Upstream: https://github.com/smacke/ffsubsync
Release asset SHA-256: $sha
License: MIT
"@ | Set-Content -LiteralPath (Join-Path $native "SOURCE.txt") -Encoding UTF8

    Write-Host "ffsubsync $version runtime hazır: $exe" -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
