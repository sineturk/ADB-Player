$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$condition, [string]$label) {
    if (!$condition) { throw "[P4.15.2-P4 FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$xaml = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$code = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml.cs'
$vm = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
foreach ($source in @($xaml,$tr,$en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($source)
}
Check ($xaml.Contains('TelegramFlatSurfaceStyle') -and
       $xaml.Contains('TelegramFolderListItemStyle') -and
       $xaml.Contains('TelegramChatListItemStyle') -and
       $xaml.Contains('TelegramMediaListItemStyle')) 'Scoped flat Telegram surfaces and dense list rows'
foreach ($name in @('TelegramWorkspaceGrid','TelegramFolderColumn','TelegramFolderGap',
                   'TelegramChatColumn','TelegramMediaGap','TelegramMediaColumn',
                   'TelegramFolderSurface','TelegramChatSurface','TelegramMediaSurface',
                   'TelegramFolderPicker','TelegramShowMediaButton','TelegramBackToChatsButton',
                   'TelegramCompactMenu')) {
    Check ($xaml.Contains('x:Name="' + $name + '"')) ('Responsive XAML element: ' + $name)
}
Check ($code.Contains('TelegramSidebarBreakpoint = 1060') -and
       $code.Contains('TelegramSinglePaneBreakpoint = 740') -and
       $code.Contains('ApplyTelegramResponsiveLayout()') -and
       $code.Contains('TelegramFolderSurface.Visibility') -and
       $code.Contains('TelegramChatSurface.Visibility') -and
       $code.Contains('TelegramMediaSurface.Visibility')) 'Two breakpoint layout with visible two-panel and single-pane modes'
Check ($xaml.Contains('ItemsSource="{Binding ChatFolders}" SelectedItem="{Binding SelectedFolder, Mode=TwoWay}"') -and
       $xaml.Contains('ItemsSource="{Binding FavoriteChats}"')) 'Sidebar folders remain reachable and favorites remain available'
Check ($xaml.Contains('TelegramBackToChats_OnClick') -and
       $xaml.Contains('TelegramShowMedia_OnClick') -and
       $code.Contains('TelegramChatSearch.SelectAll()')) 'Keyboard and explicit narrow-pane navigation'
Check ($xaml.Contains('SelectedItem="{Binding SelectedChat, Mode=OneWay}"') -and
       $xaml.Contains('SelectedItem="{Binding SelectedMedia, Mode=OneWay}"') -and
       $code.Contains('e.AddedItems.Count == 0')) 'Responsive layout does not deselect filtered chat/media'
Check ($xaml.Contains('ItemsSource="{Binding ForumTopics}"') -and
       $xaml.Contains('ItemsSource="{Binding FilteredMedia}"') -and
       $vm.Contains('RefreshFilteredMedia()')) 'P2/P3 forum, media and filters still use existing paths'
foreach ($key in @('Telegram.Responsive.ShowMedia','Telegram.Responsive.BackToChats',
                   'Telegram.Responsive.PickFolder','Telegram.Responsive.MoreActions')) {
    Check ($xaml.Contains('{DynamicResource ' + $key + '}') -and
           $tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('Translated responsive action: ' + $key)
}
Write-Host '[PASS] P4 UI-only responsive and localized XAML/source contracts. No live Telegram tests.' -ForegroundColor Cyan
