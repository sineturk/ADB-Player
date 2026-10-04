. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$dotnet = Get-DotNetExecutable
$binRoot = Join-Path $root "src\AltyaziDB.Player.App\bin"
Set-Location $root

& "$PSScriptRoot\verify-environment.ps1"

$exe = Get-LatestDebugExe -BinRoot $binRoot
$dll = Get-LatestDebugDll -BinRoot $binRoot

if (-not $exe -and -not $dll) {
    Write-Host "Debug çıktısı bulunamadı; uygulama derleniyor..." -ForegroundColor Yellow
    & "$PSScriptRoot\build-debug.ps1"
    $exe = Get-LatestDebugExe -BinRoot $binRoot
    $dll = Get-LatestDebugDll -BinRoot $binRoot
}

if ($exe) {
    Copy-LibMpvNativeRuntime -Destination $exe.Directory.FullName -Required | Out-Null
    Copy-W5OptionalNativeRuntime -Destination $exe.Directory.FullName | Out-Null
    Write-Host "Uygulama başlatılıyor: $($exe.FullName)" -ForegroundColor Cyan
    & $exe.FullName
    exit $LASTEXITCODE
}

if ($dll) {
    Copy-LibMpvNativeRuntime -Destination $dll.Directory.FullName -Required | Out-Null
    Copy-W5OptionalNativeRuntime -Destination $dll.Directory.FullName | Out-Null
    Write-Host "AppHost bulunamadı; uygulama dotnet ile başlatılıyor: $($dll.FullName)" -ForegroundColor Yellow
    & $dotnet $dll.FullName
    exit $LASTEXITCODE
}

throw "Debug uygulama çıktısı bulunamadı."
