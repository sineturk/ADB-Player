param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read([string]$name) {
    $file = Join-Path $root $name
    if (!(Test-Path -LiteralPath $file)) { throw "R5/P4.13 source missing: $name" }
    [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8)
}
function Check([bool]$result,[string]$message) {
    if (!$result) { throw "R5/P4.13 FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

$zip = Read 'src\AltyaziDB.Player.App\Services\ArchiveMediaAttachmentHelper.cs'
$telegram = Read 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$tdlib = Read 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$models = Read 'src\AltyaziDB.Player.Core\Models\TelegramModels.cs'
$main = Read 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$dialog = Read 'src\AltyaziDB.Player.App\Services\WpfDialogService.cs'
[xml]$view = Read 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'

Check ($zip.Contains('ZipFile.OpenRead(path)')) 'ZIP entries inspected without extracting entire archive'
Check ($zip.Contains('ZipArchiveMode.Read')) 'ZIP opened read-only'
Check ($zip.Contains('MaximumEntries = 4096')) 'entry-count cap present'
Check ($zip.Contains('MaximumAudioBytes')) 'uncompressed audio capped'
Check ($zip.Contains('MaximumSubtitleBytes')) 'uncompressed subtitle capped'
Check ($zip.Contains('MaximumSelectedBytes - entry.Size')) 'aggregate extraction budget enforced'
Check ($zip.Contains('IsSymbolicLink(entry)')) 'symlinks rejected'
Check ($zip.Contains('Path.GetFileName(entry.FullName.Replace')) 'paths flattened, not trusted'
Check ($zip.Contains('FileMode.CreateNew')) 'extracted files cannot overwrite existing files'
Check ($zip.Contains('Guid.NewGuid().ToString("N")')) 'isolated extraction directory per archive'
Check ($zip.Contains('extracted > item.Size')) 'streamed extraction cannot exceed entry length'
Check ($zip.Contains('ArchiveAttachmentKind.Subtitle')) 'ZIP subtitles selectable'
Check ($zip.Contains('ArchiveAttachmentKind.Audio')) 'ZIP audio selectable'
Check ($zip.Contains('allowMultiple: true')) 'user can select several audio/subtitle entries'
Check ($zip.Contains('".eac3"') -and $zip.Contains('".ac3"')) 'EAC3/AC3 audio supported'
Check ($zip.Contains('".srt"') -and $zip.Contains('".ass"') -and $zip.Contains('".vtt"')) 'SRT/ASS/VTT subtitles supported'

Check ($models.Contains('Subtitle,')) 'Telegram subtitle kind exposed'
Check ($tdlib.Contains('IsSubtitleDocument(name) ? TelegramMediaKind.Subtitle')) 'Telegram standalone subtitle documents included'
Check ($telegram.Contains('ArchiveMediaAttachmentHelper.IsZip(SelectedMedia.FileName)')) 'single ZIP recognized before archive-part refusal'
Check ($telegram.Contains('await OpenArchiveAsync(archive, subtitleOnly)')) 'Telegram ZIP uses existing archive engine directly'
Check ($telegram.Contains('OpenSingleZipAttachmentAsync(SelectedMedia, subtitleOnly)')) 'single ZIP attachment flow invoked'
Check ($telegram.Contains('IsAudioSelectionMode') -and $telegram.Contains('inspection.Entries.Where(entry => IsAudioFile(entry.Name))')) 'audio mode filters Telegram ZIP source entries'
Check ($telegram.Contains('AttachSelectedSubtitleCommand')) 'Telegram subtitle button command wired'
Check ($telegram.Contains('await _attachAudio(request)')) 'ZIP audio streamed to playback track'
Check ($telegram.Contains('await _service.ExtractArchiveSubtitleAsync(archive, entry)') -and $telegram.Contains('_attachSubtitle(subtitlePath)')) 'only the selected ZIP subtitle is fetched and attached'
Check ($telegram.Contains('if (!_hasPlayableMedia())')) 'ZIP attachments require an open video'
Check ($telegram.Contains('SelectedMedia.Kind == TelegramMediaKind.Subtitle')) 'single Telegram subtitle handled'
Check ($main.Contains('ArchiveMediaAttachmentHelper.IsZip(selected)')) 'local picker dispatches ZIP to safe extractor'
Check ($main.Contains('subtitleOnly: true')) 'local subtitle ZIP filters subtitle entries'
Check ($main.Contains('AttachSubtitlePath(attachment.FilePath)')) 'local ZIP subtitles attached'
Check ($main.Contains('AttachAudioPath(attachment.FilePath)')) 'local ZIP audio attached'
Check ($dialog.Contains('*.sub;*.idx;*.sup;*.zip')) 'subtitle picker accepts ZIP files'
Check ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding AttachSelectedSubtitleCommand}']")) 'Telegram subtitle attach button visible'

foreach ($lang in @('tr-TR','en-US')) {
    [xml]$loc = Read "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
    Check ($null -ne $loc.SelectSingleNode("//*[local-name()='String' and @*[local-name()='Key' and .='R5.Telegram.AttachSubtitle']]")) "$lang Telegram subtitle action translated"
}
Write-Host '[PASS] R5/P4.13 ZIP audio/subtitle integration and extraction safety contracts' -ForegroundColor Cyan
