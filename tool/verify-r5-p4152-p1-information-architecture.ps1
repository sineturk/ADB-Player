$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (!(Test-Path -LiteralPath $path)) { throw "Missing: $relativePath" }
    [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}
function Check([bool]$condition, [string]$message) {
    if (!$condition) { throw "[P4.15.2-P1 FAIL] $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

$telegram = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$homeXaml = Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml'
$homeCode = Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml.cs'
$telegramCode = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
$store = Source 'src\AltyaziDB.Player.Telegram\TelegramSevenZipStoredRangeResolver.cs'

foreach ($xmlText in @($telegram, $homeXaml, $tr, $en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($xmlText)
}
Check (-not $telegram.Contains('Telegram.Hub.RecentTitle')) 'Recent files are not embedded in Telegram media pane'
Check ($telegram.Contains('ItemsSource="{Binding FilteredMedia}"')) 'Telegram chat-specific media list remains available through P3 local filters'
Check ($telegram.Contains('ItemsSource="{Binding FilteredChats}"')) 'Telegram chat filter remains available'
Check ($telegram.Contains('ItemsSource="{Binding FavoriteChats}"')) 'Telegram chat favorites remain available'
Check ($homeXaml.Contains('R5.TelegramHub.HomeTitle') -and
       $homeXaml.Contains('R5.TelegramHub.RecentTitle') -and
       $homeXaml.Contains('R5.TelegramHub.FavoritesTitle')) 'Telegram Quick Access lives on Home'
Check ($homeXaml.Contains('TelegramRecentPreview, ElementName=HomeRoot') -and
       $homeXaml.Contains('TelegramFavoritePreview, ElementName=HomeRoot')) 'Home binds bounded preview collections'
Check ($homeCode.Contains('model.Telegram.RecentMedia.Take(4)') -and
       $homeCode.Contains('model.Telegram.FavoriteChats.Take(5)')) 'Home previews are bounded; not a duplicate global index'
Check ($homeCode.Contains('old.Telegram.RecentMedia.CollectionChanged -=') -and
       $homeCode.Contains('next.Telegram.RecentMedia.CollectionChanged +=')) 'Home unsubscribes and subscribes to recent updates'
Check ($homeCode.Contains('old.Telegram.FavoriteChats.CollectionChanged -=') -and
       $homeCode.Contains('next.Telegram.FavoriteChats.CollectionChanged +=')) 'Home favorites subscription has no view lifetime leak'
Check ($homeCode.Contains('OpenTelegramRecent_Click') -and
       $homeCode.Contains('model.Telegram.OpenRecentMediaAsync(media)')) 'Home media reuses Telegram playback path'
Check ($homeCode.Contains('OpenTelegramFavorite_Click') -and
       $homeCode.Contains('model.Telegram.OpenFavoriteChatAsync(chat)')) 'Home favorite opens normal Telegram chat'
Check ($homeCode.Contains('HomeTelegramFavoritesPanel') -and
       $homeCode.Contains('Grid.SetRow(HomeTelegramFavoritesPanel, stacked ? 1 : 0)')) 'Home Telegram cards stack responsively'
Check ($telegramCode.Contains('RememberRecentMedia(media)') -and
       $telegramCode.Contains('RefreshChatLists()')) 'Telegram state stays authoritative'
Check ($store.Contains('decoded.AsSpan().SequenceEqual(direct)')) 'P4.13j 7z Store Range validator preserved'

$keys = [regex]::Matches($homeXaml, '\{DynamicResource\s+(R5\.TelegramHub\.[A-Za-z]+)\}') |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
foreach ($key in $keys) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and $en.Contains('x:Key="' + $key + '"')) ('TR/EN resource: ' + $key)
}
Write-Host '[PASS] R5/P4.15.2 P1: Home/Telegram information architecture, localization, Store guard' -ForegroundColor Cyan
