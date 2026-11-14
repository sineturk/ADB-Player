param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$rel) {
  $p = Join-Path $root $rel
  if (!(Test-Path -LiteralPath $p)) {throw "Missing P4.4 source $rel"}
  return [IO.File]::ReadAllText($p)
}
function Require([string]$body,[string]$token,[string]$what) {
  if (!$body.Contains($token)) {throw "P4.4 $what : missing $token"}
  Write-Host "[PASS] $what : $token" -ForegroundColor Green
}
$dis = Source 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$dcs = Source 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
$lib = Source 'src\AltyaziDB.Player.App\Views\Pages\LibraryView.xaml'
$lcs = Source 'src\AltyaziDB.Player.App\Views\Pages\LibraryView.xaml.cs'
$que = Source 'src\AltyaziDB.Player.App\Views\Pages\PlaylistView.xaml'
$qcs = Source 'src\AltyaziDB.Player.App\Views\Pages\PlaylistView.xaml.cs'
foreach ($file in @('src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml',
  'src\AltyaziDB.Player.App\Views\Pages\LibraryView.xaml',
  'src\AltyaziDB.Player.App\Views\Pages\PlaylistView.xaml',
  'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml',
  'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml')) {
  $xml = New-Object Xml.XmlDocument
  $xml.Load((Join-Path $root $file))
  Write-Host "[PASS] XML: $file"
}
Require $dis 'x:Name="DiscoverSpotlight"' 'Discover'
Require $dis 'Binding FeaturedItem, ElementName=DiscoverRoot' 'Discover'
Require $dis 'Source="{Binding BackgroundUrl}"' 'Discover'
Require $dis 'Source="{Binding PosterUrl}"' 'Discover'
Require $dis 'Text="{Binding Title}"' 'Discover'
if (!$dis.Contains('Text="{Binding Description}"') -and
    !$dis.Contains('Text="{Binding FeaturedOverview, ElementName=DiscoverRoot}"')) {
  throw 'Discover P4.4/P4.5 featured overview binding is missing.'
}
Write-Host '[PASS] Discover content overview uses addon or localized P4.5 binding'
Require $dis 'DiscoverFeaturedDetails_Click' 'Discover'
Require $dcs 'IsPopularCatalogItem' 'Discover'
Require $dcs 'OriginCatalogId' 'Discover'
Require $dcs 'FeaturedItem = null;' 'Discover'
Require $dcs 'OpenDetails(FeaturedItem)' 'Discover'
Require $dcs 'DiscoverModalOverlay.Visibility = Visibility.Visible' 'Discover'
Require $dcs 'candidates.FirstOrDefault' 'Discover'
if ($dis.Contains('DiscoverHeroOrnament') -or $dis.Contains('R23.Discover.Headline')) {
  throw 'Decorative Discover hero must be removed.'
}
Require $lib 'Height="94"' 'Library'
Require $lib 'x:Name="LibraryHeaderActions"' 'Library'
Require $lib 'Binding Library.DisplayItemCount' 'Library'
Require $lib 'Binding Library.Folders.Count' 'Library'
Require $lib 'Binding Library.AddFolderCommand' 'Library'
Require $lib 'Binding Library.ScanAllCommand' 'Library'
Require $lib 'Binding ShowCloudListsCommand' 'Library'
Require $lcs 'LibraryHeaderActions.Visibility' 'Library'
if ($lib.Contains('LibraryHeroOrnament') -or $lib.Contains('Height="178"')) {
  throw 'Oversized Library hero remains.'
}
Require $que 'x:Name="QueueHeaderLayout"' 'Playlist'
Require $que 'x:Name="QueueHeaderRight"' 'Playlist'
Require $que 'x:Name="QueueHeaderSecondRow"' 'Playlist'
Require $que 'Binding AutoPlayNext' 'Playlist'
Require $que 'Binding Playlist.Count' 'Playlist'
Require $que 'Binding LoadCloudPlaylistCommand' 'Playlist'
Require $que 'Binding SyncPlaylistToCloudCommand' 'Playlist'
Require $que 'Binding RemoveSelectedCommand' 'Playlist'
Require $que 'Binding ClearPlaylistCommand' 'Playlist'
Require $qcs 'QueueHeaderSecondRow.Height' 'Playlist'
if ($que.Contains('x:Name="QueueHero"')) { throw 'Playlist decorative hero remains.' }
foreach ($lang in @('tr-TR','en-US')) {
  $tr = Source "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
  Require $tr 'x:Key="R5.Discover.FeaturedLabel"' $lang
  Require $tr 'x:Key="R5.Discover.DetailsAction"' $lang
}
Write-Host '[PASS] P4.4 compact Library/Playlist and real catalog featured item guards' -ForegroundColor Cyan
