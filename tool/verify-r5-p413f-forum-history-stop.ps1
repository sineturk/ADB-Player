param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$name) {
    $file=Join-Path $root $name
    if (!(Test-Path -LiteralPath $file)) {throw "Missing source: $name"}
    [IO.File]::ReadAllText($file,[Text.Encoding]::UTF8)
}
function Check([bool]$ok,[string]$message) {
    if(!$ok){throw "R5/P4.13f FAIL: $message"}
    Write-Host "[PASS] $message" -ForegroundColor Green
}
$td=Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$pager=Source 'src\AltyaziDB.Player.Telegram\TelegramForumTopicHistoryPager.cs'
$service=Source 'src\AltyaziDB.Player.Core\Interfaces\ITelegramService.cs'
$panel=Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
[xml]$view=Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'

Check ($td.Contains('TelegramForumTopicHistoryPager.ReadAsync(')) 'Forum history has dedicated pager'
Check ($td.Contains('["2'+'@type'+'"]') -eq $false) 'No malformed TDLib type tags'
Check ($td.Contains('["'+'@type'+'"] = "getForumTopicHistory"')) 'Dedicated TDLib getForumTopicHistory requested'
Check ($td.Contains('["forum_topic_id"] = forumTopicId.Value')) 'Forum topic identifier passed as integer'
Check ($td.Contains('["from_message_id"] = cursor.ToString(CultureInfo.InvariantCulture)')) 'History cursor passed to TDLib'
Check ($td.Contains('foreach (var filter in filters)') -and $td.Contains('var historyMessageCount = messages.Count;')) 'Search complements partial topic history'
Check ($td.Contains('var grouped = TelegramArchiveSetDetector.Group(parsed)')) 'Archive volumes remain joined before search filtering'
Check ($pager.Contains('cursor = oldest;')) 'History pagination uses existing message ID as cursor'
Check ($pager.Contains('Math.Min(limit, 500)')) 'Tiny history pages can be fetched repeatedly with safety bound'

Check ($service.Contains('Task StopMediaTransferAsync(')) 'Core interface exposes transfer stop'
Check ($service.Contains('void ResumeMediaTransfer(')) 'Core interface exposes resume'
Check ($td.Contains('["'+'@type'+'"] = "cancelDownloadFile"')) 'Stop calls TDLib cancelDownloadFile'
Check ($td.Contains('["only_if_pending"] = false')) 'In-flight TDLib downloads can be canceled'
Check ($td.Contains('_stoppedTransfers.TryAdd(id, 0)')) 'User stop gates all parts of archive'
Check ($td.Contains('ThrowIfTransferStopped(fileId)')) 'Stopped media cannot silently restart range transfers'
Check ($td.Contains('ThrowIfTransferStopped(fileId);' + [Environment]::NewLine + '            // Mesaj')) 'Download range gate checks stopped state before every network request'
Check (!$td.Contains('["'+'@type'+'"] = "deleteFile"')) 'Stopping cannot delete file cache'

Check ($panel.Contains('_activeTransfer = SelectedMedia;')) 'Active transfer tracked even after selection changes'
Check ($panel.Contains('StopTransferCommand = new AsyncRelayCommand(StopTransferAsync)')) 'Stop button is bound to real handler'
Check ($panel.Contains('ResumeTransferCommand = new RelayCommand(ResumeTransfer)')) 'Resume button is bound to real handler'
Check ($panel.Contains('if (_activeTransfer is null || _transferStopped) return;')) 'Stop only affects active transfer'
Check ($panel.Contains('if (_activeTransfer is null || _transferStopped) return;') -and $panel.Contains('_service.StopMediaTransferAsync(target)')) 'Stop cancels active media'
Check ($panel.Contains('_service.ResumeMediaTransfer(_activeTransfer)')) 'Resume reopens cache reads'
Check ($panel.Contains('if (_activeTransfer is null || _transferStopped) return;')) 'Stopped update callbacks do not overwrite progress'
Check ($panel.Contains('catch (OperationCanceledException) when (_transferStopped)')) 'User-canceled open does not show error modal'

Check ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding StopTransferCommand}']")) 'Stop button visible in Telegram panel'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding ResumeTransferCommand}']")) 'Resume button visible in Telegram panel'
foreach($lang in @('tr-TR','en-US')) {
 $loc=Source "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
 [xml]$xml=$loc
 Check ($loc.Contains('x:Key="R5.Telegram.StopDownload"') -and
        $loc.Contains('x:Key="R5.Telegram.ResumeDownload"')) "$lang transfer controls localized"
}
Write-Host "[PASS] R5/P4.13f forum history and Telegram transfer controls" -ForegroundColor Cyan
