# Changelog

## 1.0.1

Focused Windows hotfix.

### Fixed

- True fullscreen no longer leaves the player page inset or blue viewport border around the video.
- Fullscreen playback controls reappear on pointer movement over the native libmpv surface and auto-hide after inactivity.
- HDR output negotiation now sends the intended colorspace hint while allowing mpv/libplacebo to negotiate the Windows D3D11 swap-chain safely.
- The in-app updater now defaults to the official stable GitHub feed, shows the full installed patch version, displays update availability in the navigation rail and verifies installer/portable downloads with SHA-256.
- Telegram external-audio selection now accepts supported audio tracks that Telegram exposes as generic documents, including E-AC-3 and DTS-family files.
- Public Google Drive media links are validated before playback; ordinary public files remain supported, while browser/account-specific confirmation flows may require the connected Drive account path.
- Gofile public shares use the official Premium account-token API path. Users can store their own token securely with Windows DPAPI to browse folders and discover video/audio/subtitle/archive items; direct file URLs remain a separate fallback.
- Settings is simplified: optional Premium service credentials are collapsed under one optional-services section while normal-use settings remain visible.
- AkiraBox share-file URLs are recognized through the documented public file-status API, with byte-range/redirect probing first and an optional user-supplied API endpoint + API Key / Token fallback stored with Windows DPAPI.

## 1.0.0

First public ADB Player release for Windows x64.

### Added

- Midnight Cinema media-center and player UI.
- Local and supported remote media playback through libmpv.
- WebDAV/NAS, cloud-source and optional Telegram workflows.
- User-configured add-ons and external source/indexer connections.
- ADB Cloud account, watch-progress, collections, queue and portable preference synchronization.
- Seamless restore of supported add-on profiles on a new device.
- Turkish and English localization.
- Manual external-audio synchronization with confidence/safety validation.
- Manual subtitle synchronization with original-audio resolution, fixed/drift/FPS/piecewise timeline models and Safe Apply Gate.
- Portable ZIP and Windows installer distribution.
