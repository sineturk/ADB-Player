$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $file = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $file)) { throw "Missing: $relative" }
    return [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8)
}
function Check([bool]$condition, [string]$label) {
    if (!$condition) { throw "[P4.15.5 FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$policy = Source 'src\AltyaziDB.Player.Core\Models\LocalAudioFilePolicy.cs'
$main = Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$dialog = Source 'src\AltyaziDB.Player.App\Services\WpfDialogService.cs'
$mpv = Source 'src\AltyaziDB.Player.Playback\MpvPlaybackEngine.cs'
$library = Source 'src\AltyaziDB.Player.Library\SqliteMediaLibrary.cs'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'

foreach ($xmlText in @($tr, $en)) {
    $doc = New-Object System.Xml.XmlDocument
    $doc.LoadXml($xmlText)
}
foreach ($ext in @('.mp3','.flac','.wav','.m4a','.aac','.ogg','.opus','.wma','.mka')) {
    Check ($policy.Contains('"' + $ext + '"')) ('Local audio format: ' + $ext)
}
Check ($policy.Contains('StringComparer.OrdinalIgnoreCase') -and
       $policy.Contains('IsPlayable(') -and
       $policy.Contains('IsAudio(')) 'Audio file detection is case-insensitive and reusable'
Check ($dialog.Contains('Dialog.MediaFiles') -and
       $dialog.Contains('Dialog.MediaPickerTitle') -and
       $dialog.Contains('LocalAudioFilePolicy.FileDialogPattern') -and
       $dialog.Contains('Dialog.AllFiles')) 'Main media picker exposes both video and audio'
Check ($main.Contains('ExpandPlayableMediaSources(paths)') -and
       $main.Contains('LocalAudioFilePolicy.IsPlayable(path, VideoExtensions)') -and
       $main.Contains('LocalAudioFilePolicy.IsPlayable(file, VideoExtensions)')) 'Direct file, folder and drag-and-drop paths allow audio'
Check ($main.Contains('var requestedAudio = LocalAudioFilePolicy.IsAudio(fullPath);') -and
       $main.Contains('? LocalAudioFilePolicy.IsAudio(candidate)') -and
       $main.Contains('VideoExtensions.Contains(Path.GetExtension(candidate))')) 'Auto folder playlist does not mix audio tracks with video episodes'
Check ($main.Contains('Player.NoSupportedMediaFiles') -and
       $main.Contains('LocalAudioFilePolicy.IsPlayable(selected[0], VideoExtensions)')) 'Unsupported file picker input fails without opening a sibling'
Check ($mpv.Contains('ExecuteCommand("loadfile", source, "replace")') -and
       $mpv.Contains('SetOption("audio-client-name"')) 'Existing libmpv player is reused; no transcoding service required'
Check ($library.Contains('EnumerateVideoFilesSafe') -and
       $library.Contains('VideoExtensions.Contains(Path.GetExtension(file))')) 'Movie/TV library still excludes audio pending separate music UX'
foreach ($key in @('Dialog.MediaPickerTitle','Dialog.MediaFiles','Player.NoSupportedMediaFiles')) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR/EN resource: ' + $key)
}
Write-Host '[PASS] P4.15.5 audio file picker and local playback list contracts; no live native MPV test' -ForegroundColor Cyan
