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
        throw "V1.0.1 Gofile dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

$resolver = Read-Text "src\AltyaziDB.Player.Sources\GofilePublicLinkResolver.cs"
$service = Read-Text "src\AltyaziDB.Player.Sources\RemoteSourceService.cs"
$panel = Read-Text "src\AltyaziDB.Player.App\ViewModels\SourcePanelViewModel.cs"
$models = Read-Text "src\AltyaziDB.Player.Core\Models\RemoteSourceModels.cs"
$app = Read-Text "src\AltyaziDB.Player.App\App.xaml.cs"
$apiSettings = Read-Text "src\AltyaziDB.Player.App\ViewModels\ApiSettingsPanelViewModel.cs"
$settingsView = Read-Text "src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml"
$settingsCode = Read-Text "src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml.cs"
$stringsTr = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml"
$stringsEn = Read-Text "src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml"

Require-Text $models 'Gofile,' "RemoteSourceProvider Gofile mevcut"
Require-Text $service 'GofilePublicLinkResolver' "Public-link zinciri Gofile resolver kullaniyor"
Require-Text $service 'IsDirectDownloadSupported' "Gofile direct-store URL resolver'a yonlendiriliyor"
Require-Text $service 'trimmed.StartsWith("gofile://"' "Gofile alt klasor URI yolu destekleniyor"
Require-Text $service 'RemoteSourceProvider.Gofile => "Gofile"' "Gofile provider etiketi mevcut"
Require-Text $panel 'path.StartsWith("gofile://"' "Kaynak tarayici Gofile alt klasorlerinde gezinebiliyor"
Require-Text $app 'new RemoteSourceService(logger, secrets)' "Gofile resolver DPAPI secret store ile baslatiliyor"

Require-Text $resolver 'https://api.gofile.io' "Gofile resmi API adresi kullaniliyor"
Require-Text $resolver 'gofile:api-token' "Gofile kullanici account token anahtari tanimli"
Require-Text $resolver 'GetConfiguredAccountTokenAsync' "Gofile public klasorleri kullanici tokenini zorunlu kullaniyor"
Require-Text $resolver 'Authorization = new AuthenticationHeaderValue("Bearer", token)' "Gofile API Bearer token ile dogrulaniyor"
Require-Text $resolver 'error-notPremium' "Gofile Premium gereksinimi acik ele aliniyor"
Require-Text $resolver 'required: true' "Gofile public klasor akisi kullanici tokenini zorunlu tutuyor"
Require-Text $resolver 'pageSize=' "Gofile klasor sayfalama destegi mevcut"
Require-Text $resolver 'children' "Gofile klasor cocuklari okunuyor"
Require-Text $resolver 'RemoteSourceItemKind.Audio' "Gofile harici ses dosyalari siniflandiriliyor"
Require-Text $resolver 'RemoteSourceItemKind.Subtitle' "Gofile altyazi dosyalari siniflandiriliyor"
Require-Text $resolver 'RemoteSourceItemKind.Archive' "Gofile arsiv dosyalari siniflandiriliyor"
Require-Text $resolver 'gofile://' "Gofile klasor navigasyon URI'si mevcut"
Require-Text $resolver 'passwordStatus' "Parolali paylasim durumu kontrol ediliyor"
Require-Text $resolver 'error-passwordRequired' "Parola isteyen Gofile paylasimi acik hata veriyor"
Require-Text $resolver 'CancelAfter(TimeSpan.FromSeconds(5))' "Gofile direct URL on dogrulamasi 5 saniyede serbest birakiliyor"
Require-Text $resolver 'BuildDirectResult' "Gofile direct URL probe basarisiz olsa da libmpv handoff yapabiliyor"
Require-Text $resolver 'accountToken=' "Gofile store indirmesine kullanici token cookie aktariliyor"

Require-Text $apiSettings 'GofileApiTokenSecretKey = "gofile:api-token"' "Ayarlar Gofile API tokenini DPAPI anahtarina bagliyor"
Require-Text $apiSettings 'public string GofileApiToken' "Ayarlar ViewModel Gofile token alanini sunuyor"
Require-Text $apiSettings 'SetAsync(GofileApiTokenSecretKey' "Gofile token sifreli depoya kaydediliyor"
Require-Text $settingsView 'x:Name="GofileApiTokenBox"' "Ayarlar ekraninda Gofile token PasswordBox mevcut"
Require-Text $settingsCode 'GofileApiTokenBox_OnPasswordChanged' "Gofile token PasswordBox ViewModel'e bagli"
Require-Text $stringsTr 'Settings.Gofile.PremiumTitle' "Turkce Gofile Premium aciklamasi mevcut"
Require-Text $stringsEn 'Settings.Gofile.PremiumTitle' "Ingilizce Gofile Premium aciklamasi mevcut"

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
    & dotnet build $solution --configuration Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz oldu: exit $LASTEXITCODE" }
    Write-Host "[OK] Release x64 build" -ForegroundColor Green
}

Write-Host "ADB Player V1.0.1 Gofile Premium API dogrulamasi tamamlandi." -ForegroundColor Cyan
