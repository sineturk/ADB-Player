param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Required([string]$name) {
  $p = Join-Path $root $name
  if (!(Test-Path -LiteralPath $p)) { throw "R5/P4.7.1 missing: $name" }
  return [IO.File]::ReadAllText($p)
}
function Require([string]$body, [string]$text) {
  if (!$body.Contains($text)) { throw "R5/P4.7.1 missing: $text" }
  Write-Host "[PASS] $text" -ForegroundColor Green
}
$viewPath = 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$x = Read-Required $viewPath
$c = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
$xml = New-Object System.Xml.XmlDocument
$xml.Load((Join-Path $root $viewPath))
foreach ($id in @('DiscoverModalContent','DiscoverModalSynopsisPane',
                  'DiscoverModalActionsPane','DiscoverModalPlaybackLayout',
                  'DiscoverModalPlayGap','DiscoverModalPlayColumn',
                  'DiscoverModalPlaySecondRow','DiscoverModalPlayButton')) {
  $node = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='$id']]")
  if ($null -eq $node) { throw "R5/P4.7.1 missing named UI element $id" }
  Write-Host "[PASS] named UI element $id" -ForegroundColor Green
}
$body = $xml.SelectSingleNode("//*[local-name()='Grid' and @*[local-name()='Name' and .='DiscoverModalContent']]")
$synopsis = $body.SelectSingleNode("./*[local-name()='StackPanel' and @*[local-name()='Name' and .='DiscoverModalSynopsisPane']]")
$actions = $body.SelectSingleNode("./*[local-name()='StackPanel' and @*[local-name()='Name' and .='DiscoverModalActionsPane']]")
if ($null -eq $synopsis -or $null -eq $actions) { throw 'Summary and controls must be direct full-width body rows' }
$playback = $actions.SelectSingleNode("./*[local-name()='Grid' and @*[local-name()='Name' and .='DiscoverModalPlaybackLayout']]")
$cloud = $actions.SelectSingleNode("./*[local-name()='Border' and contains(@Style,'R24InsetPanel')]")
if ($null -eq $playback -or $null -eq $cloud) { throw 'Playback and Cloud must be full-width sibling sections' }
$source = $playback.SelectSingleNode("./*[local-name()='Border' and @*[local-name()='Grid.Row' and .='0']]")
$play = $playback.SelectSingleNode("./*[local-name()='Button' and @*[local-name()='Name' and .='DiscoverModalPlayButton']]")
if ($null -eq $source -or $null -eq $play) { throw 'Wide playback row must contain source selection and Play' }
foreach ($need in @(
  'Binding Addons.SelectedItemOverview',
  'Binding Addons.SelectedEpisode, Mode=TwoWay',
  'Binding Addons.SelectedStream, Mode=TwoWay',
  'Binding Addons.PlayStreamCommand',
  'Binding Addons.ToggleFavoriteCommand',
  'Binding Addons.ToggleWatchlistCommand',
  'Binding Addons.AddToTargetCollectionCommand',
  'Binding Addons.SelectedTargetCollection, Mode=TwoWay')) {
  Require $x $need
}
foreach ($need in @(
 'var stackedPlay = modalWidth < 750;',
 'DiscoverModalPlayColumn.Width = stackedPlay',
 'DiscoverModalPlaySecondRow.Height = stackedPlay',
 'Grid.SetRow(DiscoverModalPlayButton, stackedPlay ? 1 : 0)',
 'Grid.SetColumn(DiscoverModalPlayButton, stackedPlay ? 0 : 2)',
 'DiscoverModalContent.Margin = modalWidth < 570')) {
  Require $c $need
}
if ($x.Contains('x:Name="DiscoverModalSourcesColumn"') -or
    $x.Contains('x:Name="DiscoverModalMiddleColumn"') -or
    $c.Contains('Grid.SetRow(DiscoverModalActionsPane')) {
  throw 'Old fixed-width modal side rail returned'
}
if ($x.Contains('x:Name="DetailsPanel"')) { throw 'Old Discover sidebar returned' }
foreach ($lang in @('tr-TR','en-US')) {
  $resource = Read-Required "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
  Require $resource 'x:Key="R5.Discover.ModalSourceHint"'
}
Write-Host '[PASS] R5/P4.7.1 full-width dialog, responsive Play and Cloud layout' -ForegroundColor Cyan
