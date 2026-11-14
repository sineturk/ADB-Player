# R5/P4.15.2 — P3 Telegram Media List Refresh

**Base:** P2 Telegram Desktop-style chat list, including P1 Home Telegram quick access.
**Working branch:** `feature/v1.0.2-r5-p4152-telegram-ux-p3-media-list`
**Validation policy:** Offline source, fixture, XAML, localization and Windows Release x64 CI only. **No live Telegram or real-media playback tests until all P1–P5 work is complete.**
**Public/main release:** unchanged.

## User-facing media list

- Compact list rows with real TDLib filename as the title. A message caption such as "MEDIA INFO" must never substitute for the filename.
- Distinct visuals and Turkish/English labels for **Videos, Archives, Subtitles, Audio, Documents**. No inferred 1080p/4K labels based on untrusted filename guesses.
- Source-derived metadata: file size, known duration, number of grouped archive parts, message date, and a truthful archive status.
- Part states:
  - `ready`: verified complete ZIP, 7z, or RAR multipart set.
  - `unverified`: raw or generic numeric split parts are ordered but the final part is not verifiable from filenames alone.
  - `missing`: gaps or a missing final part.
  - `ambiguous`: duplicates/uncertain ordering requiring manual review.
  - `standalone`: individual archive part without a usable assembled set.
- **Local category chips**: All / Videos / Archives / Subtitles / Audio / Documents. They filter only already enumerated `Media` items; no new TDLib search requests.
- `FilteredMedia` is maintained by ordered incremental collection reconciliation. `SelectedMedia` stays intact even when the current filter hides its row. The persistent selected-file deck explicitly names that file, preventing accidental actions on an unidentified selection.
- Media virtualizes and reuses list rows. Selected hover/focus styles are scoped to Telegram, leaving other player pages unchanged.

## Selected-file actions

- **Open:** existing `DownloadAndOpenAsync`, including Store fast path and normal archive entry selection. Disabled for an orphan part or an incomplete/ambiguous archive.
- **Add subtitle:** existing subtitle or archive inspection path; only shown as enabled for compatible selected sources (the playback engine still checks whether a video is open).
- **Download to cache:** explicit new action that uses TDLib `DownloadMediaAsync` for the selected file or every known file in a complete archive set. **Not a Save As or export to the user's normal downloads folder.** It is available only while the player is idle, so it cannot replace the active Telegram Range transfer bookkeeping. Cache files may be evicted by existing P4.14 policies. Stop cancels the async operation, completed parts are not silently exported, and resuming a canceled full download requires selecting Download to cache again. The progress percentage represents the currently reported TDLib file, not an invented aggregate across every part.
- **More:** open cache folder and repair grouped archive parts; repair disabled when not applicable.
- **Transfer controls:** Stop/Resume are hidden unless a transfer can use them. This **Resume** means resuming a paused TDLib transfer, not the Continue Watching playback-position feature.
- **Language:** all new row types, category chips, selected-file summaries, status labels and cache messages have both TR/EN resources and respond to UI language refresh.

## Guardrails

- No changes to Telegram archive grouping, TDLib Range server, split ZIP/7z playback, Store byte validation, subtitle synchronization, account login or TDLib database configuration.
- No live Telegram login, downloading, opening or seek tests during P3. CI may execute existing synthetic/offline fixtures and compile the application.
- Any media UI changes must not cause a selected media switch when the user changes only a category filter.
- Existing P1 and P2 regression contracts are updated to recognize `FilteredMedia`, not to reintroduce the removed Telegram recent-media panel.

## Offline checks

1. `tool/verify-r5-p4152-p3-telegram-media-list.ps1`: XAML parse, categories, selected actions, TR/EN keys, 7z Store guard.
2. `tool/telegram-media-list-smoke/`: data-model classification and truthfulness of multipart status.
3. Existing P1, P2, P4.15, 7z Store, ZIP, Telegram synthetic fixture regressions and Windows Release x64 compilation.

## Planned next stages

- **P4 — Telegram Desktop visual polish:** spacing, sidebars, responsive layout, hover/focus and navigation ergonomics.
- **P5 — Telegram Hub:** richer global quick-access/continue-watching destination; decide how to persist and refresh favorites and recents without inventing all-account coverage.
- **After P5 only:** single combined real-world Windows acceptance pass covering the entire P1–P5 chain, actual Telegram grouped archives, 20-minute seek, selected media actions and TR/EN switching.
