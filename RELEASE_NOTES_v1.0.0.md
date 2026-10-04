# ADB Player V1.0.0

ADB Player V1.0 is the first public Windows release.

## Highlights

- Midnight Cinema media-center interface for Windows 10/11 x64.
- libmpv playback with modern transport controls, chapters, speed controls, video profiles and subtitle controls.
- Local files, supported remote links, WebDAV/NAS and cloud-source workflows.
- Optional Telegram integration.
- User-configured add-ons and external source/indexer connections.
- ADB Cloud account with device registration, watch progress, collections, playback queue and portable preference synchronization.
- Seamless profile restore for supported add-on configurations on a new device.
- Manual external-audio and subtitle synchronization only when the user requests it.
- Original-audio selection, multi-window subtitle analysis, fixed/drift/FPS/piecewise models and Safe Apply Gate protection.
- Turkish and English UI.
- Portable ZIP and Windows installer packaging.

## Safety and content-source model

ADB Player does not include preconfigured media catalogs, torrent indexers, copyrighted media or third-party account credentials. Remote sources and add-ons are user-configured.

Synchronization engines prefer a safe refusal over applying a result that does not pass confidence/residual/overlap checks.

## Known limitations

- Windows x64 only.
- Some online integrations require service/application credentials in official builds or developer configuration in source builds.
- ADB Cloud features require the hosted ADB Cloud service to be available.
- Synchronization may intentionally return an uncertain/no-match result for difficult material.
- Code-signing/SmartScreen reputation depends on the certificate used for the published build.

## Release artifacts

- `ADB-Player-Setup-v1.0.0.0-x64.exe`
- `ADB-Player-Portable-v1.0.0.0-x64.zip`
- `SHA256SUMS.txt`
- source snapshot/tag `v1.0.0`
