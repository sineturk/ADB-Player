# R5/P4.11.2c — Magnet Smart Handoff / Single-File Layout Fix

Private branch: `feature/v1.0.2-r5-p4112c-smart-handoff-magnet-fix`  
Base: `feature/v1.0.2-r5-p4112b-qb-connection-state`, `b331f50b9f92bec9b2a318c1f9bf8a9e6b66d9e1`.

## Failure found from real Windows screenshots

- qBittorrent WebUI status was **Connected / Ready**.
- ADB Player showed a **100% completed 1.86 GB video** downloaded into the selected `D:\Torrent` folder.
- The underlying source was a full `magnet:?` URI (Torrentio/Discover path). The previous planner rejected **every magnet**, requiring `session.Source.EndsWith(".torrent")`.
- The prior planner *also assumed* all torrent data lived under `<download folder>\<torrent name>\...`, which is not guaranteed for **single-file torrents**, so valid downloads could fail preflight despite 100% progress.
- A generic error status obscured the actual reason, and the manual `Send to qBittorrent` button was a no-op for custom folders.

## Changes

1. Smart Handoff accepts both the **original torrent file** and an **original magnet URI with v1 BTIH infohash** (hex and Base32). For private trackers, the full magnet tracker parameters are forwarded unchanged. Unsupported v2-only magnets report a safe, explicit error; do not invent passkeys or tracker URLs.
2. The source file's original `.torrent` metadata is still checked against the streamed file list when it exists.
3. The planner now reads actual `TorrentSessionSnapshot.Files[*].Path` on disk, verifies paths are inside the selected persistent folder, checks known lengths and requires at least one populated file. It chooses `Subfolder` vs `NoSubfolder` based on real paths, never from the torrent name alone.
4. Both Torrent-tab buttons use **Smart Handoff** when a persistent download folder is in use. The normal isolated-cache path still supports the independent legacy upload.
5. qBittorrent receives either magnet via the `urls` form field or full original `.torrent` bytes via `torrents`; along with `savepath`, `contentLayout`, `root_folder`, `stopped/paused=true`, `autoTMM=false`, `skip_checking=false`.
6. Player requires explicit user consent and stops the MonoTorrent manager **without deleting files**, **before** allowing the qB job to download/write.
7. qB magnet metadata is sometimes unavailable while stopped. After ADB Player releases ownership, the new job is started only to fetch metadata (maximum 60 seconds), then stopped for file-path matching. On mismatch/timeout, the job stays stopped and the user's files remain untouched.
8. It validates the qB job's save folder and all returned file names against exact real downloaded paths, using a single consistent layout. On match, requests `recheck` and `start/resume` for that specific infohash only.
9. When preflight fails, display the safe specific error rather than just a generic failure; after ownership release, display that files are preserved plus the reason.
10. Security constraints and previous protections remain: local-loopback same-PC qB WebUI only, authenticated WebUI/DPAPI, no logging secrets, no duplicate-torrent mutation, no hash-check bypass, no deletion, and no unrelated qB jobs altered.

## Windows acceptance for the reported failure

1. Start qBittorrent WebUI on this machine, confirm green **Bağlı · kullanıma hazır**.
2. In Settings select permanent `D:\Torrent` (or user folder). Restart Player.
3. Open a **magnet** from Discover/Torrent that downloads a permitted single video file into this folder. Wait until file exists (100% is useful).
4. Torrent tab → **Akıllı aktar ve doğrula** OR **qBittorrent'e aktar** → approve the transfer (the player will stop playback).
5. Check qBittorrent. The torrent should appear in the **same** download directory, be rechecked for existing pieces, then resume/seed according to its tracker and sharing settings.
6. For any failure, inspect the new concrete message. The player must leave all files in place. If qB added a stopped torrent, inspect its save path and content layout before resuming.
7. Repeat with a real `.torrent` file and a multi-file torrent. All files must be matched before qB is started for download/seeding.
8. Test a duplicate torrent already in qB and canceling the confirmation. Neither should stop the original Player torrent.
9. Do not use a loopback SSH tunnel to a remote qB server: its disk paths are not the same. Do not change a private tracker's seed policy.

## Explicit limits

- **Completed ADB Player % is not proof of full torrent completion** if only one video was prioritized. qB may correctly find other missing torrent pieces; it will download them before declaring seeding complete.
- Not automatic post-playback handoff yet. Keşfet opens the shared torrent session; transfer is started from the Torrent tab once the session is prepared.
- No automatic transfer for user-selected shared folders while streaming: dual-writer protection remains in effect.
- Valid qB authentication and Windows compile are checked in CI, but real magnet metadata exchange, torrent file layout, and tracker seeding still require the user-side Windows/qB integration acceptance test.
- Private tracker compatibility depends on the original tracker magnet having a valid announce/passkey and the qBittorrent version being accepted by the tracker.

Public `v1.0.1` remains unchanged.
