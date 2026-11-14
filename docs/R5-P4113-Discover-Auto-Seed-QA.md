# R5/P4.11.3 — Discover Automatic Smart Handoff & qBittorrent Verify/Start

Private branch: `feature/v1.0.2-r5-p4113-discover-auto-seed`
Base: `feature/v1.0.2-r5-p4112c-smart-handoff-magnet-fix` (`624d50e92f56922214310e114da92eb063f7b7e0`).

## Root causes

1. With a custom download folder, `AutoHandoffIfEnabledAsync` deliberately sent **nothing** to qBittorrent, even when the **Automatically transfer torrents** checkbox was checked. This applied equally to Discover/Torrentio/Prowlarr and manual Torrent opens.
2. qBittorrent's `recheck` endpoint is asynchronous. P4.11.2c issued `start` immediately after `recheck`; the job could remain stopped once verification finished. Real Windows feedback confirmed users had to click Start manually.
3. Discover's source picker showed release size, but not ongoing download progress or whether Smart Handoff had actually started/completed.

## Behavior

- ADB Player continues to stream from the **user-selected permanent folder**; it **never runs two downloaders writing the same files**.
- When the user has opted into auto handoff, the app watches the selected video file's download completion and keeps that torrent's session identity.
- If playback is still using the streamed source, it shows **Download complete; waiting for playback to end**, preserving uninterrupted viewing.
- When playback naturally ends or the player releases/switches sources, the app performs one **automatic, consented-by-setting** Smart Handoff for that completed session. No extra confirmation popup is shown for the opt-in path; manual Smart Handoff still requires confirmation.
- It checks actual file paths, original torrent or magnet, qBittorrent login, duplicate hash, and shared filesystem path. It removes the Player torrent manager **with KeepAllData**, submits one stopped job, verifies qBittorrent's file list, and requests force recheck.
- It polls **that specific torrent** through qBittorrent states (checking, paused/stopped, queued, stalled, downloading, uploading), sends `start` / `resume` **after checking**, and reissues a bounded start if the first request did not clear the stopped state.
- It never forces seeding beyond the user's qBittorrent configured ratio/time/queue rules; a torrent at 100% can correctly be `stalledUP` when no peers are connected.
- Both Discover's detail modal (for selected torrent streams) and the Torrent tab display the download percentage and localized transfer state. Last qB status is retained in the current ADB Player session even after the streaming torrent closes.
- When the user uses a custom folder, the checkbox explanation now says **automatic transfer after download and playback**, rather than claiming the feature is disabled.
- Default internal cache keeps the original **independent qBittorrent download** behavior, without reusing streamed bytes.

## Safe failures

- Errors before releasing the Player's downloader leave the player session and files untouched.
- Errors after ownership handoff **never restart the Player downloader**. qBittorrent remains stopped on mismatch; data stays in the chosen folder.
- Auto handoff attempts at most once per torrent session, preventing repeated job submissions from frequent progress events. A user can diagnose and retry manually.
- Password stays in Windows DPAPI. No user magnet, passkey, private tracker link, or password appears in added status/log output.
- Auto transfer is available only on local qBittorrent WebUI with shared Windows filesystem, **not** remote WebUI or SSH-tunneled remote filesystem.
- Torrent selection is still scoped to files marked selected by ADB Player. If a torrent has other files, qBittorrent may need to download the remaining pieces before seeding.
- qBittorrent verification can take minutes. If the client loses access or times out, the app reports a failure/inspection state without deleting data; automatic retry is not attempted repeatedly.

## Windows acceptance

- [ ] CI source contracts and Windows Release x64 pass.
- [ ] Settings: choose `D:\Torrent`, restart Player; Torrent connection green.
- [ ] Check "Açtığım torrentleri otomatik aktar" and confirm the friendly wait-until-playback message.
- [ ] Discover: open Torrentio magnet / Prowlarr torrent, select a video and play it; see current download percentage in the detail modal after returning.
- [ ] When selected video reaches 100% while playing, **no interruption**; UI says it is waiting for playback to finish.
- [ ] Finish/close/switch playback: automatic preflight, qB file import and hash recheck occur **without manually clicking Akıllı aktar**.
- [ ] qB 5.x should automatically leave stopped state after recheck: uploading/stalledUP (if 100%), or downloading/queuedDL/stalledDL (if missing data). qB 4.x uses resume instead.
- [ ] Manually initiated Smart Handoff still works, and the same recheck-start retry applies.
- [ ] Cancel/manual errors, missing folder, disconnected qB, duplicate torrent and unsupported source never delete local files.
- [ ] English and Turkish UI show correctly localized transfer stages; don't expose tracker details.
- [ ] Player's first-party public `v1.0.1` release remains unchanged.

**Limit:** Runtime qB integration and private tracker seeding status cannot be proven by Windows source/build CI alone. Manual Windows test with authorized torrent data is necessary.
