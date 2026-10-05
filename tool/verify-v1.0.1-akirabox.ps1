param(
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Text([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) { throw "Dosya bulunamadi: $relativePath" }
    return [IO.File]::ReadAllText($path)
}

function Require-Text([string]$content, [string]$needle, [string]$label) {
    if (-not $content.Contains($needle)) {
        throw "V1.0.1 AkiraBox dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

$resolver = Read-Text "src\AltyaziDB.Player.Sources\AkiraBoxPublicLinkResolver.cs"
$service = Read-Text "src\AltyaziDB.Player.Sources\RemoteSourceService.cs"
$models = Read-Text "src\AltyaziDB.Player.Core\Models\RemoteSourceModels.cs"
$apiSettings = Read-Text "src\AltyaziDB.Player.App\ViewModels\ApiSettingsPanelViewModel.cs"
$settingsView = Read-Text "src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml"
$settingsCode = Read-Text "src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml.cs"
$stringsTr = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml"
$stringsEn = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml"

Require-Text $models 'AkiraBox,' "RemoteSourceProvider AkiraBox mevcut"
Require-Text $service 'AkiraBoxPublicLinkResolver' "Public-link zinciri AkiraBox resolver kullaniyor"
Require-Text $service 'RemoteSourceProvider.AkiraBox => "AkiraBox"' "AkiraBox provider etiketi mevcut"
Require-Text $resolver 'https://akirabox.com/api' "AkiraBox varsayilan API adresi mevcut"
Require-Text $resolver 'GET /api/files?url=' "AkiraBox public status API akisi belgelenmis"
Require-Text $resolver 'GetPublicStatusAsync' "AkiraBox public status API kullaniliyor"
Require-Text $resolver 'RangeHeaderValue(0, 1)' "AkiraBox public link byte-range ile dogrulaniyor"
Require-Text $resolver 'CancelAfter(TimeSpan.FromSeconds(7))' "AkiraBox public probe bloklamiyor"
Require-Text $resolver 'files/list' "AkiraBox authenticated dosya listesi fallback mevcut"
Require-Text $resolver 'api_key' "AkiraBox api_key fallback mevcut"
Require-Text $resolver 'api_token' "AkiraBox api_token fallback mevcut"
Require-Text $resolver 'AuthenticationHeaderValue("Bearer", credential)' "AkiraBox Bearer token fallback mevcut"
Require-Text $resolver 'akirabox:api-base-url' "AkiraBox API endpoint ayari tanimli"
Require-Text $resolver 'akirabox:api-key' "AkiraBox API credential ayari tanimli"
Require-Text $resolver 'RemoteSourceProvider.AkiraBox' "AkiraBox direct-open sonucu dogru provider ile donuyor"

Require-Text $apiSettings 'AkiraBoxApiBaseUrlSecretKey' "Ayarlar AkiraBox API endpoint alanini sakliyor"
Require-Text $apiSettings 'AkiraBoxApiCredentialSecretKey' "Ayarlar AkiraBox API credential alanini DPAPI ile sakliyor"
Require-Text $apiSettings 'public string AkiraBoxApiBaseUrl' "Ayarlar ViewModel AkiraBox API endpoint sunuyor"
Require-Text $apiSettings 'public string AkiraBoxApiCredential' "Ayarlar ViewModel AkiraBox credential sunuyor"
Require-Text $settingsView 'x:Name="AkiraBoxApiCredentialBox"' "Ayarlar ekraninda AkiraBox credential PasswordBox mevcut"
Require-Text $settingsView 'ApiSettings.AkiraBoxApiBaseUrl' "Ayarlar ekraninda AkiraBox API endpoint TextBox mevcut"
Require-Text $settingsCode 'AkiraBoxApiCredentialBox_OnPasswordChanged' "AkiraBox credential PasswordBox ViewModel'e bagli"
Require-Text $stringsTr 'Settings.AkiraBox.PublicTitle' "Turkce AkiraBox public fallback aciklamasi mevcut"
Require-Text $stringsEn 'Settings.AkiraBox.PublicTitle' "Ingilizce AkiraBox public fallback aciklamasi mevcut"

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
    & dotnet build $solution --configuration Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz oldu: exit $LASTEXITCODE" }
    Write-Host "[OK] Release x64 build" -ForegroundColor Green
}

Write-Host "ADB Player V1.0.1 AkiraBox public/API fallback dogrulamasi tamamlandi." -ForegroundColor Cyan
