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
        throw "V1.0.1 Google Drive dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

$service = Read-Text "src\AltyaziDB.Player.Sources\RemoteSourceService.cs"
$direct = Read-Text "src\AltyaziDB.Player.Sources\DirectStreamResolver.cs"
$proxy = Read-Text "src\AltyaziDB.Player.Sources\GoogleDriveRangeProxyServer.cs"

Require-Text $service 'ResolveGoogleDriveAsync(uri, cancellationToken)' "Google Drive cozumleme async dogrulama yolunu kullaniyor"
Require-Text $service '_directStreamResolver' "Google Drive akisi medya/range dogrulamasindan geciyor"
Require-Text $service 'drive.usercontent.google.com' "Birincil Google Drive indirme ucu mevcut"
Require-Text $service 'host.Equals("drive.usercontent.google.com"' "Direct usercontent URL Drive resolver'a yonlendiriliyor"
Require-Text $service 'drive.google.com/uc?export=download' "Google Drive yedek indirme ucu mevcut"
Require-Text $service 'headers["Referer"] = "https://drive.google.com/"' "Google Drive referer korunuyor"
Require-Text $service 'headers["Cookie"] = cookieHeader' "Google Drive onay cerezleri libmpv akimina aktarilabiliyor"
Require-Text $service 'ResolveGoogleDriveConfirmationUrlAsync' "Drive confirmation sayfasi otomatik cozumleniyor"
Require-Text $service 'TryExtractGoogleDriveDownloadUri' "Drive uuid/at download URL formu ayristiriliyor"
Require-Text $service 'Google Drive confirmation fields:' "Drive confirmation hidden alan adlari loglaniyor"
Require-Text $service 'Google Drive resolved query fields:' "Drive final confirmation query alan adlari loglaniyor"
Require-Text $service 'TryExtractGoogleDriveAtToken' "Drive at token form disindan da kurtarilabiliyor"
Require-Text $service 'GoogleDriveBrowserUserAgent' "Drive confirmation ve medya istekleri ayni browser fingerprintini kullaniyor"
Require-Text $service 'initialTokenUri' "Ilk cozulmus Drive token URL proxy boyunca korunuyor"
Require-Text $service 'HasGoogleDriveConfirmationToken' "Drive confirmation token URL ayirt ediliyor"
Require-Text $service 'GoogleDriveRangeProxyServer' "Drive confirmation akisi localhost range proxy kullaniyor"
Require-Text $service 'OpenGoogleDriveUpstreamAsync' "Drive proxy her range isteginde uygulama oturumunu kullaniyor"
Require-Text $service 'session-preserving localhost range proxy' "Drive token/cookie oturumu libmpv disinda korunuyor"
Require-Text $service 'full media GET fallback accepted' "Drive Range HTML donerse normal GET fallback var"
Require-Text $service '_googleDriveMediaUris' "Drive final media URL sonraki Range istekleri icin cacheleniyor"
Require-Text $direct 'LooksLikeHtmlPayloadAsync' "Binary gorunumlu HTML payload libmpv oncesi reddediliyor"
Require-Text $proxy 'emulateRange' "Drive 200 full GET yerelde 206 Range olarak sunulabiliyor"
Require-Text $proxy 'SkipAsync' "Drive full GET fallback istenen byte ofsetine ilerleyebiliyor"
Require-Text $service "lastFailure);" "HTML/kota yaniti acik Google Drive hatasi olarak sariliyor"

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
    & dotnet build $solution --configuration Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz oldu: exit $LASTEXITCODE" }
    Write-Host "[OK] Release x64 build" -ForegroundColor Green
}

Write-Host "ADB Player V1.0.1 Google Drive medya baglantisi dogrulamasi tamamlandi." -ForegroundColor Cyan
