param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$relativePath) {
  $full = Join-Path $root $relativePath
  if (!(Test-Path -LiteralPath $full)) {throw "Missing: $relativePath"}
  return [IO.File]::ReadAllText($full)
}
function Require([string]$src,[string]$token,[string]$name) {
  if (!$src.Contains($token)) {throw "P4.5 $name contract missing: $token"}
  Write-Host "[PASS] $name - $token" -ForegroundColor Green
}
$svc = Read-Source 'src\AltyaziDB.Player.App\Services\LocalizedCatalogOverviewService.cs'
$vm = Read-Source 'src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs'
$main = Read-Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$view = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$code = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
$api = Read-Source 'src\AltyaziDB.Player.Infrastructure\AltyaziDbPlayerMetadataService.cs'
$local = Read-Source 'src\AltyaziDB.Player.App\Localization\LocalizationManager.cs'
$xml = New-Object Xml.XmlDocument
$xml.Load((Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'))

foreach ($k in @('GetTurkishOverviewAsync','BuildMediaIdentity',
  'SelectVerifiedTurkishOverview','LooksTurkish','ConcurrentDictionary',
  'SemaphoreSlim','RetryAfterLogin','item.Year != media.Year',
  'result.Match?.Confidence','includeDetail: true','TimeSpan.FromHours(12)')) {
  Require $svc $k 'resolver'
}
foreach ($k in @('SelectedItemOverview','RequestSelectedOverview',
  'GetPreferredOverviewAsync','LocalizedMetadataRefreshRequested',
  'RefreshLocalizedMetadataAfterLogin','_selectedOverviewCancellation?.Cancel()',
  'LocalizationManager.IsEnglish')) {
  Require $vm $k 'VM'
}
foreach ($k in @('mediaMetadataService);','Addons.RefreshLocalizedMetadataAfterLogin()')) {
  Require $main $k 'composition/login'
}
foreach ($k in @('FeaturedOverview','RequestFeaturedOverview()',
  'DiscoverLanguageChanged','LocalizedMetadataChanged',
  '_featuredOverviewCancellation?.Cancel()',
  'ReferenceEquals(FeaturedItem, item)')) {
  Require $code $k 'spotlight lifecycle'
}
Require $view 'Text="{Binding FeaturedOverview, ElementName=DiscoverRoot}"' 'spotlight binding'
Require $view 'Text="{Binding Addons.SelectedItemOverview}"' 'details binding'
Require $api 'GetString(mediaElement, "overview")' 'existing API response'
Require $api 'auth.AccessToken' 'authenticated API'
Require $local 'LanguageChanged?.Invoke' 'language switch event'
if ($view.Contains('Text="{Binding Addons.SelectedItem.Description}"') -or
    $view.Contains('Text="{Binding Description}" Foreground="#C8DDED"')) {
  throw 'An unlocalized synopsis binding remains in Discover.'
}
if ($svc.Contains('TranslateText') -or $svc.Contains('apikey') -or
    $svc.Contains('X-API-Key')) {
  throw 'P4.5 should reuse authenticated metadata, not invent translation or credentials.'
}
Write-Host '[PASS] R5/P4.5 localized overview source and lifecycle contracts' -ForegroundColor Cyan
