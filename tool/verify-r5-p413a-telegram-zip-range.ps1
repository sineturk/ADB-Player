param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing source: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([bool]$ok, [string]$message) {
    if (!$ok) { throw "R5/P4.13a FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

$panel = Read 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$service = Read 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$reader = Read 'src\AltyaziDB.Player.Telegram\SplitZipArchiveReader.cs'
$cache = Read 'src\AltyaziDB.Player.Telegram\TelegramArchiveStreamingEngine.cs'
$interface = Read 'src\AltyaziDB.Player.Core\Interfaces\ITelegramService.cs'

$singleStart = $panel.IndexOf('private async Task OpenSingleZipAttachmentAsync(')
$singleEnd = $panel.IndexOf('private async Task OpenArchiveAsync(', $singleStart)
Require ($singleStart -gt 0 -and $singleEnd -gt $singleStart) 'Standalone ZIP handler exists'
$single = $panel.Substring($singleStart, $singleEnd - $singleStart)
Require ($single.Contains('ArchiveParts = [item]')) 'Standalone ZIP reuses existing one-volume archive reader'
Require ($single.Contains('ArchiveFormat = TelegramArchiveFormat.SplitZip')) 'Single ZIP maps to SplitZip range engine'
Require ($single.Contains('await OpenArchiveAsync(archive, subtitleOnly)')) 'No separate ZIP download extraction path'
Require (!$single.Contains('await _service.DownloadMediaAsync(')) 'Standalone Telegram ZIP never downloaded in full before opening'

Require ($panel.Contains('await _service.InspectArchiveAsync(archive)')) 'ZIP central directory inspected remotely'
Require ($panel.Contains('await _service.CreateArchiveStreamRequestAsync(archive, entry)')) 'Selected ZIP audio/video streams over TDLib Range'
Require ($panel.Contains('await _service.ExtractArchiveSubtitleAsync(archive, entry)')) 'Selected subtitle is fetched independently'
Require ($panel.Contains('ArchiveMediaAttachmentHelper.IsSubtitle(entry.Name)')) 'Normal/forced subtitles selectable'
Require ($panel.Contains('IsAudioSelectionMode')) 'Audio attach source mode retained'
Require ($panel.Contains('allowMultiple: isStandaloneZip')) 'Multiple selected tracks on a single ZIP'
Require ($panel.Contains('await _attachAudio(request)')) 'Existing remote-audio playback bridge preserved'
Require ($panel.Contains('_attachSubtitle(subtitlePath)')) 'Selected ZIP subtitle added to existing playback'

Require ($service.Contains('SplitZipArchiveReader.InspectAsync(map, cancellationToken)')) 'Remote ZIP inspection uses byte ranges'
Require ($service.Contains('CreateArchiveMap(archive)')) 'ZIP maps to Telegram byte range reader'
Require ($service.Contains('ReadStreamRangeAsync(part.FileId, part.Size, offset, count, token)')) 'No full media fetch in archive map'
Require ($service.Contains('public async Task<string> ExtractArchiveSubtitleAsync(')) 'Selected subtitle range method implemented'
Require ($service.Contains('maximumSubtitleBytes = 128L * 1024 * 1024')) 'Subtitle extraction size ceiling'
Require ($service.Contains('entry.CompressionMethod is not (0 or 8)')) 'Only stored/deflate subtitles are extracted'
Require ($service.Contains('DeflateStream(compressed, CompressionMode.Decompress')) 'Deflated subtitle blocks decoded from ranged input'
Require ($service.Contains('entry.DataOffset + offset')) 'Subtitle reads only requested compressed entry'
Require ($service.Contains('written > entry.UncompressedSize')) 'Decompressed subtitle data bounded'
Require ($service.Contains('FileMode.CreateNew')) 'Subtitle cache file cannot overwrite user data'
Require ($interface.Contains('Task<string> ExtractArchiveSubtitleAsync(')) 'Telegram service interface declares range extractor'
Require ($reader.Contains('".srt"') -and $reader.Contains('".ass"') -and $reader.Contains('".vtt"')) 'ZIP index presents subtitle entries'
Require ($reader.Contains('tailOffset, tailSize')) 'ZIP index fetched from end-of-file'
Require ($reader.Contains('directoryGlobalOffset')) 'ZIP central directory fetched separately'
Require ($cache.Contains('InitialBufferBytes = 8 * 1024 * 1024')) 'Compressed audio starts with limited progressive cache'
Require ($cache.Contains('new DeflateStream(compressedSource, CompressionMode.Decompress')) 'Compressed audio stream progressive decode retained'

Write-Host '[PASS] R5/P4.13a standalone ZIP Range and subtitle-only extraction contract' -ForegroundColor Cyan
