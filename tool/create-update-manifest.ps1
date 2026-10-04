param(
    [Parameter(Mandatory=$true)][string]$InstallerBaseUrl,
    [Parameter(Mandatory=$true)][string]$PortableBaseUrl,
    [ValidateSet("stable", "preview")][string]$Channel = "stable",
    [string]$ReleaseNotes = "ADB Player V1.0 Windows yayın sürümü.",
    [switch]$Mandatory
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$version = Get-ReleaseVersion
$release = Join-Path $root "artifacts\release"
$installer = Join-Path $root "artifacts\installer\ADB-Player-Setup-v$version-x64.exe"
$portable = Join-Path $release "ADB-Player-Portable-v$version-x64.zip"

foreach ($url in @($InstallerBaseUrl, $PortableBaseUrl)) {
    [Uri]$parsed = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$parsed) -or $parsed.Scheme -ne "https") {
        throw "Yayın taban adresleri HTTPS olmalıdır: $url"
    }
}
if (-not (Test-Path -LiteralPath $installer)) { throw "Kurulum EXE bulunamadı: $installer" }
if (-not (Test-Path -LiteralPath $portable)) { throw "Portable ZIP bulunamadı: $portable" }

$installerName = Split-Path -Leaf $installer
$portableName = Split-Path -Leaf $portable
$manifest = [ordered]@{
    schema = 1
    version = $version
    channel = $Channel
    publishedUtc = [DateTimeOffset]::UtcNow.ToString("o")
    minimumWindowsBuild = 19041
    installerUrl = $InstallerBaseUrl.TrimEnd('/') + '/' + $installerName
    installerSha256 = Get-Sha256 -Path $installer
    portableUrl = $PortableBaseUrl.TrimEnd('/') + '/' + $portableName
    portableSha256 = Get-Sha256 -Path $portable
    releaseNotes = $ReleaseNotes
    mandatory = [bool]$Mandatory
}

New-Item -ItemType Directory -Path $release -Force | Out-Null
$path = Join-Path $release "update-manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding UTF8
Write-Host "Güncelleme manifesti hazır: $path" -ForegroundColor Green
