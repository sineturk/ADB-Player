param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common.ps1"
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)

$packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$tdlibVersion = Get-CentralPackageVersion -PackageId "tdlib.native.win-x64"
$tdlibPackagePath = Join-Path $packagesRoot "tdlib.native.win-x64\$tdlibVersion\runtimes\win-x64\native\tdjson.dll"
$components = @(
    @{ Name = "Telegram TDLib"; Candidates = @(
        (Join-Path $ProjectRoot "native\telegram\tdjson.dll"),
        $tdlibPackagePath
    ) },
    @{ Name = "Bulut rclone"; Candidates = @((Join-Path $ProjectRoot "native\rclone\rclone.exe")) }
)

Write-Host "Build W7 native bileşen kontrolü" -ForegroundColor Cyan
foreach ($component in $components) {
    $componentPath = $component.Candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($componentPath) {
        $file = Get-Item -LiteralPath $componentPath
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        Write-Host "[Hazır] $($component.Name): $($file.FullName)" -ForegroundColor Green
        Write-Host "  Boyut: $($file.Length) byte"
        Write-Host "  SHA-256: $hash"
        Write-Host "  İmza: $($signature.Status)" -ForegroundColor DarkGray
    } else {
        Write-Host "[Eksik] $($component.Name): $($component.Candidates -join ' | ')" -ForegroundColor Yellow
    }
}
Write-Host "Telegram ve MonoTorrent NuGet geri yüklemesiyle; bulut motoru doğrulanmış satıcı betiğiyle hazırlanır." -ForegroundColor DarkGray
