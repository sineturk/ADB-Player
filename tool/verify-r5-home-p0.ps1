param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$homePath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml'
$codePath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml.cs'
$trPath = Join-Path $root 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$enPath = Join-Path $root 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'

function Read-Required([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing source: $path" }
    return [IO.File]::ReadAllText($path)
}

function Require-Text([string] $value, [string] $needle) {
    if (-not $value.Contains($needle)) { throw "R5/P0 source guard failed: $needle" }
    Write-Host "[PASS] $needle" -ForegroundColor Green
}

$homeXaml = Read-Required $homePath
$code = Read-Required $codePath
$tr = Read-Required $trPath
$en = Read-Required $enPath
$cardModel = Read-Required (Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\ContinueWatchingItem.cs')
$groupModel = Read-Required (Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\HomeResumeGroup.cs')
$artworkPolicy = Read-Required (Join-Path $root 'src\AltyaziDB.Player.App\Services\ContinueWatchingArtworkPolicy.cs')
$mainVm = Read-Required (Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs')
$sourcePresentation = Read-Required (Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\HomeSourcePresentation.cs')
$sourcesXamlPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$sourcesXaml = Read-Required $sourcesXamlPath
$sourcesCode = Read-Required (Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml.cs')
$themePath = Join-Path $root 'src\AltyaziDB.Player.App\Themes\R2.Tokens.xaml'
$theme = Read-Required $themePath

foreach ($file in @($homePath, $sourcesXamlPath, $themePath, $trPath, $enPath)) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($file)
    Write-Host "[PASS] Valid XML: $(Split-Path -Leaf $file)" -ForegroundColor Green
}

# Protect the existing resume identity; P0 never groups/deduplicates distinct episodes.
Require-Text $homeXaml 'Content="{Binding ContinueWatching[0]}"'
Require-Text $homeXaml 'ItemsSource="{Binding ResumeGroups, ElementName=HomeRoot}"'
Require-Text $homeXaml 'Click="Resume_Click"'
Require-Text $homeXaml 'Command="{Binding OpenFileCommand}"'
Require-Text $homeXaml 'Command="{Binding OpenUrlCommand}"'
Require-Text $homeXaml 'Command="{Binding ShowTorrentCommand}"'
Require-Text $homeXaml 'Command="{Binding ShowTelegramCommand}"'
Require-Text $homeXaml 'Click="OpenSources_Click"'
Require-Text $homeXaml 'Click="OpenConnection_Click"'
Require-Text $homeXaml 'Content="{DynamicResource R5.Home.Resume}"'
Require-Text $homeXaml 'ItemsSource="{Binding CloudPreview, ElementName=HomeRoot}"'
Require-Text $homeXaml 'ItemsSource="{Binding ConnectionPreview, ElementName=HomeRoot}"'
Require-Text $homeXaml 'Text="{Binding Latest.EpisodeLabel}"'
Require-Text $homeXaml 'ToolTip="{DynamicResource R5.Home.OpenSaved}"'
Require-Text $code 'model.Sources.OpenCloudAccountAsync(tile.Account)'
Require-Text $code 'model.Sources.OpenSavedLinkAsync(link)'
Require-Text $code 'model.Sources.OpenWebDavProfileAsync(profile)'
Require-Text $code 'accounts.Take(4)'
Require-Text $code 'SavedPublicLinks.Take(5)'
Require-Text $code 'ContinueWatchingItem item'
Require-Text $homeXaml 'Click="ShowGroupEpisodes_Click"'
Require-Text $homeXaml 'Latest.RemainingTimeText'
Require-Text $homeXaml 'Latest.ProgressPercentText'
Require-Text $code 'ShowGroupEpisodes_Click'
Require-Text $code 'ResumeEpisodeMenuItem_Click'
Require-Text $code 'ResumeGroups.Add(group)'
Require-Text $cardModel 'public string GroupKey'
Require-Text $cardModel 'public bool HasEpisode'
Require-Text $groupModel 'GroupBy(item => item.GroupKey'
Require-Text $groupModel 'OrderByDescending(item => item.UpdatedAtUtc)'
Require-Text $artworkPolicy 'exactTitle && double.IsFinite(confidence)'
Require-Text $artworkPolicy 'requested.IsSeries ? !isSeries : !isMovie'
Require-Text $mainVm 'ContinueWatchingArtworkPolicy.IsTrusted'
Require-Text $mainVm 'cached.VerifiedForHome'
Require-Text $mainVm 'BuildResumeIdentity('
Require-Text $code 'CinematicArtwork.ApplyIfAvailable(HomeHero, "hero-home.jpg")'
# P2: source collection identity is never mutated by rendering or filtering.
Require-Text $code 'ConnectionPreview.Add(HomeSourcePresentation.Link(link, stored))'
Require-Text $code 'ConnectionPreview.Add(HomeSourcePresentation.WebDav(profile, stored))'
Require-Text $code 'HomeSourcePresentation.Cloud('
Require-Text $sourcePresentation 'StableSuffix(account.Id, account.RemoteName)'
Require-Text $sourcePresentation 'Uri.TryCreate(raw, UriKind.Absolute'
Require-Text $sourcePresentation 'No connectivity check is implied'
Require-Text $sourcesXaml 'ItemsSource="{Binding FilteredCloudAccounts, ElementName=SourcesRoot}"'
Require-Text $sourcesXaml 'ItemsSource="{Binding FilteredSavedLinks, ElementName=SourcesRoot}"'
Require-Text $sourcesXaml 'ItemsSource="{Binding FilteredWebDavProfiles, ElementName=SourcesRoot}"'
Require-Text $sourcesXaml 'SourcesSearchBox_OnTextChanged'
Require-Text $sourcesXaml 'CloudOpen_Click'
Require-Text $sourcesXaml 'CloudDisconnect_Click'
Require-Text $sourcesXaml 'LinkOpen_Click'
Require-Text $sourcesXaml 'LinkRemove_Click'
Require-Text $sourcesXaml 'WebDavOpen_Click'
Require-Text $sourcesXaml 'WebDavRemove_Click'
Require-Text $sourcesCode 'FilteredCloudAccounts.Add(account)'
Require-Text $sourcesCode 'FilteredSavedLinks.Add(link)'
Require-Text $sourcesCode 'FilteredWebDavProfiles.Add(profile)'

# P3: verify scoped glass visuals, focus states, adaptive layout and reduced motion.
Require-Text $theme 'x:Key="R5GlassHeroPanel"'
Require-Text $theme 'x:Key="R5GlassMediaCardButton"'
Require-Text $theme 'x:Key="R5GlassSourceButton"'
Require-Text $theme 'x:Key="R5GlassQuickButton"'
Require-Text $theme 'x:Key="R5GlassHomePanel"'
Require-Text $theme 'x:Key="R5GlassWorkspacePanel"'
Require-Text $theme 'IsKeyboardFocused'
Require-Text $homeXaml 'Style="{StaticResource R5GlassHeroPanel}"'
Require-Text $homeXaml 'Style="{StaticResource R5GlassMediaCardButton}"'
Require-Text $homeXaml 'Style="{StaticResource R5GlassQuickButton}"'
Require-Text $homeXaml 'Style="{StaticResource R5GlassHomePanel}"'
Require-Text $homeXaml 'Style="{StaticResource R5GlassSourceButton}"'
Require-Text $homeXaml 'HomeView_OnSizeChanged'
Require-Text $homeXaml 'HomeCard_OnMouseEnter'
Require-Text $homeXaml 'HomeCard_OnMouseLeave'
Require-Text $homeXaml 'AutomationProperties.HelpText="{DynamicResource R5.Home.ResumeHint}"'
Require-Text $code 'SystemParameters.HighContrast'
Require-Text $code 'SystemParameters.ClientAreaAnimation'
Require-Text $code 'HomeSourcesSecondColumn.Width'
Require-Text $code 'HomeQuickActions.Columns'
Require-Text $sourcesXaml 'SourcesView_OnSizeChanged'
Require-Text $sourcesXaml 'SourcesClearSearch_Click'
Require-Text $sourcesXaml 'Style="{StaticResource R5GlassWorkspacePanel}"'
Require-Text $sourcesCode 'SourcesOtherCards.Columns ='
Require-Text $sourcesCode 'SourcesSearchBox.Clear()'

if ($homeXaml.Contains('ToolTip="{Binding Name}"') -or
    $homeXaml.Contains('Text="{Binding Url}"') -or
    $homeXaml.Contains('Text="{Binding BaseUrl}"')) {
    throw "R5/P2 must not show private URLs or raw saved-link tooltips on Home."
}


if ($homeXaml.Contains('ItemsSource="{Binding Sources.SavedPublicLinks}"') -or
    $homeXaml.Contains('ToolTip="{Binding Name}"')) {
    throw "R5/P0 cannot display an unbounded saved URL list or raw URL tooltip."
}
$quickIndex = $homeXaml.IndexOf('Text="{DynamicResource R23.Home.QuickActions}"')
$resumeIndex = $homeXaml.IndexOf('Text="{DynamicResource Home.ContinueWatching}"')
if ($quickIndex -lt 0 -or $resumeIndex -lt 0 -or $quickIndex -ge $resumeIndex) {
    throw "R5/P0 quick actions must be below the hero and before the resume rail."
}

foreach ($key in @('R5.Home.Featured', 'R5.Home.Resume', 'R5.Home.MySources',
                   'R5.Home.CloudPreview', 'R5.Home.LinkPreview',
                   'R5.Home.ViewAll', 'R5.Home.OpenSaved',
                   'R5.Home.Remaining', 'R5.Home.Episodes', 'R5.Home.SelectEpisode',
                   'R5.Home.OpenCloud', 'R5.Sources.RuntimeReady',
                   'R5.Sources.RuntimeUnavailable', 'R5.Sources.Stored',
                   'R5.Sources.SearchLabel', 'R5.Sources.SearchHint',
                   'R5.Sources.NoSearchResults', 'R5.Sources.NoConnectionResults',
                   'R5.Sources.RuntimeNote', 'R5.Sources.SavedHelp',
                   'R5.Home.ResumeHint', 'R5.Sources.ClearSearch')) {
    Require-Text $tr "x:Key=""$key"""
    Require-Text $en "x:Key=""$key"""
}

Write-Host '[PASS] R5 P0-P3 cinematic Home, Sources, visual and source guards complete' -ForegroundColor Cyan
