# ADB Player

ADB Player is a Windows 10/11 x64 media player and media-center application built with .NET 10, WPF and libmpv.

V1.0 focuses on one place for local playback, user-connected sources, subtitles, cross-device profile restore and safe manual synchronization when external audio or subtitle timing does not match a video.

## Highlights

- Local video playback with libmpv.
- Direct URL, WebDAV/NAS and supported cloud-source workflows.
- Optional Telegram integration.
- User-configured Stremio-compatible add-ons and external indexer/source connections.
- ADB Cloud account, watch progress, collections, playback queue and portable preference synchronization.
- Seamless restoration of supported add-on profiles on a new device.
- AltyazıDB subtitle search/integration in official builds.
- External audio synchronization and subtitle synchronization with confidence/safety gates.
- Turkish and English user interface.
- Installer and portable Windows x64 distributions.

ADB Player does **not** ship media catalogs, torrent indexers, copyrighted media, third-party account credentials or preconfigured content sources. Users are responsible for the services and content they connect.

## System requirements

- Windows 10 build 19041 or later, or Windows 11.
- x64 processor.
- Internet access only for features that use online services.
- GPU acceleration is optional; playback falls back according to the selected player profile.

## Official binaries

Public releases are distributed as:

- `ADB-Player-Setup-v1.0.0.0-x64.exe`
- `ADB-Player-Portable-v1.0.0.0-x64.zip`
- `SHA256SUMS.txt`

Verify release hashes before installation. When a signed build is provided, Windows signature verification should also succeed.

## Build from source

Developer requirements:

- Windows 10/11 x64
- .NET 10 SDK
- PowerShell
- Git

From the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\tool\build-debug.ps1
.\tool\run-debug.ps1
```

The build tooling generates secret-free fallback credential classes when credentials are not supplied. Optional integrations such as Telegram, provider OAuth, AltyazıDB API access and ADB Cloud require developer-provided configuration in `.env.local` or environment variables. Real credentials must never be committed.

For a public-source checkout, run the source verifier with:

```powershell
.\tool\verify-public-source.ps1
```

For release packaging:

```powershell
.\tool\publish-portable.ps1
.\tool\build-installer.ps1
.\tool\verify-release.ps1
```

Inno Setup 6 or 7 is required for the installer.

## Data locations

Installed mode stores user state under:

```text
%LOCALAPPDATA%\ADB\Player
```

Portable mode stores state in the application folder under `data\`.

ADB Player migrates supported legacy local state when possible.

## Public-source scope

The public V1.0 source snapshot contains the product source and build/release tooling required to inspect and build ADB Player. Internal research evidence, historical patch archives, backend deployment material and development handoff documents are intentionally excluded.

## Privacy, support, security and licensing

- [Privacy](PRIVACY.md)
- [Support](SUPPORT.md)
- [Security](SECURITY.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [GPL-3.0 license](LICENSE)

## Release

See [ADB Player V1.0 release notes](RELEASE_NOTES_v1.0.0.md).
