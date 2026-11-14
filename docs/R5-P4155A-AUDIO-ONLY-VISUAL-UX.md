# R5/P4.15.5a — Audio-Only Visual UX (Windows)

Base: private P4.15.5 local audio playback, commit `5988049ded4f829620bc03209f030d931428e101`. Private preview only, not an installer/public release.

## Behavior

During active **audio-only** playback (libmpv reports audio but no video track), the gray native MPV window is hidden and a cinematic dark-blue WPF canvas takes its place. The native `MpvVideoHost` HWND remains initialized, and returns as soon as a video track is present. Media Center, transport controls, seek, audio device, subtitle, player mini/full-screen, and Telegram flows are not replaced.

- **Cover precedence:** embedded art from local MP3/FLAC/M4A/WAV metadata when available; else small adjacent `cover.jpg`, `folder.jpg`, `front.jpg`, `album.jpg`, PNG equivalents; else bundled ADB Player application mark.
- **Visible data:** embedded song title/artist/album when present; decoded MPV audio codec, channels, sample rate and bitrate *only when provided*. Missing values are omitted. The badge can show E-AC-3, FLAC, MP3, PCM, etc. This is not server transcoding.
- **Ambient motion:** four subtle WPF bars are decorative, NOT sample-derived spectral measurements. They animate while playing, freeze on pause or buffering, stop while hidden and respect Windows system motion preference.
- **No artwork network requests.** Metadata reading only for local standalone audio files, off the UI thread, cancellable on track changes; images capped at 4 MB and decoded to 440 pixels before reaching the UI. Invalid tag/cover data must never break playback.
- Uses pinned MIT-licensed `TagLibSharp2` 0.6.0 for read-only local metadata, without modifications to user audio files. The libmpv decoder remains the only playback engine.

## Safety

WPF does not paint over a visible Win32 HwndHost. `AudioOnlySurface` replaces **visibility of the HWND** while audio-only, rather than relying on a Z-index overlay. The host is restored for subsequent video. The control deck is a separate element and remains functional. Video/Telegram 7z Store engines and native library versions are untouched.

## Offline checks

- `tool/audio-playback-smoke/Program.cs` checks derived audio-only conditions, video priority, idle/media-center hidden state, E-AC-3 fallback and truthful optional metadata.
- `tool/verify-r5-p4155a-audio-only-visual.ps1` checks WPF XML, HwndHost visibility logic, logo/cover fallback, cancellable async metadata, resources, native codec props, original controls.
- Full P1–P5 Telegram ZIP/7z Store Range regressions and Windows x64 Release CI.
- CI green means *source/build* PASS, not a real Windows audio test.

## Live Windows acceptance (not yet performed)

1. MP3/FLAC/WAV with and without embedded cover; local album `cover.jpg` fallback and ADB logo.
2. MP3/FLAC/WAV/EAC3: play/pause, seek, volume, source switch, playlist next/previous, show real channels/rate only when available.
3. Switch audio → MKV video → audio; video must show normally with no lingering dark canvas or MPV HWND re-initialization.
4. Test 100/125/150% DPI, maximized, narrow, mini player and fullscreen. Alt bar stays usable.
5. Malformed image/tags, corrupted image, rapid file switching: no crash or previous album cover appearing on new track.
6. Playback progress, ADB Cloud signed out and Telegram session persistence remain intact.
7. The earlier unresolved Telegram performance and multi-account legacy data privacy gates remain separate and must pass before public release.

### Deployment

Deliver exactly one Windows PowerShell update script pinned to the final branch commit. It performs a new sparse private repo checkout, offline tests, x64 build, verifies native DLL and EXE SHA-256, and stages the portable test app via `robocopy /E /XD data`, preserving its existing Telegram/session/cache profile. No force close, no destructive reset of pre-existing folders, no public release or Android changes.
