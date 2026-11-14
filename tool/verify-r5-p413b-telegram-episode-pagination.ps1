param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$path) {
    $full = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $full)) { throw "Missing source $path" }
    [IO.File]::ReadAllText($full,[Text.Encoding]::UTF8)
}
function Assert([bool]$ok,[string]$title) {
    if (!$ok) { throw "R5/P4.13b FAIL: $title" }
    Write-Host "[PASS] $title" -ForegroundColor Green
}
$tdlib=Source 'src\AltyaziDB.Player.Telegram\TdJsonTelegramService.cs'
$viewModel=Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$main=Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$matcher=Source 'src\AltyaziDB.Player.App\Services\TelegramEpisodeTrackMatcher.cs'
[xml]$view=Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'

Assert ($tdlib.Contains('limit = Math.Clamp(limit, 1, 2500);')) 'Media listing can request more than one Telegram page'
Assert ($tdlib.Contains('while (fetched < limit)')) 'Each media filter paginates beyond the first 100'
Assert ($tdlib.Contains('["from_message_id"] = cursor.ToString(CultureInfo.InvariantCulture)')) 'Every page passes an older message cursor'
Assert ($tdlib.Contains('if (oldest == long.MaxValue || advanced == 0 ||')) 'Pagination stops when cursor cannot advance'
Assert ($tdlib.Contains('if (messages.Count == 0 && forumTopicId is > 0)')) 'Forum topics without filtered results get paged fallback'
Assert ($tdlib.Contains('["filter"] = null')) 'Forum fallback can fetch all message content types'
Assert ($tdlib.Contains('TelegramArchiveSetDetector.Group(parsed)')) 'Split archive pieces still grouped after pagination'
Assert ($viewModel.Contains('LoadMoreMediaCommand = new AsyncRelayCommand(LoadMoreMediaAsync)')) 'Load older media is a working action'
Assert ($viewModel.Contains('var nextLimit = Math.Min(2500, _mediaResultLimit + 300)')) 'Cumulative pages capped to avoid unbounded network fetch'
Assert ($viewModel.Contains('SelectedMedia = previous is null')) 'Selected media preserved after fetching older results'
Assert ($viewModel.Contains('TelegramEpisodeTrackMatcher.MatchingIndices(')) 'Season ZIP uses exact current episode match'
Assert ($viewModel.Contains('initial') -or $viewModel.Contains('matchingSet.Contains(index)')) 'Selection UI visually prioritizes matching tracks'
Assert ($viewModel.Contains('hasCodedOtherEpisode')) 'Unknown episode does not silently open another episode'
Assert ($viewModel.Contains('allowMultiple: isStandaloneZip')) 'User can choose normal/forced subtitles from one ZIP'
Assert ($main.Contains('() => $"{Snapshot.Title} {Path.GetFileName(_currentPlaybackSource ?? Snapshot.Source)}"')) 'Playback title and file path inform matcher'
Assert ($matcher.Contains('SeasonEpisodeRegex()') -and $matcher.Contains('XEpisodeRegex()')) 'Supports S01E05 and 1x05 patterns'
Assert ($matcher.Contains('Parse(entry.name) == playing')) 'Matches both season and episode'
Assert ($null -ne $view.SelectSingleNode("//*[local-name()='Button' and @Command='{Binding LoadMoreMediaCommand}']")) 'Load older records action shown in Telegram UI'
foreach($language in @('tr-TR','en-US')) {
    $loc=Source "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"
    [xml]$xml=$loc
    Assert ($loc.Contains('x:Key="R5.Telegram.LoadOlder"')) "$language load-more label"
    Assert ($loc.Contains('x:Key="R5.Telegram.UnreadTooltip"')) "$language unread count explanation"
}
Write-Host '[PASS] R5/P4.13b Telegram pagination and season episode selection contracts' -ForegroundColor Cyan
