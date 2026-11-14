# R5/P4.13j — Telegram 7z Store Direct Range · Closed

**Status:** Stabilized / completed for R5 Windows development.
**Source branch:** `feature/v1.0.2-r5-p413j-telegram-7z-store-direct-range`
**Verified commit:** `6b8fe2c5a7f5d20cc1bb882267fc4ad011db6e5b`
**Windows x64 CI:** [42909892276](https://github.com/sineturk/ADB-Player-V1/actions/runs/42909892276) — success.
**Real-device acceptance (2026-11-11):** User confirmed 20-minute seek resumes playback without the previous long buffering delay.

## Preserved functionality
- Telegram normal message/attachment enumeration and topic history from P4.13h.
- Generic numeric part grouping from P4.13i.
- ZIP Store Range and split 7z playback without data loss.
- A single-file, unencrypted 7z Store fast path that validates archived physical bytes before granting direct seekable Range.
- Non-Store/ambiguous formats remain on the existing guarded fallback (not instant seek).

## Regression gates
- `tool/telegram-7z-store-smoke/` (7z Store + numeric parts + random seeks + compressed guard).
- `tool/telegram-zip-range-smoke/` and `tool/telegram-all-attachments-smoke/`.
- CI test success does not replace manual live Telegram testing; the 20-minute seek was confirmed separately.

## Post-release guard
Do not change the Telegram direct Range playback path as part of unrelated cache-management UI work. Only use TDLib-managed storage cleanup, never delete the TDLib database or active archive session files.
Public ADB Player release remains unchanged; no automatic merge into main.
