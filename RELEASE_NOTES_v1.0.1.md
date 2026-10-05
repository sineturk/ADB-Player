# ADB Player V1.0.1

ADB Player V1.0.1 is a focused Windows hotfix for fullscreen presentation, fullscreen controls, HDR output negotiation and the production-ready in-app update flow.

## Fixed

- True fullscreen now removes the player-page inset and decorative blue viewport border, so the video surface reaches the monitor edges.
- The fullscreen transport controls now reappear when the pointer moves over the native libmpv video surface.
- Fullscreen controls are rendered in a separate WPF Popup HWND to avoid the WPF/HwndHost airspace limitation.
- The fullscreen transport auto-hides after inactivity and includes seek, play/pause, short seek, volume, subtitle attach and fullscreen exit controls.
- Windowed layout margins, border thickness and corner radius are restored after leaving fullscreen.
- HDR output mode now sends the intended `target-colorspace-hint` value instead of mapping every enabled state to `auto`.
- Explicit HDR display mode requests PQ transfer while leaving the D3D11 swap-chain colorspace/format to mpv/libplacebo and the current Windows display state.
- Auto and explicit HDR modes keep D3D11 colorspace and surface format on `auto`; this avoids forcing PQ onto an SDR Windows desktop.
- SDR display mode explicitly targets BT.1886 / SDR output.
- The Update page now uses the official stable GitHub release feed by default, including upgrades from older settings where the manifest URL was empty.
- Installed builds download and SHA-256 verify the Windows installer before launching it; portable builds download and verify the portable ZIP instead.
- The installed version is shown as `V1.0.1`, update availability is surfaced in the sidebar, and the unused Preview channel is hidden for this release.
- Telegram audio files sent as generic documents (including E-AC-3/DTS/TrueHD-style tracks) are now classified as audio and can be attached as external sound.
- Public Google Drive links are validated before playback; ordinary public files remain supported, while Drive files that require browser/account-specific confirmation tokens may still need the connected Google Drive account path.
- Gofile direct `file-*.gofile.io/download/web/...` URLs are playable without API credentials. `gofile.io/d/...` share-folder browsing uses the official Premium account-token API path; the token is optional, stored locally with Windows DPAPI, and is not synced to ADB Cloud.
- Settings is simplified for normal users: Gofile and AkiraBox are grouped under a collapsed optional-services section, Premium requirements are explained inline, and advanced cloud OAuth credentials remain collapsed.
- AkiraBox file links are now recognized through the documented public status API. ADB Player first probes the share URL for byte-range media or a media redirect; if public playback is insufficient, users can provide a custom AkiraBox API endpoint and API Key / Token in Settings for an authenticated fallback. Credentials stay local under Windows DPAPI.

## HDR requirements

For the explicit **HDR display** mode, Windows HDR must already be enabled for the target monitor and the GPU/display path must support HDR. ADB Player does not change the Windows system HDR toggle.

The default **Auto** mode remains recommended for general use.

## Validation checklist
- Gofile direct-file playback was live-tested successfully with a `file-*.gofile.io/download/web/...` URL. Share-folder `gofile.io/d/...` browsing remains Premium-token dependent by design.

- Enter fullscreen on a 1920×1080 or larger display and verify there is no blue outer frame or application inset.
- Move the mouse over the native video area; the bottom transport must appear and hide again after about 3 seconds.
- Seek, play/pause, volume and fullscreen exit must work from the popup transport.
- Exit fullscreen and verify the normal player layout is restored exactly.
- On an HDR-capable Windows display with Windows HDR enabled, test an HDR10/PQ source in Auto and HDR display modes.
- Verify SDR content still renders normally in Auto and SDR display modes.
- Check the Update page on an installed build and a portable build; confirm the official stable feed is selected, the current version is rendered correctly and no Preview option is exposed.

## Release artifacts

- `ADB-Player-Setup-v1.0.1.0-x64.exe`
- `ADB-Player-Portable-v1.0.1.0-x64.zip`
- `SHA256SUMS.txt`
- source snapshot/tag `v1.0.1`
