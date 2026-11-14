$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$condition, [string]$label) {
    if (!$condition) { throw "[P4.15.4 FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$main = Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$telegram = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$service = Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$interface = Source 'src\AltyaziDB.Player.Core\Interfaces\ITelegramService.cs'
$settings = Source 'src\AltyaziDB.Player.Core\Models\PlayerSettings.cs'
$policy = Source 'src\AltyaziDB.Player.Core\Models\UnifiedContinueWatchingPolicy.cs'
$homeXaml = Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml'
$homeCode = Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml.cs'
$item = Source 'src\AltyaziDB.Player.App\ViewModels\ContinueWatchingItem.cs'
$hub = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramHubView.xaml'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
foreach ($xmlContent in @($homeXaml,$hub,$tr,$en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($xmlContent)
}
Check ($policy.Contains('TelegramForAccount(') -and
       $policy.Contains('OtherSources(') -and
       $policy.Contains('!uri.IsLoopback')) 'Unified policy allows only persistent, reopenable sources'
Check ($policy.Contains('sourceOwners.TryGetValue(x.Source') -and
       $policy.Contains('owner == verifiedAccountId.Value') -and
       $policy.Contains('!telegramReady')) 'Telegram resumes require a verified matching TDLib account'
Check ($service.Contains('["@type"] = "getMe"') -and
       $interface.Contains('GetCurrentUserIdAsync(') -and
       $telegram.Contains('ResolveCurrentAccountIdAsync') -and
       $telegram.Contains('CurrentAccountId = null')) 'Identity comes from TDLib and disappears at sign-out'
Check ($settings.Contains('TelegramResumeSourceOwners') -and
       $settings.Contains('TelegramLegacyResumeOwnershipMigrated') -and
       $main.Contains('BindLegacyTelegramResumeOwnerAsync')) 'Device-local owner map and one-time legacy migration'
Check ($main.Contains('UnifiedContinueWatchingPolicy.TelegramForAccount(') -and
       $main.Contains('UnifiedContinueWatchingPolicy.OtherSources(') -and
       $main.Contains('items.Concat(visibleTelegram)') -and
       $main.Contains('ContinueWatching.Add(item)')) 'Home rail merges local HTTP and verified Telegram resumes'
Check ($main.Contains('Telegram.CurrentAccountId == verifiedTelegramAccount') -and
       $main.Contains('Telegram.IsReady') -and
       $main.Contains('TelegramContinueWatching.Add(item)')) 'Home and Hub both re-check account identity after async reads'
Check ($main.Contains('if (!IsCurrentTelegramOwner(source))') -and
       $main.Contains('resume = null;') -and
       $main.Contains('TelegramResumeSourceOwners[source] = ownerId')) 'Reopen and saved playback cannot reuse a foreign Telegram seek position'
Check ($telegram.Contains('RecentMedia.Clear();') -and
       $telegram.Contains('FavoriteChats.Clear();') -and
       $homeCode.Contains('TelegramAccount_PropertyChanged') -and
       $homeCode.Contains('model.Telegram.CurrentAccountId is not > 0')) 'Session metadata is cleared on logout and hidden on Home'
Check ($homeXaml.Contains('ItemsSource="{Binding ResumeGroups, ElementName=HomeRoot}"') -and
       $homeXaml.Contains('Value="{Binding Latest.ProgressPercent, Mode=OneWay}"') -and
       $homeXaml.Contains('SourceKindKey, Converter={StaticResource LocalizedTextConverter}') -and
       $item.Contains('Home.ContinueWatching.Telegram')) 'Existing Home carousel, resume actions and Telegram source badge remain intact'
Check ($hub.Contains('Value="{Binding ProgressPercent, Mode=OneWay}"') -and
       $hub.Contains('ItemsSource="{Binding TelegramContinueWatching}"')) 'P5 Hub survives the unification without read-only binding crashes'
foreach ($name in @('Home.ContinueWatching.Telegram','Home.ContinueWatching.Local','Home.ContinueWatching.Remote')) {
    Check ($tr.Contains('x:Key="' + $name + '"') -and
           $en.Contains('x:Key="' + $name + '"')) ('TR/EN source label: ' + $name)
}
Write-Host '[PASS] P4.15.4 verified-account Unified Continue Watching source and UI contracts' -ForegroundColor Cyan
