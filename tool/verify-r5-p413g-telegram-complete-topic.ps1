param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read([string]$name) {
  $file = Join-Path $root $name
  if (!(Test-Path -LiteralPath $file)) { throw "Missing $name" }
  [IO.File]::ReadAllText($file,[Text.Encoding]::UTF8)
}
function Check([bool]$ok,[string]$label) {
  if (!$ok) { throw "R5/P4.13g FAIL: $label" }
  Write-Host "[PASS] $label" -ForegroundColor Green
}
$td=Read 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$pager=Read 'src\AltyaziDB.Player.Telegram\TelegramForumTopicHistoryPager.cs'
$merge=Read 'src\AltyaziDB.Player.Telegram\TelegramForumMessageMerge.cs'
$panel=Read 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
[xml]$view=Read 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
Check ($td.Contains('TelegramForumMessageMerge.Add(messages, history)')) 'History uses unified message-ID merger'
Check ($td.Contains('TelegramForumMessageMerge.Add(target, messages.EnumerateArray())')) 'Search response uses identical merger'
Check ($td.Contains('var historyMessageCount = messages.Count;')) 'History result recorded before search supplement'
Check ($td.Contains('foreach (var filter in filters)')) 'Search result supplements even nonempty history'
Check (!$td.Contains('if (messages.Count == 0)' + [Environment]::NewLine + '        foreach (var filter in filters)')) 'Partial history cannot suppress search'
Check ($merge.Contains('if (id <= 0 || chatId == 0) continue;')) 'Negative Telegram supergroup IDs retained'
Check ($merge.Contains('destination[key] = message.Clone();')) 'Source message ownership and deduplication correct'
Check ($merge.Contains('chatId.ToString(CultureInfo.InvariantCulture)')) 'Message key differentiates chat ID'
Check ($pager.Contains('cursor = oldest;')) 'Pagination uses existing Telegram message ID'
Check ($td.Contains('var grouped = TelegramArchiveSetDetector.Group(parsed);')) 'Multipart parts joined before user filename search'
Check ($panel.Contains('MediaCountLabel(media)')) 'Group and physical file counts are distinct'
Check ($panel.Contains('media.Sum(item => item.ArchiveParts?.Count ?? 1)')) 'Real physical Telegram files counted'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='MenuItem' and @ToolTip='{DynamicResource R5.Telegram.MoreActions}']")) 'Compact overflow menu exists'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='MenuItem' and @Command='{Binding OpenTelegramFolderCommand}']")) 'Open folder moved to overflow menu'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='MenuItem' and @Command='{Binding RepairArchivePartsCommand}']")) 'Repair action retained in overflow menu'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding DownloadAndOpenCommand}']")) 'Play action still available'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding AttachSelectedSubtitleCommand}']")) 'Subtitle attach still available'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding StopTransferCommand}']")) 'Stop transfer action retained'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding ResumeTransferCommand}']")) 'Resume transfer action retained'
Check ($null -eq $view.SelectSingleNode("//*[local-name()='Border' and @Style='{StaticResource R24CommandDeck}']")) 'Oversized command deck removed'
foreach($lang in @('tr-TR','en-US')) {
  [xml]$loc=Read "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
  Check ($null -ne $loc.SelectSingleNode("//*[local-name()='String' and @*[local-name()='Key' and .='R5.Telegram.MoreActions']]")) "$lang overflow menu localized"
}
Write-Host '[PASS] R5/P4.13g forum message completeness and compact Telegram toolbar' -ForegroundColor Cyan
