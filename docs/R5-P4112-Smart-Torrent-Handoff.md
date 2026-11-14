# R5/P4.11.2 — Smart Torrent Handoff & Recheck

**Branch:** `feature/v1.0.2-r5-p4112-smart-torrent-handoff`  
**Base:** R5/P4.11.1 (`e2a177bf7f86442a6815e52c0f3cba892e124773`)  
**Release:** private Windows test only. Public v1.0.1 unchanged.

## How to use

1. **Settings → Download location:** choose a persistent folder; restart ADB Player.
2. **Torrent → qBittorrent connection:** connect to a qBittorrent WebUI on this Windows PC (`http://127.0.0.1:8080` by default).
3. Open an **original `.torrent` file** and let some data download.
4. Press **Smart transfer and verify**, then confirm that playback will stop.
5. ADB Player stops its torrent manager **without deleting any data**, sends the original metadata to qBittorrent with the same save path and root-folder layout, verifies qBittorrent file mapping, requests **Force recheck**, then starts that specific qBittorrent torrent. qBittorrent checks valid pieces before using them and follows its own seed limits.

## Safety rules

- This option **requires an explicitly selected persistent download directory**. The disposable internal cache is not used as a shared seeding directory.
- Smart transfer is **explicit**, never automatic while streaming. The old automatic handoff is disabled for custom/shared folders to avoid simultaneous writes by two torrent engines.
- The qBittorrent WebUI host must be local loopback; remote qBittorrent and SSH-forwarded remote servers are not supported as shared-file handoff targets. A loopback tunnel cannot be reliably identified; users must ensure qBittorrent actually runs on this same Windows machine.
- An original `.torrent` is required, not a bare magnet. Private tracker announce/passkey metadata and torrent infohash must be preserved.
- The selected download directory, torrent root and file names are checked against both the torrent metadata and MonoTorrent's actual snapshot paths. Existing normal-named files must be present. Files with unsupported `.!mt` temporary names from older downloads are not auto-renamed.
- For **new** sessions in a custom download folder, MonoTorrent is configured with `UsePartialFiles=false` so qBittorrent can rehash in-place; legacy internal cache continues using partial files.
- Preflight checks the qBittorrent login and duplicate infohash **before playback is stopped**. Existing qBittorrent jobs are never modified.
- When the user confirms, ADB Player removes the manager with `deleteFiles:false` (single-writer ownership handoff). It sends the original torrent using `savepath`, `paused=true`, `root_folder=true`, `autoTMM=false`, `skip_checking=false`.
- Newly created qBittorrent job is explicitly stopped (`stop` v5, `pause` v4), its save path and file list are checked, then `recheck` and `start`/`resume` are requested by **specific infohash** only.
- If an error occurs **after Player releases ownership**, do NOT restart the Player downloader or delete qBittorrent data. Tell the user to inspect the stopped qBittorrent torrent. No credentials, tracker URLs, passkeys or torrent paths are logged.
- No background service is added. qBittorrent must run to keep seeding. Ratio/time/tracker whitelist rules remain qBittorrent and tracker responsibilities.

## Limits

- Not a general-purpose migration for existing qBittorrent jobs or previously downloaded legacy `.!mt` files.
- The qBittorrent API can accept a recheck request, but **CI cannot prove the torrent reached 100% or seed state**. Verify using real qBittorrent job progress and tracker announce information.
- If path mapping differs, the qBittorrent job is left stopped and files are preserved. qBittorrent may create empty folders before a file-layout error is detected; no automatic folder cleanup or forced renames occur.
- Users should keep old `Transfer to qBittorrent` for independent qBittorrent downloads. With a user-selected shared directory, use **Smart transfer and verify** instead.
- Windows qBittorrent accessed via an SSH port-forward to a separate server must not use Smart Handoff even if the WebUI address is `localhost`. A remote machine does not see the same C: drive.

## Acceptance checks

- [ ] GitHub Actions P4.1–P4.11.2 guards and Release x64 build succeed.
- [ ] English/Turkish labels and confirmation are short and correct.
- [ ] With no torrent or custom folder, Smart transfer is blocked and playback continues.
- [ ] Original `.torrent`, valid matching on-disk files and same-computer qBittorrent pass preflight.
- [ ] Existing infohash in qBittorrent: detect duplicate and **do not stop Player**.
- [ ] Cancel confirmation: Player and its streaming session continue.
- [ ] Confirm: Player stops with no deletion; qBittorrent imports into same root and rechecks.
- [ ] qBittorrent v4.x uses pause/resume; 5.x uses stop/start.
- [ ] Wrong folder or mismatched file tree: keep qBittorrent stopped and preserve the Player files.
- [ ] qBittorrent verifies downloaded pieces, fetches missing pieces, then seeds after completion.
- [ ] Closing Player does not remove qBittorrent job or its data.
- [ ] Existing normal torrent playback, Discover and Prowlarr torrent flows still work.

**Validation:** static guards and Windows CI are necessary, but the real transfer/recheck scenario still requires a Windows machine with qBittorrent and legal test torrent data.
