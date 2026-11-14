# ADB Player R5/P4.8 — Connections Guided Setup

**Private branch**: `feature/v1.0.2-r5-p48-connections-setup-ux`  
**Base**: `feature/v1.0.2-r5-p471-modal-fullwidth` — commit `7f7e40ba331b16ebd8a363420ace25e31802cca7`

## Problem reproduced from Windows screenshots

The previous Connections page looked like a three-pane administration console. It exposed category IDs, result limits, authorization acknowledgements and a search pane before any service was connected. "Required" above the API key field was ambiguous. The connection display name was mistaken for the server URL. A publicly reachable IPv4 Prowlarr server entered as `http://PUBLIC_IP:9696` was (correctly) rejected by the backend but the UI did not explain that until the user tried to connect.

## P4.8 UX changes

1. New connections use a focused setup layout: **service type → friendly name → server URL → API key → test and save**.
2. The first-use search workspace is hidden until at least one connection is saved. The editor can take the available width (bounded to 720px content).
3. New drafts default to friendly name `Prowlarr`, an **empty** URL and local HTTP disabled. There is no silent fallback to `localhost:9696` when the user leaves URL blank.
4. A specific shortcut fills `http://localhost:9696` and enables local HTTP **only if the user chooses the local Prowlarr button**.
5. Example URLs are shown for same-PC, private LAN and remote HTTPS. An explicit inline warning is shown as soon as a remote HTTP URL is entered.
6. The Prowlarr API Key location is written directly below the masked API key field: **Prowlarr → Settings → General → Security → API Key**. Username/password are not required by the Player.
7. Detailed service guides and category/result-limit settings are collapsed by default in separate expandable sections.
8. Test/Save, authorization acknowledgements, existing encrypted secret storage, result search and all service types retain their original commands and backend.

## Security contract — no bypass

- Remote public HTTP remains blocked in `ExternalConnectionService.ValidateRemoteUri`. The local HTTP checkbox does **not** authorize publicly routed HTTP.
- Plain HTTP remains explicitly opt-in for localhost and recognized local/private addresses only.
- For an external server, configure a trusted HTTPS reverse proxy (e.g. Caddy/Nginx) with server authentication, or establish an SSH tunnel and connect through `http://localhost:9696` on Windows. Do not embed the Prowlarr API key in the URL or publish it in screenshots.
- Connection Test never logs or displays the API key. The encrypted local secret-store behavior and existing redaction are unchanged.

## Windows acceptance checklist

- [ ] P4.1–P4.8 source checks and Release x64 GitHub Actions succeed.
- [ ] First launch with zero connections shows the focused editor; empty release search is hidden.
- [ ] The form initially shows Prowlarr as a display name but a blank server URL.
- [ ] Typing an external `http://public-address:9696` instantly shows an explanation and the HTTPS/SSH remedy, before pressing Test.
- [ ] HTTPS remote URL does not trigger the warning. Local `http://localhost:9696` or private LAN URL offers the local-HTTP consent toggle.
- [ ] The **Use local Prowlarr** shortcut fills localhost:9696 and enables local HTTP.
- [ ] Prowlarr API key field is clearly labelled; test/verify-save report meaningful errors, with secrets redacted.
- [ ] Advanced category values 2000/5000/5070 and result limit 100 persist unchanged when collapsed.
- [ ] Authorization acknowledgements and encrypted connection storage still gate saving.
- [ ] After first successful save, saved profiles and release search become available.
- [ ] English/Turkish toggling preserves typed values and guidance; form remains usable at 850px, 125% and 150% DPI.
- [ ] Sonarr, Radarr, Torznab setup and existing profiles remain functional.

**Release policy**: private CI test build only. Windows visual validation required, no public v1.0.1 changes.
