param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
  $path = Join-Path $root $relative
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.7 missing $relative" }
  return [IO.File]::ReadAllText($path)
}
function Expect([string]$body, [string]$needle, [string]$scope) {
  if (!$body.Contains($needle)) { throw "R5/P4.7 $scope missing: $needle" }
  Write-Host "[PASS] $scope : $needle" -ForegroundColor Green
}
$xamlPath = 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$codePath = 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
$x = Source $xamlPath
$c = Source $codePath
foreach ($path in @($xamlPath, 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml',
    'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml')) {
  $xml = New-Object Xml.XmlDocument
  $xml.Load((Join-Path $root $path))
  Write-Host "[PASS] XML parses: $path" -ForegroundColor Green
}
foreach ($token in @(
  'x:Name="DiscoverPageScrollViewer"',
  'x:Name="DiscoverModalOverlay"',
  'x:Name="DiscoverModalCard"',
  'x:Name="DiscoverModalHero"',
  'x:Name="DiscoverModalScroll"',
  'x:Name="DiscoverModalCloseButton"',
  'x:Name="DiscoverModalSynopsisPane"',
  'x:Name="DiscoverModalActionsPane"',
  'KeyboardNavigation.TabNavigation="Cycle"',
  'DiscoverModal_PreviewKeyDown',
  'DiscoverModalOverlay_MouseLeftButtonDown',
  'Source="{Binding Addons.SelectedItem.BackgroundUrl}"',
  'Source="{Binding Addons.SelectedItem.PosterUrl}"',
  'Text="{Binding Addons.SelectedItemOverview}"',
  'Binding Addons.SelectedItem, Mode=TwoWay',
  'Binding Addons.SelectedEpisode, Mode=TwoWay',
  'Binding Addons.SelectedStream, Mode=TwoWay',
  'Binding Addons.PlayStreamCommand',
  'Binding Addons.ToggleFavoriteCommand',
  'Binding Addons.ToggleWatchlistCommand',
  'Binding Addons.AddToTargetCollectionCommand',
  'R5.Discover.ModalSourceHint'
)) { Expect $x $token 'modal UI' }
foreach ($token in @(
  'OpenDetails(FeaturedItem)',
  'OpenDetails(item)',
  'DiscoverModalOverlay.Visibility = Visibility.Visible',
  'DiscoverModalOverlay.Visibility = Visibility.Collapsed',
  'DiscoverPageScrollViewer.IsHitTestVisible = false',
  'DiscoverPageScrollViewer.IsHitTestVisible = true',
  'DiscoverModalScroll.ScrollToTop()',
  'DiscoverModalCard.Width = Math.Min(1040, modalWidth)',
  'Keyboard.FocusedElement',
  'e.Key != Key.Escape',
  'DiscoverModalCard.IsAncestorOf(hit)',
  'Grid.SetRow(DiscoverModalPlayButton',
  'CatalogList.Focus()'
)) { Expect $c $token 'modal behavior' }
if ($x.Contains('x:Name="DetailsPanel"') -or $c.Contains('DetailsPanel.')) {
  throw 'Legacy Discover right-hand details panel must not remain'
}
$catalog = $x.IndexOf('x:Name="DiscoverCatalogLayout"')
$overlay = $x.IndexOf('x:Name="DiscoverModalOverlay"')
$overview = $x.IndexOf('Text="{Binding Addons.SelectedItemOverview}"', $overlay)
$stream = $x.IndexOf('Text="{DynamicResource R23.Discover.AvailableStreams}"', $overlay)
$play = $x.IndexOf('Command="{Binding Addons.PlayStreamCommand}"', $overlay)
$cloud = $x.IndexOf('Text="{DynamicResource Discover.Cloud.SaveTitle}"', $overlay)
if ($catalog -lt 0 -or $overlay -lt $catalog -or $overview -lt $overlay -or
    $stream -lt $overview -or $play -lt $stream -or $cloud -lt $play) {
  throw 'Modal reading order or catalog isolation is wrong'
}
foreach ($language in @('tr-TR','en-US')) {
  Expect (Source "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml") 'x:Key="R5.Discover.ModalSourceHint"' $language
}
Write-Host '[PASS] R5/P4.7 modal isolation, commands and close behavior' -ForegroundColor Cyan
