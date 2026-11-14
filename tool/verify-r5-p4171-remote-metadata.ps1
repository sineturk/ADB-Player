$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Src([string]$relative) {
  $file = Join-Path $root $relative
  if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $relative" }
  return [IO.File]::ReadAllText($file)
}
function Check([bool]$v, [string]$name) {
  if (!$v) { throw "[P4.17.1 FAIL] $name" }
  Write-Host "[PASS] $name" -ForegroundColor Green
}
$drive = Read-Src 'src/AltyaziDB.Player.Sources/RemoteSourceService.cs'
$direct = Read-Src 'src/AltyaziDB.Player.Sources/DirectStreamResolver.cs'
$source = Read-Src 'src/AltyaziDB.Player.App/ViewModels/SourcePanelViewModel.cs'
$main = Read-Src 'src/AltyaziDB.Player.App/ViewModels/MainViewModel.cs'
# ASCII-only source markers: Windows PowerShell 5.1 interprets UTF-8 without BOM as ANSI.
# Never match a Turkish-language literal in a PS5.1 .ps1 file.
Check ($drive.Contains('RemoteMediaFileName.FromGoogleDriveHtml(html)') -and
       $drive.Contains('var displayName = trustedName') -and
       $drive.Contains('trustedName ?? "google-drive-media.mkv"') -and
       $main.Contains('recentTitle: request.DisplayName')) 'Drive confirmation filename is passed to playback and history'
Check ($drive.Contains('256 * 1024,') -and
       $drive.Contains('TryExtractGoogleDriveDownloadUri(html, finalUri)')) 'Drive filename parsing reuses bounded confirmation HTML'
Check ($drive.Contains('TryReadIsolatedGoogleDriveFileNameAsync') -and
       $drive.Contains('UseCookies = false') -and
       $drive.Contains('publicPreviewHandler') -and
       $drive.Contains('_googleDrivePreviewClient.SendAsync') -and
       $drive.Contains('Timeout = TimeSpan.FromSeconds(3)')) 'Public preview metadata uses isolated cookie-free client with timeout'
Check ($drive.Contains('RemoteMediaFileName.FromGoogleDriveHtml(html)') -and
       $drive.Contains('trustedName = await TryReadIsolatedGoogleDriveFileNameAsync')) 'Public preview filename is delivered to the existing playback title'
Check ($direct.Contains('RemoteMediaFileName.FromContentDisposition(response.Content.Headers.ContentDisposition)')) 'Direct HTTP metadata is preferred'
Check ($source.Contains('HasResolvedMediaName(result)')) 'Saved links learn real media filenames'
Check ($main.Contains('recentTitle: request.DisplayName') -and $main.Contains('TouchRecentSource(identitySource, recentTitle)')) 'Existing playback-to-poster flow receives resolved title'
Check ($main.Contains('if (misses.Count == 0 || !Account.IsSignedIn)')) 'Poster auth guard remains intact'
Check (!$drive.Contains('DownloadToFileAsync') -or $drive.Contains('RemoteMediaFileName.FromGoogleDriveHtml')) 'Metadata lookup does not require full download'
Write-Host '[PASS] P4.17.1 metadata resolution is wired to existing posters and playback safely' -ForegroundColor Green
