param(
    [switch]$SkipPortable
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$version = Get-ReleaseVersion
$portable = Join-Path $root "artifacts\portable\ADB.Player.exe"
$script = Join-Path $root "packaging\installer\AltyaziDB.Player.iss"
$output = Join-Path $root "artifacts\installer\ADB-Player-Setup-v$version-x64.exe"
Set-Location $root

if (-not $SkipPortable -or -not (Test-Path -LiteralPath $portable)) {
    & "$PSScriptRoot\publish-portable.ps1" -SkipZip
}

$iscc = Get-InnoCompiler
if (-not $iscc) {
    throw "Inno Setup derleyicisi (ISCC.exe) bulunamadı. .\tool\install-inno-setup.ps1 betiğini çalıştırın veya Inno Setup 7 veya 6'yı kurun."
}

if (-not (Test-Path -LiteralPath $script)) { throw "Inno Setup betiği bulunamadı: $script" }
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
& $iscc "/DMyAppVersion=$version" $script
if ($LASTEXITCODE -ne 0) { throw "Kurulum EXE derlemesi başarısız oldu." }
if (-not (Test-Path -LiteralPath $output)) { throw "Kurulum EXE bulunamadı: $output" }

Write-Host "Kurulum EXE hazır: $output" -ForegroundColor Green
Write-Host "SHA-256: $(Get-Sha256 -Path $output)" -ForegroundColor DarkGray
