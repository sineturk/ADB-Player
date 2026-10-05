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
        throw "V1.0.1 Settings freeze dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

$view = Read-Text "src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml"
$code = Read-Text "src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml.cs"
$tr = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml"
$en = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml"

Require-Text $view 'Settings.OptionalServices.Title' "Premium servisler tek istege bagli bolumde"
Require-Text $view 'Grid.Row="4"' "Istege bagli servisler ana ayarlardan ayrildi"
Require-Text $view 'IsExpanded="False"' "Gelismis/istege bagli bolumler varsayilan kapali"
Require-Text $view 'x:Name="GofileApiTokenBox"' "Gofile token alani korunuyor"
Require-Text $view 'x:Name="AkiraBoxApiCredentialBox"' "AkiraBox token alani korunuyor"
Require-Text $view 'Settings.Cloud.Title' "Bulut OAuth gelismis bolumu korunuyor"
Require-Text $code 'GofileApiTokenBox_OnPasswordChanged' "Gofile secret binding korunuyor"
Require-Text $code 'AkiraBoxApiCredentialBox_OnPasswordChanged' "AkiraBox secret binding korunuyor"
Require-Text $tr 'Settings.Gofile.Badge' "TR Gofile premium/optional etiketi mevcut"
Require-Text $tr 'Settings.AkiraBox.Badge' "TR AkiraBox premium/optional etiketi mevcut"
Require-Text $en 'Settings.Gofile.Badge' "EN Gofile premium/optional etiketi mevcut"
Require-Text $en 'Settings.AkiraBox.Badge' "EN AkiraBox premium/optional etiketi mevcut"
Require-Text $tr 'ADB Cloud' "TR gizli anahtarlarin buluta gitmedigi acik"
Require-Text $en 'ADB Cloud' "EN gizli anahtarlarin buluta gitmedigi acik"

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
    & dotnet build $solution --configuration Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz oldu: exit $LASTEXITCODE" }
    Write-Host "[OK] Release x64 build" -ForegroundColor Green
}

Write-Host "ADB Player V1.0.1 Settings freeze dogrulamasi tamamlandi." -ForegroundColor Cyan
