. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$dotnet = Get-DotNetExecutable
Set-Location $root

& $dotnet clean "$root\AltyaziDB.Player.Windows.sln" -p:Platform=x64
Remove-BuildDirectories -Root $root

if (Test-Path "$root\artifacts") {
    Remove-Item "$root\artifacts" -Recurse -Force
}

Write-Host "Derleme çıktıları temizlendi." -ForegroundColor Green
