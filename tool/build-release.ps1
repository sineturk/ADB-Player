param(
    [string]$InstallerBaseUrl = "",
    [string]$PortableBaseUrl = "",
    [ValidateSet("stable", "preview")][string]$Channel = "stable",
    [string]$ReleaseNotes = "ADB Player V1.0.1 fullscreen, kontrol paneli, HDR ve güvenli güncelleme hotfix.",
    [switch]$SkipInstaller
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$version = Get-ReleaseVersion
$release = Join-Path $root "artifacts\release"
$installerDir = Join-Path $root "artifacts\installer"
New-Item -ItemType Directory -Path $release -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

& "$PSScriptRoot\publish-portable.ps1"
if (-not $SkipInstaller) {
    & "$PSScriptRoot\build-installer.ps1" -SkipPortable
}

$manifestPath = Join-Path $release "update-manifest.json"
$templateManifestPath = Join-Path $release "update-manifest.template.json"
$sumFile = Join-Path $release "SHA256SUMS.txt"

# Release builds must not inherit stale manifest/checksum files from an earlier
# Candidate/Public run. Public mode publishes only the real update manifest;
# candidate mode publishes only the template.
foreach ($stale in @($manifestPath, $templateManifestPath, $sumFile)) {
    if (Test-Path -LiteralPath $stale) {
        Remove-Item -LiteralPath $stale -Force
    }
}

$hasPublicUrls =
    -not [string]::IsNullOrWhiteSpace($InstallerBaseUrl) -and
    -not [string]::IsNullOrWhiteSpace($PortableBaseUrl) -and
    -not $SkipInstaller

if ($hasPublicUrls) {
    & "$PSScriptRoot\create-update-manifest.ps1" `
        -InstallerBaseUrl $InstallerBaseUrl `
        -PortableBaseUrl $PortableBaseUrl `
        -Channel $Channel `
        -ReleaseNotes $ReleaseNotes
} else {
    Copy-Item (Join-Path $root "packaging\manifests\update-manifest.template.json") $templateManifestPath -Force
    Write-Host "Yayın HTTPS adresleri verilmediği için dağıtıma hazır manifest üretilmedi." -ForegroundColor Yellow
}

$portableName = "ADB-Player-Portable-v$version-x64.zip"
$installerName = "ADB-Player-Setup-v$version-x64.exe"
$expectedArtifacts = New-Object System.Collections.Generic.List[string]
$expectedArtifacts.Add((Join-Path $release $portableName))
if (-not $SkipInstaller) {
    $expectedArtifacts.Add((Join-Path $installerDir $installerName))
}
$expectedArtifacts.Add($(if ($hasPublicUrls) { $manifestPath } else { $templateManifestPath }))

$lines = foreach ($artifact in $expectedArtifacts) {
    if (-not (Test-Path -LiteralPath $artifact)) {
        throw "SHA256SUMS için beklenen yayın artefaktı bulunamadı: $artifact"
    }
    "$(Get-Sha256 -Path $artifact)  $(Split-Path -Leaf $artifact)"
}
$lines | Set-Content -LiteralPath $sumFile -Encoding ASCII

& "$PSScriptRoot\verify-release.ps1" -AllowMissingInstaller:$SkipInstaller
Write-Host "ADB Player v$version yayın çıktıları hazır: $release" -ForegroundColor Green
