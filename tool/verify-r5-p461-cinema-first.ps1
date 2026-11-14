param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function File([string]$p) { $f = Join-Path $root $p; if (!(Test-Path $f)) { throw "Missing $p" }; [IO.File]::ReadAllText($f) }
function Require([string]$text,[string]$needle) { if (!$text.Contains($needle)) {throw "Missing P4.6.1 token: $needle"}; Write-Host "[PASS] $needle" }
$x = File 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$code = File 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
foreach ($p in @('src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml',
 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml',
 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml')) {
 $xml = New-Object Xml.XmlDocument
 $xml.Load((Join-Path $root $p))
 Write-Host "[PASS] XAML/XML: $p"
}
foreach ($n in @('x:Name="DiscoverSpotlight" Height="228"',
 'Height="228" VerticalAlignment="Top"',
 'Width="132" Height="190"',
 'x:Name="DiscoverReadMoreButton"',
 'x:Name="DiscoverCatalogLayout" Height="685"',
 'Style="{StaticResource R5DiscoverCatalogPanel}" Background="Transparent"',
 '<Setter Property="Width" Value="166"/>',
 '<Border Width="152" Height="228"',
 '<DropShadowEffect Color="#25B9EB"',
 'R5.Discover.Synopsis',
 'Text="{Binding Addons.SelectedItemOverview}"',
 'Binding Addons.SelectedItem.Title',
 'Binding Path="Addons.SelectedItemMetadata"',
 'Binding Addons.SearchText',
 'Binding Addons.SelectedItem, Mode=TwoWay',
 'Binding Addons.SelectedEpisode, Mode=TwoWay',
 'Binding Addons.SelectedStream, Mode=TwoWay',
 'Binding Addons.PlayStreamCommand',
 'Binding Addons.ToggleFavoriteCommand',
 'Binding Addons.ToggleWatchlistCommand',
 'Binding Addons.AddToTargetCollectionCommand')) { Require $x $n }
foreach ($n in @('double.PositiveInfinity','DiscoverFeaturedDescription.MaxHeight',
 'DiscoverCatalogLayout.Height = 685',
 'DiscoverModalCard.Width = Math.Min(1040, modalWidth)',
 'AnimateCatalogCard(sender, 1.035)')) { Require $code $n }
# P4.6.1 established a complete synopsis before playback and Cloud controls.
# P4.7 moves that section to a modal: support both layouts and first validate
# the anchor, so IndexOf never receives a negative startIndex.
$intro = $x.IndexOf('<!-- P4.6.1: selected overview')
if ($intro -lt 0) {
  $intro = $x.IndexOf('x:Name="DiscoverModalOverlay"')
  if ($intro -lt 0) { throw 'No legacy detail section or P4.7 modal found' }
  Require $x 'x:Name="DiscoverModalSynopsisPane"'
  Require $x 'x:Name="DiscoverModalActionsPane"'
}
$summary = $x.IndexOf('Text="{Binding Addons.SelectedItemOverview}"', $intro)
$stream = $x.IndexOf('Text="{DynamicResource R23.Discover.AvailableStreams}"', $intro)
$play = $x.IndexOf('Command="{Binding Addons.PlayStreamCommand}"', $intro)
$cloud = $x.IndexOf('Text="{DynamicResource Discover.Cloud.SaveTitle}"', $intro)
if ($summary -lt $intro -or $stream -lt $summary -or
    $play -lt $stream -or $cloud -lt $play) {
  throw 'P4.6.1 full synopsis, playback-first and Cloud reading order broken'
}
if ($x.Contains('x:Name="DiscoverStatGrid"')) { throw 'Old dashboard cards returned' }
foreach ($l in @('tr-TR','en-US')) { Require (File "src\AltyaziDB.Player.App\Localization\Strings.$l.xaml") 'x:Key="R5.Discover.Synopsis"' }
Write-Host '[PASS] R5/P4.6.1 Cinema-First source contracts' -ForegroundColor Cyan
