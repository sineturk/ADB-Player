param(
    [switch]$Force
)

. "$PSScriptRoot\common.ps1"

function Get-DotNet10Sdks {
    $dotnet = Get-DotNetExecutable
    return @(& $dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' })
}

$existing = Get-DotNet10Sdks
if ($existing.Count -gt 0 -and -not $Force) {
    Write-Host ".NET 10 SDK zaten kurulu:" -ForegroundColor Green
    $existing | ForEach-Object { Write-Host "  $_" }
    return
}

$winget = Get-Command winget -ErrorAction SilentlyContinue
if (-not $winget) {
    throw "WinGet bulunamadı. Microsoft'un resmi .NET 10 SDK x64 yükleyicisini kurun ve PowerShell'i yeniden açın."
}

Write-Host ".NET 10 SDK x64 kuruluyor..." -ForegroundColor Cyan
& winget install `
    --id Microsoft.DotNet.SDK.10 `
    --exact `
    --source winget `
    --accept-package-agreements `
    --accept-source-agreements

if ($LASTEXITCODE -ne 0) {
    throw "WinGet .NET 10 SDK kurulumunu tamamlayamadı. Çıkış kodu: $LASTEXITCODE"
}

$dotnetRoot = Join-Path $env:ProgramFiles "dotnet"
if (Test-Path -LiteralPath $dotnetRoot) {
    $env:PATH = "$dotnetRoot;$env:PATH"
}

$installed = Get-DotNet10Sdks
if ($installed.Count -eq 0) {
    throw ".NET 10 SDK kuruldu ancak bu PowerShell oturumunda görünmüyor. PowerShell'i kapatıp yeniden açın ve 'dotnet --list-sdks' çalıştırın."
}

Write-Host ".NET 10 SDK kurulumu tamamlandı:" -ForegroundColor Green
$installed | ForEach-Object { Write-Host "  $_" }
