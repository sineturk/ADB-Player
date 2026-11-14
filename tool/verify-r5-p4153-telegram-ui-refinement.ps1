$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing: $relative" }
    return [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$condition,[string]$label) {
    if (!$condition) { throw "[P4.15.3 FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$hub = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramHubView.xaml'
$telegram = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$model = Source 'src\AltyaziDB.Player.Core\Models\TelegramModels.cs'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
foreach ($inputText in @($hub,$telegram,$tr,$en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($inputText)
}
Check ($hub.Contains('ContentPresenter HorizontalAlignment="Stretch"') -and
       $hub.Contains('<Setter Property="HorizontalAlignment" Value="Stretch"/>') -and
       $hub.Contains('x:Key="TelegramHubRowButton"')) 'Hub uses full-width media row template instead of centered common button'
foreach ($section in @('Continue','Starred','Opened','Seen')) {
    Check ($hub.Contains('ToolTip="{DynamicResource Telegram.Hub.' + $section + 'Help}"') -and
           !$hub.Contains('Text="{DynamicResource Telegram.Hub.' + $section + 'Help}"')) ('Section explanation only on tooltip: ' + $section)
}
$hubCode = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramHubView.xaml.cs'
Check ($hub.Contains('MaxHeight="300"') -and
       $hub.Contains('ItemsSource="{Binding Telegram.RecentMedia}"') -and
       $hub.Contains('UniformGrid Columns="2"') -and
       $hub.Contains('TelegramHubSeenItems_OnLoaded') -and
       $hubCode.Contains('grid.Columns = compact ? 1 : 2') -and
       $hub.Contains('TelegramContinueWatching') -and
       $hub.Contains('Mode=OneWay')) 'Bounded, responsive session cards and fixed resume progress remain intact'
Check ($telegram.Contains('<Path Width="16" Height="16"') -and
       $telegram.Contains('TelegramFavoriteToggle_OnClick') -and
       $telegram.Contains('Telegram.ChatList.FavoriteAdd') -and
       $telegram.Contains('Telegram.ChatList.FavoriteRemove')) 'Chat favorite action visible and independently labeled'
Check ($telegram.Contains('Value="unverified"') -and
       !$telegram.Contains('Telegram.MediaList.Status.unverifiedHelp') -and
       !$telegram.Contains('Telegram.MediaList.Status.unverified}') -and
       $telegram.Contains('Value="missing"') -and
       $telegram.Contains('Value="ambiguous"')) 'Unknown final-piece state remains internal, missing and ambiguous still shown'
Check ($model.Contains('? "unverified" : "ready"') -and
       $model.Contains('!ArchiveSetComplete ? "missing"')) 'Archive readiness safety policy is unchanged'
foreach ($name in @('Telegram.ChatList.FavoriteAdd','Telegram.ChatList.FavoriteRemove','Telegram.MediaList.Status.unverifiedHelp')) {
    Check ($tr.Contains('x:Key="' + $name + '"') -and
           $en.Contains('x:Key="' + $name + '"')) ('TR/EN translation: ' + $name)
}
Check ($telegram.Contains('M1,6 L7,6 9,8 19,8 19,18') -and
       $telegram.Contains('M2,6 L7,6 9,8 18,8 18,17') -and
       $telegram.Contains('M10,1.4 L12.8,6.8') -and
       $telegram.Contains('M5,3 L17,10 5,17') -and
       !$telegram.Contains('Text="{Binding MediaIcon}"') -and
       !$telegram.Contains('Content="{Binding FavoriteGlyph}"')) 'Folder, star and media icons use vector paths, not clipped text glyphs'
# R23Button has 11,6 padding. The favorite button must explicitly override it
# or a 20x20 WPF Path is cropped inside the small button.
$starStart = $telegram.IndexOf('<Button Grid.Column="3" Width="32" Height="32"')
$starEnd = $telegram.IndexOf('</Button>', $starStart)
$starMarkup = if ($starStart -ge 0 -and $starEnd -gt $starStart) {
    $telegram.Substring($starStart, $starEnd + 9 - $starStart)
} else { '' }
Check ($starMarkup.Contains('<Setter Property="Padding" Value="0"/>') -and
       $starMarkup.Contains('<Path Width="16" Height="16"') -and
       $starMarkup.Contains('DataTrigger Binding="{Binding IsFavorite}" Value="True"') -and
       $starMarkup.Contains('TelegramFavoriteToggle_OnClick')) 'Favorite star has safe 16px vector within 32px zero-padding button at any DPI'
$vm = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
Check ($vm.Contains('SelectedMedia.PartStatusCode is "missing" or "ambiguous" or "standalone"') -and
       $model.Contains('PartStatusCode is "missing" or "ambiguous"') -and
       $telegram.Contains('<Setter Property="Visibility" Value="Collapsed"/>')) 'No unknown-final notice on rows, metadata or selection footer; real errors retained'
Write-Host '[PASS] P4.15.3 UI refinement contracts. No live Telegram scan, Range streaming or account state was modified.' -ForegroundColor Cyan
