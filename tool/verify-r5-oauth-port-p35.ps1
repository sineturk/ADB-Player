param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Read([string]$file) {
  $p=Join-Path $root $file
  if (!(Test-Path -LiteralPath $p)) {throw "Missing file: $file"}
  return [IO.File]::ReadAllText($p)
}
function Assert([string]$text,[string]$needle) {
  if (!$text.Contains($needle)) {throw "P3.5 source guard missing: $needle"}
  Write-Host "[PASS] $needle" -ForegroundColor Green
}
$cloud=Read 'src\AltyaziDB.Player.Cloud\RcloneCloudService.cs'
$sources=Read 'src\AltyaziDB.Player.App\ViewModels\SourcePanelViewModel.cs'
$diag=Read 'tool\diagnose-rclone-oauth-port.ps1'
Assert $cloud 'private const int LocalOAuthPort = 53682;'
Assert $cloud 'VerifyLocalOAuthPortAvailable();'
Assert $cloud 'new TcpListener(IPAddress.Loopback, LocalOAuthPort)'
Assert $cloud 'listener.Stop();'
Assert $cloud 'SocketError.AddressAlreadyInUse'
Assert $cloud 'SocketError.AccessDenied'
Assert $cloud 'oauth_local_port_occupied'
Assert $cloud 'oauth_local_port_reserved'
Assert $sources 'oauth_port_reserved'
Assert $sources 'oauth_port_busy'
Assert $sources 'oauth_port_server'
Assert $diag 'Get-NetTCPConnection -LocalPort $Port -State Listen'
Assert $diag 'netsh interface ipv4 show excludedportrange protocol=tcp'
Assert $diag '[System.Net.Sockets.TcpListener]::new('
Assert $diag '$probe.Stop()'
foreach($forbidden in @('Restart-Service','Stop-Service','Stop-Process',
  'Kill(','set dynamicport','add excludedportrange','Remove-Item','config delete')) {
  if ($diag.Contains($forbidden)) {throw "Diagnostics must be non-destructive: $forbidden"}
}
foreach($path in @('tool\diagnose-rclone-oauth-port.ps1',
  'tool\verify-r5-oauth-port-p35.ps1')) {
  $tokens=$null
  $errors=$null
  [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root $path),[ref]$tokens,[ref]$errors) | Out-Null
  if ($errors.Count -gt 0) {throw "Invalid PowerShell syntax in $path"}
}
Write-Host '[PASS] R5/P3.5 read-only OAuth port diagnosis and safeguards' -ForegroundColor Cyan
