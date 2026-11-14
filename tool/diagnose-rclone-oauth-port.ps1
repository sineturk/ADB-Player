param(
    [ValidateRange(1025, 65535)]
    [int]$Port = 53682
)

$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host '=== ADB Player: rclone OAuth Port Diagnostics ===' -ForegroundColor Cyan
Write-Host 'Read-only diagnostics. No service, network setting, process or saved account is modified.'
Write-Host "Required port: 127.0.0.1:$Port"

# Display only process name/PID; never report command lines, auth URLs, settings
# or token-bearing environment variables.
$active = @()
try {
    $active = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop)
} catch {
    # The separate bind probe remains authoritative.
}
if ($active.Count -gt 0) {
    foreach ($entry in $active) {
        $processName = 'Unknown'
        try { $processName = (Get-Process -Id $entry.OwningProcess -ErrorAction Stop).ProcessName }
        catch { }
        Write-Host "LISTENER: $($entry.LocalAddress):$($entry.LocalPort) PID=$($entry.OwningProcess) Process=$processName" -ForegroundColor Yellow
    }
} else {
    Write-Host 'LISTENER: none seen'
}

# A reserved Windows range may block binding without a listening process.
$excluded = @()
try {
    $lines = @(& netsh interface ipv4 show excludedportrange protocol=tcp 2>&1)
    foreach ($line in $lines) {
        if ($line -match '^\s*(\d{2,5})\s+(\d{2,5})(?:\s+\*)?\s*$') {
            $lo = [int]$Matches[1]
            $hi = [int]$Matches[2]
            if ($lo -le $Port -and $Port -le $hi) { $excluded += "$lo-$hi" }
        }
    }
} catch {
    Write-Host 'EXCLUDED RANGE: query unavailable'
}
if ($excluded.Count -gt 0) {
    Write-Host "EXCLUDED RANGE: $($excluded -join ', ')" -ForegroundColor Yellow
} else {
    Write-Host 'EXCLUDED RANGE: none matching port (or netsh data unavailable)'
}

# Brief bind test immediately releases the local listener.
$probe = [System.Net.Sockets.TcpListener]::new(
    [System.Net.IPAddress]::Loopback, $Port)
$bound = $false
$socketError = ''
try {
    $probe.Start()
    $bound = $true
} catch [System.Net.Sockets.SocketException] {
    $socketError = [string]$_.Exception.SocketErrorCode
} catch {
    $socketError = $_.Exception.GetType().Name
} finally {
    $probe.Stop()
}
if ($bound) {
    Write-Host 'BIND TEST: AVAILABLE (port was released)' -ForegroundColor Green
} else {
    Write-Host "BIND TEST: BLOCKED ($socketError)" -ForegroundColor Red
}
if ($active.Count -gt 0) {
    Write-Host 'NEXT STEP: Close the app listening on the port normally, only if safe, then retry OAuth.' -ForegroundColor Yellow
} elseif ($excluded.Count -gt 0 -or $socketError -eq 'AccessDenied') {
    Write-Host 'NEXT STEP: Windows port reservation suspected; review virtualization/HNS. Do not delete saved accounts.' -ForegroundColor Yellow
} elseif (-not $bound) {
    Write-Host 'NEXT STEP: Binding blocked; check Windows security and network restrictions.' -ForegroundColor Yellow
} else {
    Write-Host 'NEXT STEP: Retry OAuth. If it fails, repeat diagnostics after the error.' -ForegroundColor Green
}
Write-Host ''
Write-Host 'Share only this short output, never OAuth links, Client Secrets or rclone.conf.'
