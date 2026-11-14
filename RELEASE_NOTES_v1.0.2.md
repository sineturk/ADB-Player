# ADB Player v1.0.2 — Windows

ADB Player v1.0.2 brings a refreshed Windows media center, Telegram browsing, unified device-local playback history and standalone audio playback.

## Highlights

- **Cinematic Media Hub:** redesigned Home, Discover, Library, Lists and source views with responsive layout, compact content previews and Turkish/English localized metadata.
- **Telegram Desktop-inspired browser:** simplified folders/chats/topics, searchable media and a dedicated Telegram Hub for starred chats, opened media, unfinished watch progress and files seen in the current session.
- **Telegram login and resume artwork:** the temporary phone authorization state no longer looks like an invalid number warning; Home can safely enrich verified Telegram resumes with trusted artwork and shows a bundled brand fallback when none is available.
- **Telegram multipart:** restored ZIP / 7z Store grouped-file and HTTP Range playback workflows, including direct seeking of verified uncompressed archive entries. Part availability and archive completeness are tracked separately from "playable" state.
- **Unified Continue Watching:** Home shows unfinished local, directly reopenable HTTP/HTTPS and verified-account Telegram entries together; ADB Cloud login is not required for device-local resume.
- **Standalone audio playback:** MP3, FLAC, WAV and additional compatible audio files can be opened directly; local music playlists do not automatically mix with video episodes.
- **Audio-only visual experience:** cinematic dark-blue screen, album-cover or bundled-logo fallback, visible codec/quality values when the decoder supplies them, and gentle optional ambient animation.
- **Player & connections:** refined player controls, localized status messaging, configurable cloud/source pages, manual audio/subtitle sync controls and torrent/qBittorrent status/handoff UX.

## Privacy and source behavior

- Only media the user chooses or connects is playable. ADB Player provides no built-in copyrighted catalog, tracker list or service credentials.
- Telegram sessions are held in application-local data and are not ADB Cloud credentials. A verified TDLib account ID scopes current Telegram playback history; legacy pre-v1.0.2 resumes without account provenance receive a one-time first-verified-account migration **which must be checked before stable release**.
- Local audio metadata and album artwork are read on device. No remote album-art search is performed.
- Cloud/provider OAuth connections, Telegram credentials, AltyazıDB API configuration and media-provider account settings require the appropriate official app configuration or user credentials.

## Notes and compatibility

- The existing Windows 10/11 x64 installer identity is preserved for upgrades.
- Live Telegram and cloud-source functions require the appropriate official app configuration; application credentials are not shipped in public source code.
- Local audio artwork is optional and may be absent or unreadable for some formats. The ADB Player logo is used as the fallback.
- The user controls their own media sources and remains responsible for access to external services and content.
- Automatic cross-device playback synchronization requires compatible ADB Cloud features; Telegram's local playback history itself does not require an ADB Cloud account.

## Windows distribution (after approval)

- `ADB-Player-Setup-v1.0.2.0-x64.exe`
- `ADB-Player-Portable-v1.0.2.0-x64.zip`
- `SHA256SUMS.txt`
- `update-manifest.json`


