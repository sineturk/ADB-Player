param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Source([string]$path) {
    $full=Join-Path $root $path
    if(!(Test-Path -LiteralPath $full)){throw "Missing source: $path"}
    [IO.File]::ReadAllText($full,[Text.Encoding]::UTF8)
}
function Assert([bool]$good,[string]$name) {
    if(!$good){throw "R5/P4.13e FAIL: $name"}
    Write-Host "[PASS] $name" -ForegroundColor Green
}
$td=Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$detector=Source 'src\AltyaziDB.Player.Telegram\TelegramArchiveSetDetector.cs'
$panel=Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
[xml]$view=Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'

Assert ($td.Contains('string?[] filters = forumTopicId is > 0')) 'All forum messages fetched without media filter'
Assert ($td.Contains('? [null]')) 'Unfiltered topic message query active'
Assert ($td.Contains('["filter"] = filter is null ? null')) 'TDLib receives null media filter'
Assert ($td.Contains('["query"] = forumTopicId is > 0 ? string.Empty')) 'All parts fetched before text search'
Assert ($td.Contains('var grouped = TelegramArchiveSetDetector.Group(parsed);')) 'Multipart groups assembled before caption search'
Assert ($td.Contains('item.ArchiveParts?.Any(part =>')) 'Search matching one part retains entire archive group'
Assert ($td.Contains('if (string.IsNullOrWhiteSpace(query)) return grouped;')) 'Unfiltered media uses completed archive groups'

Assert ($detector.Contains('TelegramArchiveFormat.Split7Zip => true')) 'Original 7zip aggregation retained'
Assert ($detector.Contains('TelegramArchiveFormat.SplitZip => parts.Any(item => !item.IsFinal)')) 'Original z01/z02/zip aggregation retained'
Assert ($detector.Contains('TelegramArchiveFormat.SplitZip => contiguous && finalCount == 1')) 'Final ZIP required exactly once'
Assert ($detector.Contains('fileName.Normalize(System.Text.NormalizationForm.FormKC)')) 'Archive part names normalized'
Assert ($detector.Contains('Regex Split7ZipRegex()')) '7zip numeric suffix recognition preserved'
Assert ($detector.Contains('Regex SplitZipPartRegex()')) 'z01 numeric suffix recognition preserved'

Assert ($panel.Contains('if (SelectedMedia.IsArchiveSet)')) 'Selected archive remains first playback path'
Assert ($panel.Contains('await OpenArchiveAsync(SelectedMedia, subtitleOnly)')) 'Archive opens through existing stream engine'
Assert ($panel.Contains('await _service.CreateArchiveStreamRequestAsync(archive, entry)')) 'Archive media uses TDLib Range instead of full download'
Assert ($panel.Contains('public string SelectedMediaDiagnostic')) 'Real TDLib filename displayed for debugging'
Assert (!$panel.Contains('Bu belge türü oynatılamıyor')) 'Regression-prone document hard block reverted'

Assert (($null -ne $view.SelectSingleNode("//*[local-name()='TextBlock' and @Text='{Binding SelectedMediaName}' and @ToolTip='{Binding SelectedMediaDiagnostic}']")) -and
        $panel.Contains('public string SelectedMediaName => SelectedMedia?.FileName')) 'Selected filename and diagnostic tooltip use real TDLib name, never caption'
Write-Host "[PASS] R5/P4.13e rollback of playing archive cards and exact forum query" -ForegroundColor Cyan
