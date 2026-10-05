param()

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$version = Get-ReleaseVersion

function Need([string]$Relative, [string]$Needle = "") {
    $path = Join-Path $root $Relative
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing public source file: $Relative" }
    if (-not [string]::IsNullOrWhiteSpace($Needle)) {
        $text = [IO.File]::ReadAllText($path)
        if (-not $text.Contains($Needle)) { throw "Missing public source marker: $Relative :: $Needle" }
    }
}

if ($version -ne "1.0.1.0") { throw "Expected public source version 1.0.1.0; got $version" }

Need "README.md" "# ADB Player"
Need "README_TR.md" "# ADB Player"
Need "CHANGELOG.md" "## 1.0.1"
Need "PRIVACY.md"
Need "SECURITY.md"
Need "LICENSE"
Need "THIRD_PARTY_NOTICES.md"
Need "RELEASE_NOTES_v1.0.1.md"
Need "Directory.Build.props" "<Product>ADB Player</Product>"

foreach ($forbidden in @(
    ".agents", "archive", "research", "cloud", "server",
    "docs\continuity", "docs\release", "docs\history", "tool\sync-lab",
    "tool\export-public-snapshot.ps1",
    "tool\verify-public-release-gate.ps1",
    "packaging\public-snapshot-policy.json"
)) {
    if (Test-Path -LiteralPath (Join-Path $root $forbidden)) {
        throw "Forbidden internal path in public source: $forbidden"
    }
}

foreach ($legacyRoot in @(
    "INIT_GITHUB_REPO.ps1",
    "PACKAGE_MANIFEST.json",
    "SECRET_SCAN_REPORT.md",
    "SHA256SUMS.txt",
    "SOURCE_PROVENANCE.json",
    "TEST_KONTROL_LISTESI.md",
    "VERIFY_REPO.ps1"
)) {
    if (Test-Path -LiteralPath (Join-Path $root $legacyRoot)) {
        throw "Forbidden legacy/private root file in public source: $legacyRoot"
    }
}

if (Get-ChildItem -LiteralPath $root -Filter "BUILD_*" -File -ErrorAction SilentlyContinue) {
    throw "Legacy BUILD_* files must not exist in public source."
}

$docsPath = Join-Path $root "docs"
if ((Test-Path -LiteralPath $docsPath) -and
    (Get-ChildItem -LiteralPath $docsPath -Filter "REV*.md" -File -ErrorAction SilentlyContinue)) {
    throw "Legacy docs/REV*.md files must not exist in public source."
}

foreach ($verifier in @(
    "verify-v1.0.1-hotfix.ps1",
    "verify-v1.0.1-updater-ready.ps1",
    "verify-v1.0.1-telegram-audio.ps1",
    "verify-v1.0.1-google-drive.ps1",
    "verify-v1.0.1-gofile.ps1",
    "verify-v1.0.1-akirabox.ps1",
    "verify-v1.0.1-settings-freeze.ps1",
    "verify-player-ux-final-polish.ps1",
    "verify-subtitle-sync-safe-apply.ps1",
    "verify-subtitle-sync-v2.ps1"
)) {
    $path = Join-Path $PSScriptRoot $verifier
    if (Test-Path -LiteralPath $path) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $path
        if ($LASTEXITCODE -ne 0) { throw "Public source verifier failed: $verifier" }
    }
}

Write-Host "ADB Player V1.0 public source verification passed." -ForegroundColor Green
