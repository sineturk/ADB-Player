# ADB Player Windows v1.0.2 — R5 / P2 Sources & Connections

> **Private development only.** Based on P1 commit `f7833aed41faaa86b8fb1e6a48befaa9bf4f7f01`. The user elected to defer all Windows build, CI and visual acceptance tests until the rest of the R5 work is complete.

## Implemented

### Home — bounded Sources summary

- Displays at most **four** saved cloud accounts without mutating `SourcePanelViewModel.CloudAccounts`.
- Uses permanent `SavedCloudAccount.Id` to derive a short *stable fingerprint* for duplicate displayed names. Different pCloud accounts stay separate, even if provider labels match. The remote name itself is not shown.
- Shows provider label, icon glyph and rclone **runtime** status. Runtime availability is not represented as verification of a specific account's authorization or ability to browse.
- Displays at most **five** saved public-link / WebDAV shortcuts combined. `SavedPublicLinks` is already sorted by last opened time; WebDAV fills remaining slots.
- A saved link's URL, signed query, remote token, or WebDAV secret is never used for the Home caption or tooltip. It displays a recognizable short user label or service/host.
- Accounts and links retain their original references when selected. The existing `OpenCloudAccountAsync`, `OpenSavedLinkAsync` and `OpenWebDavProfileAsync` operations remain the action targets.
- Empty collections show friendly no-account/no-link messages; both panels have **View all** opening the existing Sources workspace.

### SourcesView — full management

- A visible search field filters source **previews** without editing, deleting, or merging the underlying saved collections.
- Filters accounts by visible name/provider/short stable identity; public links by name, provider, and host; WebDAV by name/host.
- Retains existing source connect, disconnect, saved-link open/remove, WebDAV open/remove, password and browser interactions.
- Connected-account manager rows display a stable short account fingerprint; the original saved account data is untouched.
- Search empty states and the explicit note distinguishing rclone runtime availability from per-account reachability are localized.
- Adds matching Turkish and English language strings.

## Protection rules

- Do not change the persistence format for `SavedCloudAccount`, `SavedPublicLink`, or `SavedWebDavProfile`.
- Do not use `RemoteName`, full URLs, raw query parameters or credentials as Home UI labels/tooltips.
- Do not claim a per-account "connected" health check when only `RcloneAvailable` was read.
- Do not alter `MainWindow` playback HUD, video HwndHost, subtitles, synchronization or Mini Player.
- Public `sineturk/ADB-Player` and public v1.0.1 remain unchanged.

## Deferred P2 acceptance checks (not run yet)

1. Run `tool/verify-r5-home-p0.ps1` together with previous R4.1.1 tests; check XAML, WPF compile, Windows Release x64 build, libmpv payload and SHA-256 artifact.
2. With 3 separate pCloud Europe accounts that share the same label, all three must be individually selectable and must open their original saved account.
3. `RcloneAvailable=false` changes the runtime state label but does not delete or hide saved accounts; no account-specific status is invented.
4. Six or more links: Home shows only five; the Sources manager keeps every saved link; saved WebDAV accounts remain preserved.
5. Signed Google Drive URL with tokens/query, full Gofile URL, custom title, non-Latin name and WebDAV URL: Home reveals no signed query/secret. Link opening still passes original object to the existing handler.
6. Home no-account/no-link cases; user-facing empty states and "View all" navigation.
7. Search by display name, provider, host and stable account marker; filtering leaves full persisted collections and saved credentials intact.
8. Open, disconnect, remove, save, connect and WebDAV browser behavior in the Sources manager; correct password handling.
9. Desktop/window layouts 1180×600 and 1500×860; keyboard tab sequence, focus, 125%/150% DPI and scroll behavior.
10. P0/P1 resume cards and media metadata fallbacks plus R4.1.1 paired Mini Player HUD regressions.

## Next

**R5/P3 Glass & Motion / QA**: only subtle WPF-card transitions, performance and accessibility. Then run one consolidated Windows CI and on-device visual acceptance pass. Obtain separate approval before any public release.
