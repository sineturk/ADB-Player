param(
    [switch]$NoRun
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
Set-Location $root

& "$PSScriptRoot\install-dotnet10.ps1"
& "$PSScriptRoot\build-debug.ps1"

if (-not $NoRun) {
    & "$PSScriptRoot\run-debug.ps1"
}
