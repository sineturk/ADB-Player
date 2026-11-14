# ADB Player Windows v1.0.2 — R5/P3.5 OAuth Port Diagnostics

Private branch: `feature/v1.0.2-r5-p35-oauth-port-diagnostics`  
Base: R5/P3.4 `d2286c44ff0bef382f2b999a6b0394ae7f3f1f24`

## Confirmed observations

- Existing Google Drive account can list files: the existing saved rclone remote works.
- Adding a **new** Google Drive account fails before the browser opens.
- R5/P3.4 reported a local OAuth port conflict. A new browser launcher cannot fix a failed local OAuth listener bind.
- rclone uses the registered loopback redirect at `127.0.0.1:53682` for providers such as Google Drive. Do **not** change that port arbitrarily: the registered OAuth redirect URI is part of the provider integration.
- Windows/Hyper-V/WSL/Docker may exclude port ranges without active listeners; distinguish that case from a separate process actually listening.

## Safe changes in P3.5

1. A short-lived `TcpListener` probe immediately before new OAuth authorization tests whether 127.0.0.1:53682 is bindable. It immediately stops and releases the port.
2. Separate on-screen diagnostics for **in use**, **access denied / Windows-reserved**, and other auth webserver failures. No user identity, browser URL, OAuth state, token or secret is logged.
3. New read-only `tool/diagnose-rclone-oauth-port.ps1` script reports: active TCP listener PID/process name; IPv4 excluded port ranges including 53682; and a temporary local bind test. It never changes network settings, stops services, kills processes or disconnects remotes.
4. Existing OAuth launch UI, secure account storage, verify-before-save, protected credentials and all saved cloud accounts are unchanged.

## User-run diagnostics

In PowerShell on the same Windows machine (ADB Player **closed or idle**):

```powershell
Get-NetTCPConnection -LocalPort 53682 -State Listen -ErrorAction SilentlyContinue |
  Select-Object LocalAddress,LocalPort,OwningProcess

netsh interface ipv4 show excludedportrange protocol=tcp
```

For a richer report, run from the cloned repository:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tool\diagnose-rclone-oauth-port.ps1
```

Never send full rclone logs, client secrets, local OAuth URLs or token-bearing config files. Only send the short diagnostic report.

## Decision tree

- Listener present: identify the process by PID; consider closing that **known and safe** app normally. Do not kill unrelated processes.
- No listener, excluded range contains port 53682, or bind reports AccessDenied: likely Windows network port reservation; inspect virtualization components and Host Network Service configuration before making any host-level changes.
- Listener absent and bind successful: retry new OAuth; if the original error persists, capture the **sanitized** category and rerun the diagnostic immediately.
- Do **not** automatically change Windows global dynamic TCP port ranges, restart HNS, remove Hyper-V/WSL/BlueStacks, alter OAuth redirect URIs or remove working saved rclone remotes.

Reference: https://rclone.org/faq/ and https://github.com/rclone/rclone/issues/9083

## Validation

Private GitHub Actions runs existing R4/P0–P3.4 source checks, the P3.5 guard, .NET 10 x64 Release build, libmpv presence and SHA-256. Live testing of localhost port ownership and Google OAuth can only be completed on the user's Windows desktop.

No Premium/Membership, Neon production schema or public release changes.
