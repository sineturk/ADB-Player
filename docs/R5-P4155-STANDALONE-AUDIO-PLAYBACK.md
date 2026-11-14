# R5/P4.15.5 — Standalone Audio Playback (Windows preview)

Base: private P4.15.4 Unified Continue Watching commit `aec267ed52c7adf2bc823417bcdb68fe736b817e`.

## Why this is small

ADB Player already uses **libmpv** and its existing `loadfile` path. mpv supports common standalone audio formats; no separate MP3/FLAC/WAV decoder, server-side FFmpeg/transcoding, network API, or paid codec component is introduced.

The existing "Attach external audio" file picker already accepted `*.mp3`, `*.flac` and `*.wav`. This change is for **opening them as primary media**, not merely as a secondary soundtrack attached to a video.

## Implementation

- New Core `LocalAudioFilePolicy` recognizes MP3, FLAC, WAV, M4A, AAC, OGG, OPUS, WMA, MKA, AIF/AIFF, AC3, EAC3 and DTS, case-insensitively.
- Main `Dosya aç` picker now shows a combined **video and audio files** filter, a separate audio filter and an optional "all files" filter, localized TR/EN.
- Standard open, command-line/external file open and drag/drop paths use the same extension policy and send the original audio file directly to existing libmpv playback. Selected audio starts playback as a normal playlist item.
- With the existing "automatically add files in folder" setting, opening one audio file queues **other audio files in the same folder only**, in natural order. Opening one video still queues only video siblings (no mixing audio with TV episodes).
- Multiple manually selected media files or a dropped directory can contain both video and audio. Unsupported files such as ZIP or SRT are not admitted into the main playlist, even if the user selects an "all files" filter.
- If nothing supported is selected, a localized status is shown. No network upload or audio transcoding occurs.

## Deliberate scope limit

The existing SQLite **Movie/Series Library** only scans video. It has film/episode parsing and poster/metadata enrichment; blindly ingesting MP3 or FLAC would classify music as movies and potentially send media filenames to film metadata matching. A dedicated music library (artist, album, album art, library filter) should be a **separate phase**. This iteration supports direct audio playback/queue/history; it does not redesign film catalog, audio visualization, mobile Android or Telegram attachment streaming.

## Validation

- `tool/audio-playback-smoke`: all formats recognized (including uppercase extensions); video still passes; ZIP/SRT/EXE rejected; picker list unique.
- `tool/verify-r5-p4155-local-audio-playback.ps1`: picker, direct open, drag/drop, same-type folder queue, TR/EN resources, libmpv reuse, video library isolation.
- Full existing Telegram P1–P5, 7z Store Range and Windows Release x64 CI remain mandatory.
- **Real Windows manual test still required**: open MP3, FLAC and WAV individually; play/pause, volume, seek and next/previous; open a music folder; verify no video entries are mixed; ensure video still works; check Unicode titles, resume after restart, 100/125/150% display and no WPF crash. High-resolution FLAC and multichannel WAV require actual device/driver checks.
- Standalone audio may display the existing generic player background/video surface. Audio-specific artwork or waveform UI is not claimed in this phase.

No public releases or installed/Android app modifications in this private code branch.
