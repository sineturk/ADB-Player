param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function ReadSource([string]$path) {
    $full = Join-Path $root $path
    if (!(Test-Path -LiteralPath $full)) { throw "P4.11.3e source missing: $path" }
    return [IO.File]::ReadAllText($full,[Text.Encoding]::UTF8)
}
function Assert([bool]$condition,[string]$message) {
    if (!$condition) { throw "P4.11.3e FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

$addon = ReadSource 'src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs'
$main = ReadSource 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$torrentVm = ReadSource 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
[xml]$discover = ReadSource 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
[xml]$legacy = ReadSource 'src\AltyaziDB.Player.App\MainWindow.xaml'
[xml]$modern = ReadSource 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'

Assert ($addon.Contains('public string SelectedStreamTransportText => SelectedStream?.Kind switch')) 'Discover source type derived from real CatalogStreamKind'
Assert ($addon.Contains('CatalogStreamKind.Direct => LocalizationManager.Get("R5.Torrent.Discover.KindDirect")')) 'direct HTTP stream is not mislabeled as torrent'
Assert ($addon.Contains('CatalogStreamKind.Torrent => LocalizationManager.Get("R5.Torrent.Discover.KindTorrent")')) 'actual torrent source explicitly identified'
Assert ($addon.Contains('CatalogStreamKind.External => LocalizationManager.Get("R5.Torrent.Discover.KindExternal")')) 'Prowlarr/Torznab external source explicitly identified'
Assert ($addon.Contains('OnPropertyChanged(nameof(SelectedStreamTransportText))')) 'source kind updates when stream selection changes'
Assert ($main.Contains('Torrent.NotifyDirectSourceOpened(SelectedAddonTitle(stream));')) 'successful direct playback reports non-torrent source'
Assert ($main.Contains('await OpenSourceArgumentAsync(directUri.AbsoluteUri)')) 'direct playback route preserved'
Assert ($main.Contains('await Torrent.OpenMagnetOrThrowAsync(')) 'real magnet sources continue to use torrent engine'
Assert ($main.Contains('await Torrent.OpenTorrentFileOrThrowAsync(')) '.torrent sources continue to use torrent engine'
Assert ($torrentVm.Contains('public void NotifyDirectSourceOpened(string displayTitle)')) 'direct stream tracked without creating a fake torrent session'
Assert ($torrentVm.Contains('SetDiscoverSourceState("Direct", displayTitle.Trim());')) 'direct stream status uses only a safe display title'
Assert ($torrentVm.Contains('public string DiscoverSourceStatusText =>')) 'Torrent view model exports source status'
Assert ($torrentVm.Contains('public double HandoffDisplayPercent => Session?.ProgressPercent ?? _handoffProgressPercent;')) 'real torrent session remains sole download progress source'
Assert ($torrentVm.Contains('await _service.RemoveAsync(session.SessionId, deleteFiles: false)')) 'qBittorrent handoff data retention remains intact'
Assert ($null -ne $discover.SelectSingleNode("//*[local-name()='TextBlock' and @Text='{Binding Addons.SelectedStreamTransportText}']")) 'Discover detail modal shows selected source transport'
Assert ($null -ne $legacy.SelectSingleNode("//*[local-name()='TextBlock' and @Text='{Binding DiscoverSourceTitle}']")) 'player legacy Torrent panel shows last Discover source'
Assert ($null -ne $legacy.SelectSingleNode("//*[local-name()='TextBlock' and @Text='{Binding DiscoverSourceStatusText}']")) 'player legacy Torrent panel explains missing torrent session'
Assert ($null -ne $modern.SelectSingleNode("//*[local-name()='TextBlock' and @Text='{Binding DiscoverSourceStatusText}']")) 'modern Torrent tab has matching source explanation'

foreach ($language in @('tr-TR','en-US')) {
    $text = ReadSource "src\AltyaziDB.Player.App\Localization\Strings.$language.xaml"
    [xml]$dictionary = $text
    foreach ($key in @('KindDirect','KindTorrent','KindExternal','Direct')) {
        Assert ($text.Contains('x:Key="R5.Torrent.Discover.' + $key + '"')) "$language translation for $key"
    }
}
Write-Host '[PASS] P4.11.3e stream-source classification checks passed' -ForegroundColor Cyan
