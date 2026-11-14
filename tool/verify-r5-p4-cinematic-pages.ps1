param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Required([string]$rel) {
    $p = Join-Path $root $rel
    if (!(Test-Path -LiteralPath $p)) { throw "R5/P4 file missing: $rel" }
    return [IO.File]::ReadAllText($p)
}
function Require([string]$body, [string]$needle, [string]$scope) {
    if (!$body.Contains($needle)) { throw "R5/P4 $scope missing: $needle" }
    Write-Host "[PASS] $scope : $needle" -ForegroundColor Green
}
$views = @(
    'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml',
    'src\AltyaziDB.Player.App\Views\Pages\LibraryView.xaml',
    'src\AltyaziDB.Player.App\Views\Pages\PlaylistView.xaml'
)
$locales = @(
    'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml',
    'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
)
foreach ($p in @($views + $locales + @('src\AltyaziDB.Player.App\Themes\R2.Tokens.xaml'))) {
    $full = Join-Path $root $p
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($full)
    # WPF templates create their own namescopes. A repeated x:Name in
    # separate ControlTemplates or DataTemplates is valid XAML.
    # The actual compiled XAML/BAML namescope validation follows in dotnet build.
    Write-Host "[PASS] XML parsed: $p" -ForegroundColor Green
}
$explore = Read-Required $views[0]
$library = Read-Required $views[1]
$queue = Read-Required $views[2]
$exploreCode = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml.cs'
$libraryCode = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\LibraryView.xaml.cs'
$queueCode = Read-Required 'src\AltyaziDB.Player.App\Views\Pages\PlaylistView.xaml.cs'
$token = Read-Required 'src\AltyaziDB.Player.App\Themes\R2.Tokens.xaml'
$main = Read-Required 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$libModel = Read-Required 'src\AltyaziDB.Player.App\ViewModels\LibraryPanelViewModel.cs'

foreach ($key in @('R5DiscoverToolbar','R5DiscoverStatCard','R5DiscoverCatalogPanel',
                   'R5LibraryHeroPanel','R5LibraryFolderPanel','R5LibraryStage',
                   'R5PlaylistHero','R5PlaylistWorkspace','R5PlaylistCloud',
                   'R5PlaylistActions')) {
    Require $token ('x:Key="' + $key + '"') 'theme'
}
foreach ($key in @('R5.Discover.SearchLabel','R5.Discover.Matches',
                   'R5.Library.SearchLabel','R5.Library.ResetFilters',
                   'R5.Playlist.HeroEyebrow','R5.Playlist.QueueCount',
                   'R5.Playlist.Selected','R5.Playlist.QueueAccessLabel')) {
    foreach ($locale in $locales) {
        $body = Read-Required $locale
        Require $body ('x:Key="' + $key + '"') $locale
    }
}
foreach ($name in @('Discover_OnSizeChanged','DiscoverClearSearch_Click',
                    'CatalogCard_OnMouseEnter','CatalogCard_OnMouseLeave')) {
    Require $exploreCode $name 'Discover code'
    Require $explore $name 'Discover XAML'
}
foreach ($name in @('Library_OnSizeChanged','LibraryClearSearch_Click',
                    'LibraryResetFilters_Click','LibraryPoster_OnMouseEnter',
                    'LibraryPoster_OnMouseLeave')) {
    Require $libraryCode $name 'Library code'
    Require $library $name 'Library XAML'
}
foreach ($name in @('Queue_OnSizeChanged','QueueItemPlay_Click','Queue_OnDoubleClick',
                    'Queue_OnKeyDown')) {
    Require $queueCode $name 'Playlist code'
    Require $queue $name 'Playlist XAML'
}
foreach ($name in @('Binding Addons.SearchText','Binding Addons.SelectedFilter',
                    'Binding Addons.RefreshCommand','Binding Addons.Items.Count',
                    'Binding Addons.IsBusy')) {
    Require $explore $name 'Discover preserved contract'
}
foreach ($name in @('Binding SearchText','Binding SelectedFilter','Binding SelectedSort',
                    'Binding SelectedScope','Binding AddFolderCommand','Binding ScanAllCommand',
                    'Binding RemoveFolderCommand','Binding GridItems','Binding Items',
                    'Binding PlaySelectedCommand','Binding SelectedGridItem')) {
    Require $library $name 'Library preserved contract'
}
foreach ($name in @('Binding Playlist','Binding SelectedPlaylistItem',
                    'Binding AutoPlayNext','Binding CloudPlaylistStatus',
                    'Binding CloudPlaylistCountText','Binding LoadCloudPlaylistCommand',
                    'Binding SyncPlaylistToCloudCommand','Binding PlaySelectedCommand',
                    'Binding RemoveSelectedCommand','Binding ClearPlaylistCommand')) {
    Require $queue $name 'Playlist preserved contract'
}
Require $queueCode 'vm.SelectedPlaylistItem = item;' 'Playlist per-row playback selection'
Require $queueCode 'await vm.PlaySelectedAsync()' 'Playlist per-row playback'
Require $main 'private void QueueCloudPlaylistSync()' 'MainViewModel'
Require $libModel 'public ObservableCollection<LibraryFolder> Folders' 'LibraryViewModel'
if ($queue.Contains('NullToBooleanConverter') -or $queue.Contains('http://127.0.0.1')) {
    throw 'Unexpected missing converter or OAuth state in Playlist UI'
}
Write-Host '[PASS] R5/P4.1-P4.3 cinematic source guards complete' -ForegroundColor Cyan
