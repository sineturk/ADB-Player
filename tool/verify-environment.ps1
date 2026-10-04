. "$PSScriptRoot\common.ps1"

if ($env:OS -ne "Windows_NT") {
    throw "Build W7 yalnız Windows x64 üzerinde derlenir."
}

$dotnet = Get-DotNetExecutable
$sdks = @(& $dotnet --list-sdks)
if (-not ($sdks | Select-String -Pattern '^10\.')) {
    throw ".NET 10 SDK bulunamadı. Yüklü SDK'lar:`n$($sdks -join "`n")"
}

$architecture = if ($env:PROCESSOR_ARCHITEW6432) {
    $env:PROCESSOR_ARCHITEW6432
} else {
    $env:PROCESSOR_ARCHITECTURE
}

if ($architecture -notin @("AMD64", "x86_64")) {
    throw "Build W7 x64 Windows gerektirir. Bulunan mimari: $architecture"
}

$config = Get-NuGetConfigPath
$sources = @(& $dotnet nuget list source --configfile $config)
if ($LASTEXITCODE -ne 0 -or -not ($sources | Select-String -SimpleMatch "nuget.org")) {
    throw "Proje NuGet kaynağı doğrulanamadı: $config"
}

Write-Host "Windows: $([Environment]::OSVersion.VersionString)"
Write-Host "Mimari: $architecture"
Write-Host ".NET SDK'lar:"
$sdks | ForEach-Object { Write-Host "  $_" }
Write-Host "NuGet yapılandırması: $config"
Write-Host "Ortam doğrulaması başarılı." -ForegroundColor Green
