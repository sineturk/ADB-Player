# R5/P4.14 — Telegram Cache Management (Windows)

**Base:** R5/P4.13j 7z Store verified direct HTTP Range and seek.
**Scope:** TDLib file-cache settings and maintenance. Playback engine, split archive detection, byte mapping and Range server are deliberately unchanged.

## Features
1. **Ayarlar > Telegram önbellek yönetimi:** approximate TDLib cached-file size and count, archive extraction scratch usage, and separate protected database size.
2. **Configurable TDLib target:** 5 / 10 / 20 / 50 / 100 GiB; default **10 GiB**. JSON persistence in PlayerSettings.
3. **Automatic cleanup:** enabled by default, checked after first authorization and after playback transitions to idle; at most once per 30 minutes; only runs when playback is idle. Calls official TDLib `getStorageStatisticsFast` and `optimizeStorage(size=target, immunity_delay=3600)`. Recently accessed files may temporarily exceed target.
4. **Manual cleanup:** user confirmation and playback-idle guard. Calls TDLib `optimizeStorage(size=0, immunity_delay=0)`, not a raw filesystem deletion of TDLib files.
5. **Safe archive crash residue cleanup:** only app-owned `archive-stream-cache` directories older than 24 hours, not active in the current session, without recently modified files and with exclusive file access.
6. **Selectable cache location:** uses a dedicated `ADB-Player-Telegram-Cache` subdirectory beneath a user-selected local directory. Requires restart. Existing cache is **not moved**; active TDLib database directory and encryption key remain untouched. Reset to defaults is available.

## Storage boundary
- The TDLib files target is **not** a hard global disk cap. Protected/newer cache chunks and active archive extraction can be larger. The UI distinguishes TDLib files, archive scratch and database.
- Never call Directory.Delete on Telegram database, session, secrets or a currently active archive session.
- Manual cleanup is prohibited while playback is not idle; this does not affect regular streaming or seek requests.

## Verification
- `tool/verify-r5-p414-telegram-cache-management.ps1`: storage safety contracts and 7z Store guard.
- `tool/telegram-cache-smoke/`: settings JSON persistence and cache/database usage separation.
- Previous `telegram-7z-store-smoke`, `telegram-zip-range-smoke`, `telegram-all-attachments-smoke` remain in the Windows CI workflow.
- Interactive checks required on an actual Telegram session: reported size, manual cleanup with player idle, refusal during playback, quota persistence, custom directory applied on restart and uninterrupted 20-minute seek.

**Public release:** not changed. Private development branch only.
