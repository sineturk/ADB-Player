param(
    [string]$PfxPath = "",
    [string]$CertificateThumbprint = "",
    [Parameter(Mandatory=$true)][string]$TimestampUrl,
    [string]$PasswordEnvironmentVariable = "ALTYAZIDB_SIGNING_PASSWORD",
    [string]$InstallerBaseUrl = "",
    [string]$PortableBaseUrl = "",
    [ValidateSet("stable", "preview")][string]$Channel = "stable",
    [string]$ReleaseNotes = "ADB Player V1.0.1 fullscreen, controls, HDR and secure updater hotfix."
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$version = Get-ReleaseVersion
$portableDir = Join-Path $root "artifacts\portable"
$appExe = Join-Path $portableDir "ADB.Player.exe"
$portableZip = Join-Path $root "artifacts\release\ADB-Player-Portable-v$version-x64.zip"
$installer = Join-Path $root "artifacts\installer\ADB-Player-Setup-v$version-x64.exe"
if (-not (Test-Path -LiteralPath $appExe)) { throw "Önce portable yayın üretin: $appExe" }

$signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "Windows SDK x64 signtool.exe bulunamadı." }
if ([string]::IsNullOrWhiteSpace($PfxPath) -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "-PfxPath veya -CertificateThumbprint parametrelerinden biri gereklidir."
}

function Sign-Target {
    param([Parameter(Mandatory=$true)][string]$Target)
    $arguments = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", $TimestampUrl)
    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX bulunamadı: $PfxPath" }
        $arguments += @("/f", (Resolve-Path -LiteralPath $PfxPath).Path)
        $password = [Environment]::GetEnvironmentVariable($PasswordEnvironmentVariable)
        if (-not [string]::IsNullOrWhiteSpace($password)) { $arguments += @("/p", $password) }
    } else {
        $arguments += @("/sha1", ($CertificateThumbprint -replace '\s',''))
    }
    $arguments += $Target
    & $signtool.FullName @arguments
    if ($LASTEXITCODE -ne 0) { throw "Kod imzalama başarısız: $Target" }
    & $signtool.FullName verify /pa /v $Target
    if ($LASTEXITCODE -ne 0) { throw "İmza doğrulaması başarısız: $Target" }
    Write-Host "İmzalandı: $Target" -ForegroundColor Green
}

# Önce uygulama imzalanır; ardından portable ZIP ve installer yeniden üretilir.
Sign-Target -Target $appExe
if (Test-Path -LiteralPath $portableZip) { Remove-Item -LiteralPath $portableZip -Force }
Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $portableZip -CompressionLevel Optimal
& "$PSScriptRoot\build-installer.ps1" -SkipPortable
Sign-Target -Target $installer
$sumFile = Join-Path $root "artifacts\release\SHA256SUMS.txt"
@(
    "$(Get-Sha256 -Path $portableZip)  $(Split-Path -Leaf $portableZip)",
    "$(Get-Sha256 -Path $installer)  $(Split-Path -Leaf $installer)"
) | Set-Content -LiteralPath $sumFile -Encoding ASCII
if ([string]::IsNullOrWhiteSpace($InstallerBaseUrl) -xor [string]::IsNullOrWhiteSpace($PortableBaseUrl)) {
    throw "Update manifest yenilemek icin InstallerBaseUrl ve PortableBaseUrl birlikte verilmelidir."
}
if (-not [string]::IsNullOrWhiteSpace($InstallerBaseUrl)) {
    & "$PSScriptRoot\create-update-manifest.ps1" `
        -InstallerBaseUrl $InstallerBaseUrl `
        -PortableBaseUrl $PortableBaseUrl `
        -Channel $Channel `
        -ReleaseNotes $ReleaseNotes
}
& "$PSScriptRoot\verify-release.ps1"
Write-Host "İmzalı yayın yeniden paketlendi; SHA-256 degerleri yenilendi." -ForegroundColor Green
