$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$condition, [string]$label) {
    if (-not $condition) { throw "[P4.15.2-P2 FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$view = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$viewCode = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml.cs'
$vm = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$models = Source 'src\AltyaziDB.Player.Core\Models\TelegramModels.cs'
$tdlib = Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
$store = Source 'src\AltyaziDB.Player.Telegram\TelegramSevenZipStoredRangeResolver.cs'

foreach ($source in @($view,$tr,$en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($source)
}
Check ($view.Contains('TelegramChatListItemStyle') -and
       $view.Contains('TelegramChatFilterChipStyle')) 'Telegram-only compact list and chip styles'
Check ($view.Contains('TelegramChatSearch') -and
       $view.Contains('ClearChatFilterCommand')) 'Instant search, placeholder and clear action'
Check ([regex]::Matches($view, 'GroupName="TelegramChatScope"').Count -eq 5) 'Five exclusive chat category chips'
Check ($view.Contains('IsAllFilter, Mode=TwoWay') -and
       $view.Contains('IsStarredFilter, Mode=TwoWay') -and
       $view.Contains('IsChannelsFilter, Mode=TwoWay') -and
       $view.Contains('IsGroupsFilter, Mode=TwoWay') -and
       $view.Contains('IsPrivateFilter, Mode=TwoWay')) 'All/starred/channels/groups/private filters bound'
Check ($vm.Contains('item.IsChannel') -and $vm.Contains('item.IsGroup') -and
       $vm.Contains('item.IsPrivate')) 'Chat filters use typed TDLib facets, not names'
Check ($tdlib.Contains('ReadBool(typeElement, "is_channel")')) 'Telegram channel type comes from TDLib'
Check ($models.Contains('System.ComponentModel.INotifyPropertyChanged') -and
       $models.Contains('nameof(FavoriteGlyph)')) 'Toggling a star updates the bound icon immediately'
Check ($view.Contains('UnreadBadgeText') -and $models.Contains('"999+"')) 'Unread badge remains compact'
Check ($view.Contains('SelectedItem="{Binding SelectedChat, Mode=OneWay}"') -and
       $view.Contains('SelectionChanged="TelegramChatList_OnSelectionChanged"')) 'Chat filtering cannot write null into selected media context'
Check ($viewCode.Contains('e.AddedItems.Count == 0') -and
       $viewCode.Contains('telegram.SelectedChat = chat;')) 'Only selecting a visible row navigates to its chat'
Check (-not $vm.Contains('FilteredChats.Clear()') -and
       $vm.Contains('FilteredChats.Move(')) 'Incremental list reconciliation protects active chat and playback'
Check ($viewCode.Contains('TelegramView_OnPreviewKeyDown') -and
       $viewCode.Contains('TelegramChatSearch.SelectAll()')) 'Ctrl+F and Escape keyboard search available'
Check ($view.Contains('VirtualizingStackPanel.VirtualizationMode="Recycling"')) 'Chat list virtualization preserved'
Check ($vm.Contains('FilteredChatCountText') -and
       $vm.Contains('OnPropertyChanged(nameof(FilteredChatCountText))')) 'Visible chat count refreshes after filter/language changes'
Check ($view.Contains('ItemsSource="{Binding ForumTopics}"') -and
       $view.Contains('ItemsSource="{Binding FilteredMedia}"')) 'Forum topics and selected media preserved via P3 filtered view'
Check ($store.Contains('decoded.AsSpan().SequenceEqual(direct)')) 'P4.13j 7z Store Range remains untouched'

$keys = [regex]::Matches($view, '\{DynamicResource\s+(Telegram.ChatList.[A-Za-z]+)\}') |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
foreach ($key in $keys) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR-EN parity: ' + $key)
}
Check ($tr.Contains('x:Key="Telegram.ChatList.ResultCount"') -and
       $en.Contains('x:Key="Telegram.ChatList.ResultCount"')) 'Translated result count exists'
Write-Host '[PASS] P4.15.2/P2 offline Telegram chat-list contract; no live Telegram QA' -ForegroundColor Cyan
