[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)

$requiredFiles = @(
    "AltyaziDB.Player.Windows.sln",
    "src\AltyaziDB.Player.Connections\AltyaziDB.Player.Connections.csproj",
    "src\AltyaziDB.Player.Addons\AltyaziDB.Player.Addons.csproj",
    "src\AltyaziDB.Player.Addons\IAddonCatalogService.cs",
    "src\AltyaziDB.Player.Addons\Models\CatalogModels.cs",
    "src\AltyaziDB.Player.Addons\Services\AddonRegistryStore.cs",
    "src\AltyaziDB.Player.Addons\Services\AddonCatalogService.cs",
    "src\AltyaziDB.Player.Addons\Services\StremioAddonClient.cs",
    "src\AltyaziDB.Player.App\ViewModels\ConnectionsPanelViewModel.cs",
    "src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs",
    "src\AltyaziDB.Player.App\MainWindow.xaml",
    "src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml",
    "src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml"
)
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $relative) -PathType Leaf)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: eksik dosya $relative"
    }
}

$solution = Get-Content -LiteralPath (Join-Path $ProjectRoot "AltyaziDB.Player.Windows.sln") -Raw -Encoding UTF8
foreach ($project in @("AltyaziDB.Player.Connections", "AltyaziDB.Player.Addons")) {
    if ($solution -notmatch [regex]::Escape($project)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: solution içinde eksik proje $project"
    }
}

$appProject = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.App\AltyaziDB.Player.App.csproj") -Raw -Encoding UTF8
foreach ($reference in @("AltyaziDB.Player.Connections.csproj", "AltyaziDB.Player.Addons.csproj")) {
    if ($appProject -notmatch [regex]::Escape($reference)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: App referansı eksik $reference"
    }
}

foreach ($relative in @(
    "src\AltyaziDB.Player.App\MainWindow.xaml",
    "src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml",
    "src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml"
)) {
    [xml](Get-Content -LiteralPath (Join-Path $ProjectRoot $relative) -Raw -Encoding UTF8) | Out-Null
}

$mainWindow = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.App\MainWindow.xaml") -Raw -Encoding UTF8
foreach ($token in @(
    "Tab.Connections", "Tab.Addons", "Addons.Legal.Accept", "Addons.Legal.OwnAddon",
    "AllowLocalHttp", "LegalNoticeAccepted", "UserOwnsAddonConfirmed"
)) {
    if ($mainWindow -notmatch [regex]::Escape($token)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: panel öğesi eksik $token"
    }
}
if ($mainWindow -match "Tab\.Discover|Binding Discover") {
    throw "Kullanıcı servisleri/eklentileri doğrulaması: eski Discover kullanıcı arayüzü kalmış."
}

$registry = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.Addons\Services\AddonRegistryStore.cs") -Raw -Encoding UTF8
if ($registry -match "ManifestUrl") {
    throw "Kullanıcı servisleri/eklentileri doğrulaması: eklenti adresi düz registry modeline yazılıyor."
}

$catalogService = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.Addons\Services\AddonCatalogService.cs") -Raw -Encoding UTF8
foreach ($token in @("ISecretStore", "addon.manifest.", "SecretKey", "AllowLocalHttp")) {
    if ($catalogService -notmatch [regex]::Escape($token)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: şifreli eklenti kaydı kuralı eksik $token"
    }
}

$client = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.Addons\Services\StremioAddonClient.cs") -Raw -Encoding UTF8
foreach ($token in @(
    "AllowAutoRedirect = false", "MaximumJsonBytes", "MaximumTorrentBytes", "ValidateRemoteUri",
    "allowLocalHttp", "MaximumJsonBytes", "UserInfo", "redirect <= 3"
)) {
    if ($client -notmatch [regex]::Escape($token)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: eklenti güvenlik kuralı eksik $token"
    }
}

$addonVm = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs") -Raw -Encoding UTF8
foreach ($token in @("LegalNoticeAccepted", "UserOwnsAddonConfirmed", "Addons.Status.ConfirmationRequired")) {
    if ($addonVm -notmatch [regex]::Escape($token)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: kullanıcı onayı eksik $token"
    }
}

$appPaths = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.Infrastructure\AppPaths.cs") -Raw -Encoding UTF8
foreach ($token in @("ConnectionRegistryFile", "AddonRegistryFile", "AddonCacheDirectory", 'Path.Combine(root, "addons.json")')) {
    if ($appPaths -notmatch [regex]::Escape($token)) {
        throw "Kullanıcı servisleri/eklentileri doğrulaması: AppPaths kaydı eksik $token"
    }
}

$connectionService = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\AltyaziDB.Player.Connections\Services\ExternalConnectionService.cs") -Raw -Encoding UTF8
if ($connectionService -notmatch [regex]::Escape("int? leechers = peers is not null")) {
    throw "Kullanıcı servisleri/eklentileri doğrulaması: Rev9.3.2 nullable leechers düzeltmesi eksik."
}

$sourceRoots = @(
    (Join-Path $ProjectRoot "src\AltyaziDB.Player.Addons"),
    (Join-Path $ProjectRoot "src\AltyaziDB.Player.App")
)
$forbiddenDefaults = "v3-cinemeta|torrentio\.strem|strem\.fun|providers=yts|thepiratebay|nyaa\.si"
foreach ($root in $sourceRoots) {
    $files = Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction Stop |
        Where-Object { $_.Extension -in @(".cs", ".xaml", ".csproj") -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        if ($text -match $forbiddenDefaults) {
            throw "Kullanıcı servisleri/eklentileri doğrulaması: hazır eklenti/sağlayıcı adresi gömülmüş $($file.FullName)"
        }
    }
}

Write-Host "Kullanıcı yapılandırmalı bağlantı ve Eklentiler mimarisi statik doğrulamayı geçti." -ForegroundColor Green
