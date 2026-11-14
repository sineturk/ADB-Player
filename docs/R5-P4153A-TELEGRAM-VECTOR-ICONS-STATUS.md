# R5/P4.15.3a — Telegram vector icons / quiet unverified state

Base: private P4.15.3 refinement commit `0cd07963e15ab44d35ac7eb534d7edfe242f86f7`.

## User feedback
- A Windows screenshot at 1531×923 shows clipped/malformed chat-star glyphs, square-ish archive glyphs, and folder glyphs in Telegram's three-pane workspace.
- "Son parça belirsiz" appears as a repeated third line in each archive row and again in the selected-media summary. User explicitly requests no such informational warning.

## Changes
- Use WPF `Path` geometry for Telegram folder, chat star and media type icons (video, archive, subtitle, audio and fallback document). No font glyph fallback, pixel clipping or emoji font differences. Star outline/fill uses `IsFavorite` state, with distinct add/remove labels and independent selection behavior.
- Remove only the **user-visible** `unverified` final-piece status from Telegram media rows, selected-file summary and `TelegramMediaItem.MetaLabel`. Continue to display genuine `missing`, `ambiguous` or standalone-part warnings. Keep file type, size and detected physical part count.
- **Do not change** `PartStatusCode`, `ArchiveSetComplete`, `ArchiveSetAmbiguous`, `NeedsArchiveRepair`, TDLib, Range streaming, ZIP/7z detection or playback. Unknown final-piece status is still available internally, never silently upgraded to verified. If seeking beyond actual available bytes fails, report a real playback error rather than giving a false completeness claim.
- Preserve P5 read-only `ProgressPercent, Mode=OneWay` crash fix and P4.15.3 compact/scrollable Hub.

## Validation
- P1–P5 offline Telegram tests, 7z Store Range fixture, x64 Release Windows CI.
- Live on Windows: check at 100/125/150% DPI, chat star toggle, folder icons, five media type icons, missing/ambiguous state, unknown-final split playable item, tooltip and media pane footer, mid and narrow widths. Validate stream starts and multiple seek positions (including near end) without WPF crash.
- Cold/warm Telegram performance and multi-account history isolation are separate unresolved release gates.

All changes on a private branch, no public release or Android changes.
