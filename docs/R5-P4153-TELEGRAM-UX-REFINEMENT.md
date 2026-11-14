# R5 / P4.15.3 — Telegram UX Refinement (Windows)

Base: `feature/v1.0.2-r5-p4152-live-crash-fix`, including the Telegram Hub read-only progress binding crash fix. This is a **private preview**, not an installer/public release.

## Naming

Keep **Telegram Hub** rather than "Diary" (which implies a personal journal). The dedicated Hub is a playback/shortcut center. The Telegram tab remains a chat/channel media explorer.

## Changes

1. **Hub buttons/cards:** The shared `R23Button` template forces `ContentPresenter HorizontalAlignment="Center"`, causing media rows to appear as narrow, tiny centered content on an otherwise wide card. TelegramHubRowButton now provides its own border/template with a **stretched ContentPresenter**, visible stroke, hover and focus. Source actions and bindings are unchanged. The P5 `ProgressPercent, Mode=OneWay` crash fix remains.
2. **Less text:** Hub explanatory content is still available via section title tooltips; the long permanently visible helper lines are removed. Session-discovered entries remain explicitly session-only, without implying all of Telegram was indexed.
3. **Bounded session list:** A 300-DIP scroll viewer keeps 30-session-item source data available without an excessively tall home dashboard. Entries use two columns at normal width and one column in compact mode (<780 DIP), without a new TDLib request.
4. **Starred chats:** The chat-list ★/☆ toggle is always visible, 34x34, clearer selected color and border. Tooltips change between "Add to starred" and "Remove from starred" in TR/EN. It remains an independent button and does not navigate or alter chat selection.
5. **Archive labels:** Unverified raw/generic split **is NOT missing** and **is NOT confirmed complete**. A short, neutral "Son parça belirsiz / Final part unknown" hint replaces alarming long inline warning; full explanation appears on hover. Truly missing/ambiguous cases still appear in warning/error colors. Verified items need no redundant third-line success notice. No changes to archive grouping, `ArchiveSetComplete`, data-level `PartStatusCode`, TDLib Range, 7z Store streaming, download or seek.

## Test plan (manual QA not yet done)

- Windows WPF at 100%, 125%, 150% DPI, regular/narrow window, keyboard focus, tooltips and localized labels.
- Hub cards full width; resume card reads progress without crash; starred / last opened / seen actions still open the right content.
- Chat list: click ★/☆ in a selected and unselected row. Selected chat and active playback must not be changed.
- Archive case matrix: truly missing, ambiguous duplicate, verified Store and raw split with unknown final part. Unverified raw can attempt playback without claiming entire file is verified. Seeking to missing bytes may fail and must never be misreported as a success.
- P1–P5 offline regressions + Windows Release x64 CI.
- Compare Telegram cold vs warm chat load, multipart open and seek times to P4.15 on the same file/profile conditions; this UX change **does not claim to fix** the separately reported performance regression.
- Account switching with two Telegram accounts is a **release blocker** until P5 history isolation is implemented and verified.

No public release, main/Android changes, automated session imports or account-wide TDLib scanning.
