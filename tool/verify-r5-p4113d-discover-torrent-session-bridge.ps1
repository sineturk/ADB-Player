param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read([string]$relative) {
    $file = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $file)) { throw "P4.11.3d source missing: $relative" }
    [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8)
}

function Require([bool]$ok, [string]$label) {
    if (!$ok) { throw "P4.11.3d FAIL: $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}

$vm = Read 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$main = Read 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$addons = Read 'src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs'

Require ($vm.Contains('private string _discoverSourceState = "Idle";')) 'Discover source status lifecycle retained'
Require ($vm.Contains('public string DiscoverSourceTitle =>')) 'selected Discover source title exposed'
Require ($vm.Contains('public string DiscoverSourceStatusText =>')) 'preparing/ready/failed/transferred status exposed'
Require ($vm.Contains('SetDiscoverSourceState("Preparing", _preferredDisplayTitle);')) 'preparation state begins with safe display title'
Require ($vm.Contains('SetDiscoverSourceState("Ready");')) 'prepared session reports ready'
Require ($vm.Contains('SetDiscoverSourceState("Failed");')) 'source failure reported on Torrent page'
Require ($vm.Contains('session.SessionId == _discoverSourceSessionId')) 'handoff completion is scoped to the source session'
Require ($vm.Contains('if (propagateFailure && Session is null)')) 'programmatic open cannot falsely report success without session'
Require ($vm.Contains('if (propagateFailure) throw new InvalidOperationException(StatusText);')) 'preflight failures propagate to caller'
Require ($vm.Contains('LocalizationManager.Get("R5.Torrent.Discover.LegalRequired")')) 'missing legal usage approval has actionable error'
Require ($vm.Contains('LocalizationManager.Get("R5.Torrent.Discover.FileMissing")')) 'missing downloaded torrent file has actionable error'
Require ($vm.Contains('await _service.RemoveAsync(session.SessionId, deleteFiles: false)')) 'handoff retains downloaded files'
Require ($vm.Contains('public double HandoffDisplayPercent => Session?.ProgressPercent ?? _handoffProgressPercent;')) 'progress remains connected to session'
Require ($main.Contains('await Torrent.OpenMagnetOrThrowAsync(')) 'Discover and Prowlarr magnet paths use strict source-opening API'
Require ($main.Contains('await Torrent.OpenTorrentFileOrThrowAsync(')) 'Discover and Prowlarr .torrent paths use strict source-opening API'
Require ($addons.Contains('_openExternalRelease(release)') -and $addons.Contains('_openStream(SelectedStream)')) 'both Prowlarr and extension routes use shared source-opening logic'

$discoverPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$torrentPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'
[xml]$discover = Read 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
[xml]$torrent = Read 'src\AltyaziDB.Player.App\Views\Pages\TorrentView.xaml'

$panel = $discover.SelectSingleNode("//*[@*[local-name()='Name' and .='DiscoverTorrentHandoffPanel']]")
Require ($null -ne $panel -and $panel.GetAttribute('Visibility') -eq 'Visible') 'Discover torrent status panel is always visible'
Require ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and @Text='{Binding Torrent.DiscoverSourceTitle}']")) 'Discover shows last source title'
Require ($null -ne $panel.SelectSingleNode(".//*[local-name()='TextBlock' and @Text='{Binding Torrent.DiscoverSourceStatusText}']")) 'Discover shows source lifecycle status'
Require ($null -ne $torrent.SelectSingleNode("//*[local-name()='TextBlock' and @Text='{Binding DiscoverSourceTitle}']")) 'Torrent tab shows last Discover source'
Require ($null -ne $torrent.SelectSingleNode("//*[local-name()='TextBlock' and @Text='{Binding DiscoverSourceStatusText}']")) 'Torrent tab displays failure if session is absent'

foreach ($locale in @('tr-TR', 'en-US')) {
    $path = "src\AltyaziDB.Player.App\Localization\Strings.$locale.xaml"
    $raw = Read $path
    [xml]$dict = $raw
    foreach ($key in @('SourceFormat','Preparing','Ready','Failed','Transferred','NoSession','LegalRequired','MagnetRequired','FileMissing')) {
        Require ($raw.Contains('x:Key="R5.Torrent.Discover.' + $key + '"')) "$locale localized $key"
    }
}
Write-Host '[PASS] R5/P4.11.3d Discover/Prowlarr-to-Torrent session bridge contract' -ForegroundColor Cyan
