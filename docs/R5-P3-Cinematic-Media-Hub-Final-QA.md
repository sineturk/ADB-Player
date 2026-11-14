# ADB Player Windows v1.0.2 — R5 Cinematic Media Hub

**Scope:** P0 Home Layout, P1 Smart Media Cards, P2 Sources & Connections, P3 Glass & Motion.  
**Repo:** private `sineturk/ADB-Player-V1`.  
**Base:** P2 commit `5ee2793abb86cae2490e54bd06c1cd679a7ccf99`.  
**Branch:** `feature/v1.0.2-r5-cinematic-home-p3`.

## R5 implementation

- P0: data-driven featured resume hero, wide resume rail, quick actions, bounded sources.
- P1: source-safe episode grouping and selector; progress, remaining time; guarded poster/backdrop metadata/cache.
- P2: explicit cloud provider labeling, stable account fingerprint for duplicate display names, privacy-safe links, searchable Sources workspace without mutating persistence.
- P3: scoped navy/cyan glass styles; keyboard focus outlines; short hover animation disabled under Windows reduced-motion/high-contrast; adaptive Home and Sources columns; search reset.

No player HWND, subtitle/audio timing, Mini Player or public release code modified by P3.

## Consolidated Windows CI

Workflow: `.github/workflows/adb-player-r3-release-validation.yml`. It runs:

1. Existing R4.1.1 player HUD/shortcut regression source checks.
2. `tool/verify-r5-home-p0.ps1`: P0–P3 XAML parse, localization keys, source identity, privacy, media-card bindings, focus/motion and responsive hooks.
3. .NET 10 NuGet restore and Release x64 build.
4. Private libmpv runtime verification and executable SHA-256 receipt.
5. Private `ADB-Player-R5-P0-P3-CinematicMediaHub-Release-x64-test` Actions artifact.

**Limitation:** CI cannot replace interactive WPF layout, signed URL playback or Mini Player mouse-hover verification on an actual Windows desktop.

## Windows interactive acceptance checklist

- [ ] 1500×860 and 1180×600 windows: hero remains readable; quick actions stay accessible.
- [ ] 1100×700 and ~900×650: Home source panels stack instead of clipping; Sources manager stack maintains text/controls; no horizontal scroll traps.
- [ ] 125% and 150% DPI: headers, controls and progress bars remain readable.
- [ ] Windows animations OFF / High contrast ON: no scaling; focus ring remains visible; default theme not forced over OS contrast.
- [ ] Mouse hover and keyboard Tab: cyan frames and mouse enlargement behave without reflow; inactive buttons remain correctly disabled.
- [ ] Same TV series multiple seasons/episodes: one series card, distinct episode choices, each resumes original source and position.
- [ ] Two releases for one episode: separate choices; neither source is merged/deleted.
- [ ] Ambiguous title, release mismatch, wrong year/media type, low-confidence artwork: safe image fallback instead of incorrect media poster.
- [ ] No signed URL query, token, WebDAV username/password or full secret-bearing path appears in Home labels or tooltips.
- [ ] Identically named pCloud accounts: separate stable short labels; correct selected account opens.
- [ ] rclone unavailable: offline wording reflects runtime status only, not per-account authentication.
- [ ] Source search, reset, open/remove/disconnect/connect and WebDAV browser preserve original records.
- [ ] Mini Player hover, paired HUD, subtitles/audio synchronization, full screen and exit return remain unchanged.
- [ ] No unrelated files changed in public `sineturk/ADB-Player`.

## Release discipline

This is a **private test build**, not a published Windows release. Do not merge/publish until Windows visual and playback acceptance are confirmed.
