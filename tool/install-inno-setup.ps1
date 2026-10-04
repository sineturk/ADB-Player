$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") { throw "Inno Setup yalnız Windows üzerinde kurulur." }
$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
if (-not $winget) {
    throw "winget bulunamadı. Inno Setup 7 veya 6'yı resmi kurulum paketiyle yükleyip build-installer.ps1 betiğini yeniden çalıştırın."
}

& $winget.Source install --id JRSoftware.InnoSetup --exact --source winget --accept-package-agreements --accept-source-agreements
if ($LASTEXITCODE -ne 0) { throw "Inno Setup kurulumu tamamlanamadı." }
Write-Host "Inno Setup kurulumu tamamlandı. Yeni bir PowerShell penceresi açmanız gerekebilir." -ForegroundColor Green
