$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$condition,[string]$name) {
    if (!$condition) { throw "[P4.15.2 P5 FAIL] $name" }
    Write-Host "[PASS] $name" -ForegroundColor Green
}
$hub = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramHubView.xaml'
$code = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramHubView.xaml.cs'
$vm = Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$shell = Source 'src\AltyaziDB.Player.App\Views\Shell\MediaCenterShell.xaml'
$shellCode = Source 'src\AltyaziDB.Player.App\Views\Shell\MediaCenterShell.xaml.cs'
$homeXaml = Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml'
$telegram = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$policy = Source 'src\AltyaziDB.Player.Core\Models\TelegramHubSourcePolicy.cs'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
foreach ($source in @($hub,$shell,$homeXaml,$telegram,$tr,$en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($source)
}
Check ($hub.Contains('ItemsSource="{Binding Telegram.FavoriteChats}"') -and
       $hub.Contains('ItemsSource="{Binding TelegramRecentlyOpened}"') -and
       $hub.Contains('ItemsSource="{Binding TelegramContinueWatching}"') -and
       $hub.Contains('ItemsSource="{Binding Telegram.RecentMedia}"')) 'Four correctly scoped collections'
Check ($shell.Contains('NavTelegramHub') -and
       $shell.Contains('TelegramHubPage') -and
       $shellCode.Contains('index == 13') -and
       $vm.Contains('Math.Clamp(value, 0, 13)')) 'Dedicated Hub navigation route 13'
Check ($homeXaml.Contains('Command="{Binding ShowTelegramHubCommand}"') -and
       $telegram.Contains('Command="{Binding ShowTelegramHubCommand}"')) 'Home and Telegram Hub shortcuts'
Check ($hub.Contains('Telegram.IsReady') -and
       $code.Contains('Model?.Telegram.IsReady == true')) 'Signed-out hub contents and actions gated'
Check ($policy.Contains('telegram-archive:') -and
       $policy.Contains('telegram:') -and
       $vm.Contains('TelegramHubSourcePolicy.RecentlyOpened(RecentSources)') -and
       $vm.Contains('UnifiedContinueWatchingPolicy.TelegramForAccount(')) 'No ephemeral Range URLs or session-discovered entries in persisted history'
Check ($code.Contains('PlayContinueWatchingAsync(item)') -and
       $code.Contains('PlayRecentAsync(item)') -and
       $code.Contains('OpenFavoriteChatAsync(chat)') -and
       $code.Contains('OpenRecentMediaAsync(item)')) 'Existing media and chat actions reused'
# WPF ProgressBar.Value binds TwoWay by default; ContinueWatchingItem.ProgressPercent is read-only.
# The template is instantiated only after a resumable item appears, so compilation alone
# does not detect this runtime XamlParseException.
$continueItem = Source 'src\AltyaziDB.Player.App\ViewModels\ContinueWatchingItem.cs'
Check ($continueItem.Contains('public double ProgressPercent =>') -and
       $hub.Contains('Value="{Binding ProgressPercent, Mode=OneWay}"') -and
       !$hub.Contains('Value="{Binding ProgressPercent}"')) 'Hub resume ProgressBar cannot TwoWay-bind the read-only ProgressPercent'
Check ($hub.Contains('TelegramHubOpenedPanel') -and
       $code.Contains('e.NewSize.Width < 780')) 'Responsive Hub columns'
Check ($telegram.Contains('ItemsSource="{Binding FilteredMedia}"') -and
       $telegram.Contains('ItemsSource="{Binding FilteredChats}"')) 'P2/P3 media and chat lists retained'
foreach ($key in @('Telegram.Hub.PageTitle','Telegram.Hub.ContinueTitle',
                   'Telegram.Hub.OpenedTitle','Telegram.Hub.StarredTitle',
                   'Telegram.Hub.SeenTitle','Telegram.Hub.UnknownMedia',
                   'Telegram.Hub.OpenHub','Telegram.Hub.ConnectHint')) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR/EN resource ' + $key)
}
Write-Host '[PASS] P5 Hub routing, XAML, source policy, language and UI-only contracts' -ForegroundColor Cyan
