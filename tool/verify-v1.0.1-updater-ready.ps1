param(
    [switch]$Build
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot

function Read-Text([string]$Relative) {
    $path = Join-Path $root $Relative
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Dosya bulunamadi: $Relative"
    }

    return [IO.File]::ReadAllText($path)
}

function Require-Text([string]$Text, [string]$Needle, [string]$Label) {
    if (-not $Text.Contains($Needle)) {
        throw "[FAIL] $Label :: $Needle"
    }

    Write-Host "[OK] $Label" -ForegroundColor Green
}

$settings = Read-Text "src\AltyaziDB.Player.Core\Models\PlayerSettings.cs"
$interface = Read-Text "src\AltyaziDB.Player.Core\Interfaces\IUpdateService.cs"
$service = Read-Text "src\AltyaziDB.Player.Infrastructure\HttpUpdateService.cs"
$viewModel = Read-Text "src\AltyaziDB.Player.App\ViewModels\UpdatePanelViewModel.cs"
$view = Read-Text "src\AltyaziDB.Player.App\Views\Pages\UpdatesView.xaml"
$shell = Read-Text "src\AltyaziDB.Player.App\Views\Shell\MediaCenterShell.xaml"
$tr = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml"
$en = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml"

Require-Text $settings 'https://github.com/sineturk/ADB-Player/releases/latest/download/update-manifest.json' "Resmi stable manifest adresi gomulu"
Require-Text $settings 'UpdateManifestUrl { get; set; } = OfficialStableUpdateManifestUrl' "Yeni kurulum stable feed ile basliyor"

Require-Text $viewModel 'Channels = new[] { "stable" };' "V1.0.1 yalniz stable kanalini sunuyor"
Require-Text $viewModel 'CurrentVersionText => FormatVersionLabel' "Tam surum etiketi V1.0.1 olarak gosteriliyor"
Require-Text $viewModel 'PlayerSettings.OfficialStableUpdateManifestUrl' "Eski bos/custom feed stable adrese normalize ediliyor"
Require-Text $viewModel 'HasUpdateBadge => IsUpdateAvailable' "Guncelleme rozeti state'i mevcut"
Require-Text $viewModel 'DownloadPortableAsync' "Portable akis ayri paketi kullaniyor"
Require-Text $viewModel 'Update.Status.PortableReady' "Portable kullaniciya dogru bitis mesaji veriliyor"

Require-Text $interface 'DownloadPortableAsync' "Updater arayuzu portable indirmeyi destekliyor"
Require-Text $service 'manifest.PortableUrl' "Portable URL manifestten geliyor"
Require-Text $service 'manifest.PortableSha256' "Portable SHA-256 manifestten geliyor"
Require-Text $service 'DownloadVerifiedAsync' "Installer ve portable ortak SHA-256 dogrulama hattini kullaniyor"

Require-Text $view 'AvailableVersionText' "Guncelleme sayfasi yeni surumu gosteriyor"
Require-Text $view 'Update.Channel.StableOnlyHelp' "Stable-only aciklamasi gorunur"
Require-Text $view 'UpdateActionText' "Kurulu/portable aksiyon metni ayriliyor"
Require-Text $shell 'Updates.HasUpdateBadge' "Sol menude guncelleme rozeti bagli"

Require-Text $tr 'Action.DownloadPortable' "Turkce portable updater metni mevcut"
Require-Text $tr 'Update.Status.Current' "Turkce guncel durum metni mevcut"
Require-Text $en 'Action.DownloadPortable' "Ingilizce portable updater metni mevcut"
Require-Text $en 'Update.Status.Current' "Ingilizce guncel durum metni mevcut"

if ($viewModel.Contains('Channels = new[] { "stable", "preview" }')) {
    throw "V1.0.1 RC2 preview kanalini son kullaniciya sunmamali."
}
Write-Host "[OK] Preview kanali UI akisindan kaldirilmis" -ForegroundColor Green

[xml]$null = $view
Write-Host "[OK] UpdatesView.xaml XML olarak iyi bicimli" -ForegroundColor Green
[xml]$null = $shell
Write-Host "[OK] MediaCenterShell.xaml XML olarak iyi bicimli" -ForegroundColor Green

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
    & dotnet build $solution --configuration Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build basarisiz oldu: exit $LASTEXITCODE"
    }

    Write-Host "[OK] Release x64 build" -ForegroundColor Green
}

Write-Host "ADB Player V1.0.1 RC2 updater-ready dogrulamasi tamamlandi." -ForegroundColor Green
