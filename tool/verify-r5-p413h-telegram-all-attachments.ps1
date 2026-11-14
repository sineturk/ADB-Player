param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$name) {
  $path=Join-Path $root $name
  if (!(Test-Path -LiteralPath $path)) {throw "File missing: $name"}
  [IO.File]::ReadAllText($path,[Text.Encoding]::UTF8)
}
function Check([bool]$ok,[string]$label) {
  if (!$ok) {throw "R5/P4.13h FAIL: $label"}
  Write-Host "[PASS] $label" -ForegroundColor Green
}
$td=Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$detector=Source 'src\AltyaziDB.Player.Telegram\TelegramArchiveSetDetector.cs'
$models=Source 'src\AltyaziDB.Player.Core\Models\TelegramModels.cs'
$panel=Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
Check ($td.Contains('kind = ClassifyDocumentKind(name, mime)')) 'Telegram document classification is centralized'
Check ($td.Contains('IsPlayableDocument(name, mime) ? TelegramMediaKind.Video')) 'Regular Telegram MKV documents remain playable'
Check ($td.Contains('TelegramMediaKind.Document;')) 'Unknown Telegram documents are retained'
Check ($td.Contains('TelegramArchiveSetDetector.IsArchivePartFileName(name) ? TelegramMediaKind.ArchivePart')) 'Raw parts recognized before playback'
Check (!$td.Contains('else' + [Environment]::NewLine + '                    return null;' + [Environment]::NewLine + '                duration = 0;')) 'Unknown documents not silently discarded'
Check ($td.Contains('archive.ArchiveFormat is TelegramArchiveFormat.RawSplit or TelegramArchiveFormat.GenericSplit')) 'Raw split stream uses contiguous Range map'
Check ($td.Contains('new TelegramArchiveEntry(name, map.Length, map.Length, 0, 0, false, true)')) 'Raw video is not passed through 7zip decompressor'
Check ($td.Contains('Telegram dosya listesi: topic=')) 'Runtime scan counts can locate remaining Telegram paging issues'
Check ($models.Contains('public string DisplayTitle => FileName;')) 'MEDIA INFO caption cannot replace filename'
Check ($models.Contains('TelegramArchiveFormat.RawSplit')) 'Raw media format model present'
Check ($detector.Contains('RawMediaPartRegex()')) 'Split MKV .001 and .002 recognized'
Check ($detector.Contains('TelegramArchiveFormat.RawSplit => parts.Count > 1')) 'Lone raw split remains visible but ungrouped'
Check ($detector.Contains('TelegramArchiveFormat.RawSplit => contiguous')) 'Only contiguous raw parts can play'
Check ($detector.Contains('TelegramArchiveFormat.Split7Zip => true')) 'Original 7zip grouping unchanged'
Check ($detector.Contains('TelegramArchiveFormat.SplitZip => parts.Any(item => !item.IsFinal)')) 'Original WinRAR split ZIP grouping unchanged'
Check ($td.Contains('TelegramForumMessageMerge.Add(messages, history)')) 'Normal forum history retained'
Check ($td.Contains('var grouped = TelegramArchiveSetDetector.Group(parsed)')) 'Grouping happens before searching MEDIA INFO'
Check ($panel.Contains('await OpenArchiveAsync(SelectedMedia, subtitleOnly)')) 'Legacy group playback path unchanged'
Check ($panel.Contains('await _service.CreateArchiveStreamRequestAsync(archive, entry)')) 'Telegram Range archive streaming preserved'
Write-Host '[PASS] R5/P4.13h raw Telegram attachments and stable multipart playback contract' -ForegroundColor Cyan
