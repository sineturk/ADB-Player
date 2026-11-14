param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Source([string]$name) {
  $path = Join-Path $root $name
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.11.2a missing file: $name" }
  [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}

function Require([string]$data, [string]$needle, [string]$label) {
  if (!$data.Contains($needle)) { throw "R5/P4.11.2a $label missing: $needle" }
  Write-Host "[PASS] $label" -ForegroundColor Green
}

$viewPath = 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$xaml = Source $viewPath
$catalog = Source 'src\AltyaziDB.Player.Addons\Models\CatalogModels.cs'
$stremio = Source 'src\AltyaziDB.Player.Addons\Services\StremioAddonClient.cs'
$vm = Source 'src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs'
$external = Source 'src\AltyaziDB.Player.Connections\Models\ConnectionModels.cs'

$xml = New-Object Xml.XmlDocument
$xml.Load((Join-Path $root $viewPath))
$selector = $xml.SelectSingleNode("//*[@*[local-name()='Name' and .='DiscoverModalStreamSelector']]")
if (!$selector) { throw 'Stream dropdown missing' }
if ($selector.GetAttribute('DisplayMemberPath') -eq 'Name') {
  throw 'Stream dropdown still suppresses size metadata'
}
if ($selector.SelectSingleNode("./*[local-name()='ComboBox.ItemTemplate']/*[local-name()='DataTemplate']") -eq $null) {
  throw 'Stream dropdown must use a data template'
}
foreach ($property in @('Name','SizeLabel','SeedersLabel')) {
  if (!$selector.OuterXml.Contains('Binding ' + $property)) {
    throw "Dropdown missing $property"
  }
}
foreach ($token in @(
 'Text="{Binding Addons.SelectedStream.DetailLabel}"',
 'Text="{Binding SizeLabel}"',
 'Text="{DynamicResource R5.Discover.SizeUnknown}"',
 'DataTrigger Binding="{Binding SizeLabel}" Value=""',
 'HorizontalContentAlignment="Stretch"',
 'MaxDropDownHeight="440"',
 'SelectedItem="{Binding Addons.SelectedStream, Mode=TwoWay}"',
 'Command="{Binding Addons.PlayStreamCommand}"'
)) { Require $xaml $token 'discover size/selection contract' }
Require $catalog 'public string SizeLabel => FormatSize(SizeBytes);' 'catalog byte format'
Require $catalog 'public string DetailLabel =>' 'selected source detail'
Require $catalog 'long? SizeBytes,' 'catalog size data'
Require $external 'long? SizeBytes,' 'external release data'
Require $vm 'release.SizeBytes);' 'Prowlarr and Torznab size propagation'
foreach ($token in @(
 'ReadStreamSizeBytes(stream) ?? ParseSize(combined)',
 'hints.TryGetProperty("videoSize", out var videoSize)',
 'stream.TryGetProperty("size", out var topLevelSize)',
 'value.TryGetInt64(out var bytes)',
 'long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes)',
 'return ParseSize(text);'
)) { Require $stremio $token 'structured Stremio size extraction' }

foreach ($lang in @('tr-TR', 'en-US')) {
  $file = "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
  $dict = Source $file
  $doc = New-Object Xml.XmlDocument
  $doc.Load((Join-Path $root $file))
  Require $dict 'x:Key="R5.Discover.SizeUnknown"' "$lang missing-size label"
}
Write-Host '[PASS] R5/P4.11.2a release size visible in Discover choices and selected item' -ForegroundColor Cyan
