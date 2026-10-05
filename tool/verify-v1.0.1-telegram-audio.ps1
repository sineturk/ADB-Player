param(
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Text([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Dosya bulunamadi: $relativePath"
    }
    return [IO.File]::ReadAllText($path)
}

function Require-Text([string]$content, [string]$needle, [string]$label) {
    if (-not $content.Contains($needle)) {
        throw "V1.0.1 Telegram ses dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

$service = Read-Text "src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs"
$panel = Read-Text "src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs"

Require-Text $service 'else if (IsAudioDocument(name, mime))' "Telegram document ses dosyalari Audio olarak siniflandiriliyor"
Require-Text $service '".eac3"' "E-AC-3 Telegram document olarak destekleniyor"
Require-Text $service '".dts"' "DTS Telegram document olarak destekleniyor"
Require-Text $service '".truehd"' "TrueHD Telegram document olarak destekleniyor"
Require-Text $service '".m4a"' "M4A Telegram document olarak destekleniyor"
Require-Text $service '".wav"' "WAV Telegram document olarak destekleniyor"
Require-Text $panel '".eac3"' "Telegram paneli E-AC-3 harici sesi kabul ediyor"
Require-Text $panel '".dtshd"' "Telegram paneli DTS-HD harici sesi kabul ediyor"
Require-Text $panel '".truehd"' "Telegram paneli TrueHD harici sesi kabul ediyor"

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
    & dotnet build $solution --configuration Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build basarisiz oldu: exit $LASTEXITCODE"
    }
    Write-Host "[OK] Release x64 build" -ForegroundColor Green
}

Write-Host "ADB Player V1.0.1 Telegram harici ses dogrulamasi tamamlandi." -ForegroundColor Cyan
