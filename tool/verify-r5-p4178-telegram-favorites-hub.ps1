$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "Eksik dosya: $relative" }
    return [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$ok, [string]$name) {
    if (-not $ok) { throw "[P4.17.8 FAIL] $name" }
    Write-Host "[PASS] $name" -ForegroundColor Green
}
$chat = Read 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$hub = Read 'src\AltyaziDB.Player.App\Views\Pages\TelegramHubView.xaml'
$code = Read 'src\AltyaziDB.Player.App\Views\Pages\TelegramHubView.xaml.cs'
$vm = Read 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$policy = Read 'src\AltyaziDB.Player.Core\Models\TelegramFavoriteFeedPolicy.cs'
$tr = Read 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Read 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
foreach ($xmlText in @($chat, $hub, $tr, $en)) {
    $doc = New-Object System.Xml.XmlDocument
    $doc.LoadXml($xmlText)
}
Check ($chat.Contains('DataTrigger Binding="{Binding IsFavorite}" Value="True"') -and
       $chat.Contains('Fill" Value="#F6D878"') -and
       $vm.Contains('chat.IsFavorite = favorites.Contains(chat.Id);') -and
       $vm.Contains('foreach (var chat in Chats) chat.IsFavorite = favorites.Contains(chat.Id);') -and
       $vm.Contains('_knownChats.TryAdd(chat.Id, chat)')) 'Starred chat state updates visible folder rows'
Check ($hub.Contains('ItemsSource="{Binding Telegram.FavoriteRecentMedia}"') -and
       $hub.Contains('Click="RefreshFavoriteFeed_Click"') -and
       $hub.Contains('Click="OpenFavoriteMedia_Click"') -and
       $hub.Contains('IsVisibleChanged="OnTelegramHubVisibilityChanged"') -and
       $code.Contains('RefreshFavoriteMediaAsync(force: true)') -and
       $code.Contains('OpenRecentMediaAsync(item)')) 'Hub has on-demand refresh and playback'
Check ($vm.Contains('Take(8)') -and
       $vm.Contains('GetChatMediaAsync(chat.Id, limit: 12)') -and
       $vm.Contains('generation != _accountIdentityGeneration') -and
       $vm.Contains('FavoriteRecentMedia.Clear();') -and
       $policy.Contains('favorites.Contains(item.ChatId)') -and
       $policy.Contains('item.Kind != TelegramMediaKind.ArchivePart')) 'Bounded, identity-scoped favorite feed and no multipart fragments'
foreach ($key in @('FeedTitle','FeedHelp','FeedEmpty','FeedLoading','FeedRefresh')) {
    foreach ($lang in @($tr, $en)) {
        $needle = 'x:Key="Telegram.Hub.' + $key + '"'
        Check ($lang.Split(@($needle), [StringSplitOptions]::None).Length -eq 2) "TR/EN unique localization: $key"
    }
}
Write-Host '[PASS] P4.17.8 Telegram favorites and Hub UX static verification.' -ForegroundColor Cyan
