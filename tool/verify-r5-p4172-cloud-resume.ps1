$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function ReadSource([string]$relative) {
  $file = Join-Path $root $relative
  if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing: $relative" }
  return [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8)
}
function Check([bool]$ok, [string]$message) {
  if (!$ok) { throw "[P4.17.2 FAIL] $message" }
  Write-Host "[PASS] $message" -ForegroundColor Green
}
$main = ReadSource 'src/AltyaziDB.Player.App/ViewModels/MainViewModel.cs'
$source = ReadSource 'src/AltyaziDB.Player.App/ViewModels/SourcePanelViewModel.cs'
$cloud = ReadSource 'src/AltyaziDB.Player.Cloud/RcloneCloudService.cs'
$policy = ReadSource 'src/AltyaziDB.Player.Core/Models/UnifiedContinueWatchingPolicy.cs'
Check ($main.Contains('isConnectedCloudSource: source =>') -and
       $main.Contains('connectedCloudRemotes.Contains(Uri.UnescapeDataString(uri.Host))')) 'Home cloud cards require a connected account snapshot'
Check ($main.Contains('!Sources.IsConnectedCloudSource(item.Source)')) 'UI rechecks current cloud account after async refresh'
Check ($main.Contains('Sources.OpenPersistentCloudMediaAsync(source)')) 'Cloud history restores via the original provider'
Check ($source.Contains('public bool IsConnectedCloudSource(string source)') -and
       $source.Contains('CloudAccounts.Any(account')) 'Only accounts present in this profile can produce cloud cards'
Check ($source.Contains('CreateOpenRequestAsync(account, item)') -and
       $source.Contains('await _openRemote(request)')) 'Resume regenerates short-lived localhost stream'
Check ($cloud.Contains('new RemoteOpenRequest(source, item.Name, headers, $"rclone:{account.RemoteName}", item.OpenUrl)')) 'Cloud source identity is the stable rclone URI'
Check ($policy.Contains('isConnectedCloudSource?.Invoke(source) == true') -and
       $policy.Contains('!uri.IsLoopback')) 'Disconnected remotes and localhost URLs are hidden'
Check ($main.Contains('recentTitle: request.DisplayName') -and
       $main.Contains('TouchRecentSource(identitySource, recentTitle)')) 'Original media name reaches history and poster matching'
Write-Host '[PASS] P4.17.2 cloud continue-watching regression' -ForegroundColor Green
