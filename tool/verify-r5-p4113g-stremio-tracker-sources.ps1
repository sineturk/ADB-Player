param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.11.3g file missing: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([bool]$condition, [string]$message) {
    if (!$condition) { throw "R5/P4.11.3g FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

$model = Source 'src\AltyaziDB.Player.Addons\Models\CatalogModels.cs'
$client = Source 'src\AltyaziDB.Player.Addons\Services\StremioAddonClient.cs'
$main = Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$torrent = Source 'src\AltyaziDB.Player.Torrent\MonoTorrentStreamingService.cs'
$handoff = Source 'src\AltyaziDB.Player.App\Services\QBittorrentHandoffClient.cs'

Require ($model.Contains('IReadOnlyList<string>? TrackerUrls = null')) 'TrackerUrls optional for existing/catalog sources'
Require ($client.Contains('var trackerUrls = ReadTrackerSources(stream, allowLocalHttp);')) 'extract addon-provided sources'
Require ($client.Contains('private static IReadOnlyList<string> ReadTrackerSources(JsonElement stream, bool allowLocalHttp)')) 'source parser present'
Require ($client.Contains('stream.TryGetProperty("sources", out var sources)')) 'reads Stremio sources array'
Require ($client.Contains('source.ValueKind != JsonValueKind.String')) 'rejects malformed source entries'
Require ($client.Contains('entry.StartsWith("tracker:", StringComparison.OrdinalIgnoreCase)')) 'only tracker source hints used'
Require ($client.Contains('tracker.Scheme != "udp"') -and $client.Contains('tracker.Scheme != "http"') -and $client.Contains('tracker.Scheme != "https"')) 'restricts tracker transport protocols'
Require ($client.Contains('!string.IsNullOrWhiteSpace(tracker.UserInfo)')) 'rejects credential-bearing URI authorities'
Require ($client.Contains('!allowLocalHttp && IsLocalNetworkHost(tracker)')) 'default local-network SSRF guard retained'
Require ($client.Contains('candidate.Length is < 8 or > 2048')) 'untrusted tracker length bounded'
Require ($client.Contains('output.Count >= 24')) 'untrusted tracker count bounded'
Require ($client.Contains('seen.Add(tracker.AbsoluteUri)')) 'tracker hints deduplicated'
Require ($client -match 'size,\s+trackerUrls\)\)') 'tracker list carried into CatalogStream'
Require ($main.Contains('stream.TrackerUrls is { Count: > 0 }')) 'only actual tracker list enhances hash-only magnet'
Require ($main.Contains('"&tr=" + Uri.EscapeDataString(tracker)')) 'tracker urls encoded as magnet tr parameters'
Require ($main.Contains('if (Uri.TryCreate(stream.Url, UriKind.Absolute, out var streamUri)')) 'explicit magnet URL handling preserved'
Require ($torrent.Contains('AllowDht = true')) 'DHT remains fallback for streams without trackers'
Require ($torrent.Contains('AutoSaveLoadMagnetLinkMetadata = true')) 'existing metadata cache remains enabled'
Require ($handoff.Contains('CheckSmartHandoffAsync')) 'qBittorrent handoff interface remains separate'
Require ($handoff.Contains('Torrent qBittorrent')) 'qB duplicate job guard remains'
Write-Host '[PASS] R5/P4.11.3g tracker-source handoff contract verified' -ForegroundColor Cyan
