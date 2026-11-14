# R5/P4.15.4 — Unified Continue Watching (Windows, private candidate)

Base: `feature/v1.0.2-r5-p4153b-favorite-star-padding` at `560f82aa6665a99c0a62e734a8b77e8d26234cd9`.

## User-facing behavior

- Home hero and the existing Continue Watching carousel aggregate resumable local files, reopenable HTTP/HTTPS URLs and Telegram media with **stable** `telegram:` / `telegram-archive:` source IDs.
- Telegram Hub retains its dedicated Telegram-only Continue Watching panel. The two pages use the same saved resume store; there is no ADB Cloud login requirement.
- Entries are unfinished, minimum 5 seconds watched and at least 45 seconds remaining. Deduplication by persistent source; newest watch position first, up to 20 in the combined Home rail. The Home cards show a TR/EN source-kind tag.
- The existing `PlayContinueWatchingAsync` reopens Telegram via `Telegram.OpenPersistentAsync`, which reconstructs a fresh TDLib stream. Temporary localhost Range/seek URLs are never used as persistent Home cards.
- HTTP/HTTPS entries can include supported web/cloud streams whose URLs are directly reopenable. No claim is made that arbitrary rclone/proprietary cloud schemes can be reopened by Home.

## Privacy and account change

- A new read-only `ITelegramService.GetCurrentUserIdAsync` returns the ID from TDLib `getMe`. The default interface implementation returns null for other service providers/fakes (fail closed).
- `TelegramPanelViewModel.CurrentAccountId` clears at logout and identity changes; getMe is asynchronous and does not block chat loading.
- Device-local `PlayerSettings.TelegramResumeSourceOwners` maps stable Telegram source identity to the verified Telegram account. All Telegram candidates are filtered **before** the resume count limit.
- Home/Hub recompute on authorization and verified-user changes. Home removes Telegram entries immediately on logout and refreshes asynchronously with a generation check to discard stale results. Home's session-seen previews and TelegramPanel's in-memory previous-account lists are cleared/hidden.
- `OpenPersistentSourceAsync` refuses to reopen an account-mismatched Telegram source. `OpenSourceAsync` refuses to restore an account-mismatched saved seek position, even if the same chat/message identifier collides across accounts.
- Existing P5 data had **no account provenance**. There is a one-time local legacy attribution to the first *verified* Telegram account so previously visible P5 resume entries can appear in Home; it never reattributes those entries on a later account switch. **Limit:** this is not cryptographic proof of the legacy item's origin. If this portable profile was previously shared between different Telegram accounts, older mixed data might be attributed to the first account. Multi-account migration audit and account-scoped pre-existing favorites/History remain a release gate; DO NOT publicly ship until verified.

## Non-goals

- No ADB Cloud requirement or automatic remote watch-history synchronization.
- No change to Telegram ZIP/7z Store Range, private tracker behavior, subtitles, cache retention or playback engine.
- No new TDLib chat scans; one `getMe` identity request on authorization only.
- No Android or public Windows release changes.

## QA gates

1. With ADB Cloud logged out and Telegram user A signed in: Home hero and rail show A's unfinished Telegram items alongside local and valid HTTP media; Hub still shows its Telegram-only resumes.
2. Click a Home Telegram resume; verify the intended exact item, saved position and 7z Store seek after restart without WPF XAML binding crash.
3. Stop/restart and verify order, source label, no loopback URLs and only really opened (not session-seen) media.
4. Sign out Telegram: Home Telegram items vanish immediately; local and HTTP entries remain.
5. Sign in Telegram user B in a test profile: no A titles/positions/history in Home or Telegram Hub; do not attribute legacy mixed data without inspecting the source provenance.
6. Return to A: only matching A-owned resumable sources return; switching while a resume refresh is in flight must not display stale A items under B.
7. Validate Home at 100%,125%,150% DPI with keyboard/hover and reduced motion.
8. Run full P1–P5 source/fixture checks and Release x64 CI; do not treat them as live Windows acceptance.

See `tool/verify-r5-p4154-unified-continue-watching.ps1` and `tool/telegram-hub-smoke` for offline test contracts.
