$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function ReadSource([string]$path) {
    $file = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $file)) { throw "[P415] Missing: $path" }
    [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
}
function Check([bool]$ok,[string]$name) {
    if (-not $ok) { throw "[P415 FAIL] $name" }
    Write-Host "[PASS] $name" -ForegroundColor Green
}
$xaml = ReadSource 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$homeXaml = ReadSource 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml'
$homeCode = ReadSource 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml.cs'
$mainXaml = ReadSource 'src\AltyaziDB.Player.App\MainWindow.xaml'
$main = ReadSource 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$telegram = ReadSource 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$settings = ReadSource 'src\AltyaziDB.Player.Core\Models\PlayerSettings.cs'
$models = ReadSource 'src\AltyaziDB.Player.Core\Models\TelegramModels.cs'
$view = ReadSource 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml.cs'
$window = ReadSource 'src\AltyaziDB.Player.App\MainWindow.xaml.cs'
$engine = ReadSource 'src\AltyaziDB.Player.Playback\MpvPlaybackEngine.cs'
$interface = ReadSource 'src\AltyaziDB.Player.Core\Interfaces\IPlaybackEngine.cs'
$storedRange = ReadSource 'src\AltyaziDB.Player.Telegram\TelegramSevenZipStoredRangeResolver.cs'
$tr = ReadSource 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = ReadSource 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'

foreach ($document in @($xaml,$homeXaml,$mainXaml,$tr,$en)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.LoadXml($document)
}
Check ($settings.Contains('TelegramFavoriteChatIds')) 'Favorites persisted in device settings'
Check ($models.Contains('FavoriteGlyph')) 'Filled and outline star models available'
Check ($telegram.Contains('FilteredChats') -and $telegram.Contains('FavoriteChats') -and $telegram.Contains('RecentMedia')) 'Telegram filter, favorites, recent hub'
Check ($telegram.Contains('ToggleFavoriteChatAsync') -and $telegram.Contains('OpenRecentMediaAsync')) 'Favorite and recent actions are connected'
Check ($xaml.Contains('Telegram.Hub.Filter') -and $homeXaml.Contains('R5.TelegramHub.RecentTitle')) 'Telegram chat filter and Home recents are on their proper screens'
Check ($view.Contains('TelegramFavoriteToggle_OnClick') -and $homeCode.Contains('OpenTelegramRecent_Click')) 'Telegram star and Home recent actions exist'
Check ($telegram.Contains('_globalSearchMediaIds') -and $telegram.Contains('limit: 2500')) 'Global search archive playback rehydrates source chat'
Check ($main.Contains('return Telegram.OpenPersistentAsync(source)')) 'Resume cards restore Telegram stream, not expired HTTP source'
Check ($telegram.Contains('preferredEntryName') -and $telegram.Contains('matchingIndex')) 'Resume restores exact archive movie entry'
Check ($main.Contains('_primarySubtitleTrackDelays') -and $main.Contains('_secondarySubtitleTrackDelays')) 'Subtitle timing is isolated per track'
Check ($main.Contains('_primarySubtitleTrackDelays.Clear()')) 'Subtitle timing is reset when media changes'
Check ($mainXaml.Contains('Player.VideoLayout.Fit') -and $mainXaml.Contains('Player.VideoLayout.Fill')) 'Bottom player menu offers fit and fill'
Check ($engine.Contains('SetVideoFill(bool fill)') -and $interface.Contains('SetVideoFill(bool fill)')) 'Video fit/fill uses mpv property'
Check ($window.Contains('Forms.Cursor.Hide();') -and $window.Contains('Forms.Cursor.Show();')) 'Native cursor hide/show balanced'
Check ($storedRange.Contains('decoded.AsSpan().SequenceEqual(direct)')) '7z Store Range validation untouched'
$keys = [regex]::Matches(($xaml + $homeXaml + $mainXaml), '\{DynamicResource\s+(Player.VideoLayout.[A-Za-z]+|Telegram.Hub.[A-Za-z]+|R5.TelegramHub.[A-Za-z]+)\}') |
 ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
foreach ($key in $keys) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and $en.Contains('x:Key="' + $key + '"')) ('TR/EN parity: ' + $key)
}
Write-Host '[PASS] P4.15 Telegram UX / resume / subtitles / player contract' -ForegroundColor Cyan
