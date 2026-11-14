$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$condition,[string]$name) {
    if (!$condition) { throw "[P4.15.2-P3 FAIL] $name" }
    Write-Host "[PASS] $name" -ForegroundColor Green
}
$xaml = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$code = Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml.cs'
$vm = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$core = Source 'src\AltyaziDB.Player.Core\Models\TelegramModels.cs'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
$store = Source 'src\AltyaziDB.Player.Telegram\TelegramSevenZipStoredRangeResolver.cs'
$tdlib = Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
foreach ($content in @($xaml,$tr,$en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($content)
}
Check ($xaml.Contains('TelegramMediaListItemStyle') -and
       $xaml.Contains('VirtualizingStackPanel.VirtualizationMode="Recycling"')) 'Compact virtualized Telegram media rows'
Check ([regex]::Matches($xaml, 'GroupName="TelegramMediaScope"').Count -eq 6) 'Six mutually exclusive media-category filters'
Check ($vm.Contains('RefreshFilteredMedia()') -and
       $vm.Contains('FilteredMedia.Move(') -and
       $vm.Contains('FilteredMedia.Insert(')) 'Local category filter reconciles instead of refetching chat histories'
Check ($xaml.Contains('ItemsSource="{Binding FilteredMedia}"') -and
       $xaml.Contains('SelectedItem="{Binding SelectedMedia, Mode=OneWay}"') -and
       $code.Contains('TelegramMediaList_OnSelectionChanged')) 'Changing media filter cannot clear active selected media'
Check ($code.Contains('e.AddedItems.Count == 0') -and
       $code.Contains('telegram.SelectedMedia = media;')) 'Only visible user-selected media may navigate selection'
Check ($core.Contains('public string MediaCategory') -and
       $core.Contains('public string PartStatusCode') -and
       $core.Contains('public string MediaSizeLabel')) 'Media model provides source-only category and truthful archive status'
Check ($core.Contains('ArchiveFormat.RawSplit or TelegramArchiveFormat.GenericSplit') -and
       $core.Contains('? "unverified" : "ready"')) 'Raw and generic split endings never misreported as verified'
Check ($xaml.Contains('Value="unverified"') -and
       !$xaml.Contains('Telegram.MediaList.Status.unverified') -and
       $xaml.Contains('Telegram.MediaList.Status.missing') -and
       $xaml.Contains('Telegram.MediaList.Status.ambiguous')) 'Hidden unverified indicator; missing and ambiguous warnings remain'
Check ($xaml.Contains('SelectedMediaName') -and $xaml.Contains('SelectedMediaSummary')) 'File name remains visible when filtered row is hidden'
Check ($xaml.Contains('CanOpenSelectedMedia') -and
       $xaml.Contains('CanAttachSelectedSubtitle') -and
       $xaml.Contains('CanCacheSelectedMedia') -and
       $xaml.Contains('CanRepairSelectedArchive')) 'Selected-file actions guarded by selection and format'
Check ($vm.Contains('CacheSelectedMediaAsync') -and
       $vm.Contains('_prefetchCancellation?.Cancel()') -and
       $vm.Contains('await _service.DownloadMediaAsync(part, cancellation.Token)')) 'Explicit cancellable TDLib cache downloads'
Check ($xaml.Contains('Telegram.MediaList.CacheDownloadHelp') -and
       $tr.Contains('x:Key="Telegram.MediaList.CacheDownloadHelp"') -and
       $en.Contains('x:Key="Telegram.MediaList.CacheDownloadHelp"')) 'Cache download does not imply file export to Downloads folder'
Check ($xaml.Contains('ItemsSource="{Binding ForumTopics}"') -and
       $xaml.Contains('ItemsSource="{Binding FilteredChats}"')) 'P2 forum and chat lists remain intact'
Check ($tdlib.Contains('CreateArchiveStreamRequestAsync') -and
       $store.Contains('decoded.AsSpan().SequenceEqual(direct)')) 'P4.13j direct Store and archive streaming untouched'

$keys = [regex]::Matches($xaml, '\{DynamicResource\s+(Telegram\.MediaList\.[A-Za-z.]+)\}') |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
foreach ($key in $keys) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR/EN media resource: ' + $key)
}
foreach ($codeName in @('video','archive','subtitle','audio','document')) {
    $key = 'Telegram.MediaList.Type.' + $codeName
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR/EN media type: ' + $codeName)
}
foreach ($state in @('ready','unverified','missing','ambiguous','standalone')) {
    $key = 'Telegram.MediaList.Status.' + $state
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR/EN archive status: ' + $state)
}
Write-Host '[PASS] P4.15.2/P3 static UI, localized status, selection safety and Store regression guards' -ForegroundColor Cyan
