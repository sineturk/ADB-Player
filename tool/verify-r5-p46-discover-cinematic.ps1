param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$path) {
  $full = Join-Path $root $path
  if (!(Test-Path -LiteralPath $full)) { throw "P4.6 missing file: $path" }
  [IO.File]::ReadAllText($full)
}
function Require([string]$text, [string]$needle, [string]$label) {
  if (!$text.Contains($needle)) { throw "P4.6 $label missing $needle" }
  Write-Host "[PASS] $label" -ForegroundColor Green
}
$viewPath = 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$codePath = 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
$view = Source $viewPath
$code = Source $codePath
foreach ($p in @($viewPath,'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml','src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml')) {
  $xml = New-Object Xml.XmlDocument
  $xml.Load((Join-Path $root $p))
  Write-Host "[PASS] XML: $p"
}
foreach ($token in @(
    'x:Name="DiscoverSpotlight" Height="228"',
    'x:Name="DiscoverFeaturedDescription"',
    'x:Name="DiscoverReadMoreButton"',
    'Click="DiscoverFeaturedDetails_Click"',
    'x:Name="CatalogSearchBox"',
    'Binding Addons.Items.Count',
    'Binding Addons.MovieCount',
    'Binding Addons.SeriesCount',
    'Binding Addons.AnimeCount',
    'Binding Addons.SelectedItem, Mode=TwoWay',
    'Binding Addons.SelectedStream, Mode=TwoWay',
    'Binding Addons.SelectedEpisode, Mode=TwoWay',
    'Binding Addons.PlayStreamCommand',
    'Binding Addons.ToggleFavoriteCommand',
    'Binding Addons.ToggleWatchlistCommand',
    'Binding Addons.AddToTargetCollectionCommand',
    'Binding Addons.SelectedItemOverview',
    'DiscoverLocalizedGenreConverter',
    'LanguageRevision',
    'R5.Discover.DetailsHeader'
)) { Require $view $token $token }
foreach ($token in @('LanguageRevision++','IMultiValueConverter','TurkishGenres',
    'UpdateFeaturedOverviewPresentation','double.PositiveInfinity','DiscoverModalOverlay.Visibility = Visibility.Visible')) {
  Require $code $token $token
}
if ($view.Contains('x:Name="DiscoverStatGrid"')) { throw 'Oversized statistic cards remain' }
if ($view.IndexOf('Text="{DynamicResource R23.Discover.AvailableStreams}"') -gt
    $view.IndexOf('Command="{Binding Addons.PlayStreamCommand}"')) {
  throw 'Playback stream selection must precede Play'
}
if ($view.IndexOf('Command="{Binding Addons.PlayStreamCommand}"') -gt
    $view.IndexOf('Text="{DynamicResource Discover.Cloud.SaveTitle}"')) {
  throw 'Cloud actions must follow the playback-first section'
}
if ($view.Contains('TextTrimming="WordEllipsis" MaxHeight="95"')) { throw 'Synopsis clipped' }
foreach ($lang in @('tr-TR','en-US')) {
  $resource = Source "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
  Require $resource 'x:Key="R5.Discover.DetailsHeader"' "$lang detail header"
}
Write-Host '[PASS] P4.6 source contracts ready for Windows WPF build and visual checks' -ForegroundColor Cyan
