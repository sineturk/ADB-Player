param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Required([string]$rel) {
  $path = Join-Path $root $rel
  if (-not (Test-Path -LiteralPath $path)) {
    throw "Missing source: $rel"
  }
  return [IO.File]::ReadAllText($path)
}
function Require([string]$body, [string]$needle, [string]$label) {
  if (-not $body.Contains($needle)) {
    throw "R5/P4.5 overview expand $label missing $needle"
  }
  Write-Host "[PASS] $label $needle" -ForegroundColor Green
}

$xaml = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$cs = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
$xml = New-Object Xml.XmlDocument
$xml.Load((Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'))
Write-Host '[PASS] Discover XAML parses successfully'
foreach ($name in @(
  'x:Name="DiscoverReadMoreButton"',
  'Click="DiscoverToggleOverview_Click"',
  'Text="{Binding FeaturedOverview, ElementName=DiscoverRoot}"',
  'Text="{Binding Addons.SelectedItemOverview}"',
  'Source="{Binding BackgroundUrl}"',
  'Source="{Binding PosterUrl}"')) {
  Require $xaml $name 'Discover XAML'
}
foreach ($name in @(
  'DiscoverToggleOverview_Click',
  'UpdateFeaturedOverviewPresentation',
  'DiscoverFeaturedDescription.MaxHeight',
  'DiscoverFeaturedDescription.TextTrimming',
  'double.PositiveInfinity',
  'DiscoverSpotlight.Height = _featuredOverviewExpanded',
  'double.NaN : compactHeight',
  'R5.Discover.ReadMore',
  'R5.Discover.ReadLess')) {
  Require $cs $name 'Discover code-behind'
}
foreach ($language in @('tr-TR','en-US')) {
  $resource = Read-Required "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"
  $localXml = New-Object Xml.XmlDocument
  $localXml.Load((Join-Path $root "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"))
  foreach ($name in @('R5.Discover.ReadMore','R5.Discover.ReadLess')) {
    Require $resource ('x:Key="' + $name + '"') $language
  }
}
if ($xaml.Contains('TextTrimming="WordEllipsis" MaxHeight="95"')) {
  throw 'Selected item overview is still clipped to 95px'
}
if ($xaml.Contains('MaxHeight="36" MaxWidth="610"') -and
    -not $cs.Contains('DiscoverFeaturedDescription.MaxHeight = _featuredOverviewExpanded')) {
  throw 'Collapsed spotlight teaser lacks its full-text expansion path'
}
Write-Host '[PASS] Full overview accessible from spotlight and selected item panel' -ForegroundColor Cyan

# Subsequent Cinema-First layouts keep a bounded poster and a top-cropped backdrop.
if ($xaml -notmatch 'Height="(194|228)" VerticalAlignment="Top"') { throw 'Featured backdrop crop contract missing' }
if ($xaml -notmatch 'Width="(112|132)" Height="(162|190)"') { throw 'Featured portrait poster proportions missing' }
Write-Host '[PASS] Bounded featured backdrop and poster proportions' -ForegroundColor Cyan
