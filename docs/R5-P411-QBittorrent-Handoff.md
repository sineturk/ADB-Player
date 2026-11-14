# R5/P4.11 — qBittorrent Handoff for Private Tracker Seeding

Base: `feature/v1.0.2-r5-p410-localization-audit` (commit `84e73116d9debdbc967a0d5b2a6ddd25ec17df03`)  
Branch: `feature/v1.0.2-r5-p411-qbittorrent-handoff`

## Delivered in P4.11 (first iteration)

- In Torrent view, a collapsed **qBittorrent connection** section for WebUI URL (default `http://127.0.0.1:8080`), username and password.
- **Connect and save** tests the WebUI login and application version before persisting the URL/username. Password is saved **only** to Windows DPAPI via `ISecretStore`; never JSON settings, logs or URL.
- **Send to qBittorrent** on the current prepared torrent sends its original `.torrent` bytes (preferred for private trackers) or original magnet text through qBittorrent Web API v2. Authentication uses same-origin SID cookie and a Referer header. Redirect following is disabled.
- Optional **Automatically send torrents I open**: OFF by default. When enabled, it applies to new torrents entered from manual files, magnet, Prowlarr, and Discover, after a successful metadata preparation. Failure to send never interrupts local playback.
- qBittorrent uses its own save directory, download policy, ratio and seeding-time settings. ADB Player never deletes qBittorrent's content nor changes its share rules. qBittorrent must stay running for seeding to continue.
- HTTP WebUI with password is permitted only for `localhost` or loopback IP. Remote WebUI needs trusted HTTPS (or a local VPN/SSH tunnel binding to localhost). Do not expose qBittorrent WebUI to the public internet with plain HTTP.
- Original MonoTorrent streaming remains unchanged, including its playback cache. **The already downloaded bytes in Player are NOT copied or rechecked by qBittorrent in this phase; qBittorrent may download the same content again.** A future shared-data import needs hash verification and paths known to both programs.
- A private tracker may require a whitelisted qBittorrent version, valid announces/passkey, minimum ratio/time, or a complete download. Handoff does not guarantee compliance by itself. Prefer original private `.torrent` over a bare info-hash magnet.

## Short Windows walkthrough

1. In qBittorrent enable Web UI and set a username/password. For same-PC setup use `127.0.0.1:8080` (adjust port if needed).
2. Open **ADB Player → Torrent → qBittorrent bağlantısı**.
3. Enter WebUI address, username and password; click **Bağlan ve kaydet**.
4. Open a torrent, then click **qBittorrent'e aktar**. Or optionally enable automatic sending of newly opened torrents.
5. Confirm the torrent appears in qBittorrent. Keep qBittorrent running after closing Player.

## Acceptance

- [ ] Release x64 CI and P4.1–P4.11 guards pass.
- [ ] Correct local WebUI connection saves credentials securely; bad password fails.
- [ ] Public `http://` URL is rejected before sending credentials; HTTPS remote allowed.
- [ ] Source `.torrent` from a private tracker is transferred as binary with its original metadata, and appears in qBittorrent.
- [ ] Magnet handoff works; none of the magnet's private tracker/passkey data appears in logs.
- [ ] Auto handoff is OFF by default and works for manual torrent, magnet and Prowlarr/Discover entry paths when enabled.
- [ ] Failed qBittorrent connection does not prevent playback in MonoTorrent.
- [ ] qBittorrent jobs remain after Player is closed, and continue according to qBittorrent settings.
- [ ] Reopen Player: URL/username and auto-choice persist; password is retrieved from secure storage, never from JSON.
- [ ] Windows Turkish/English UX labels are short and accurate.
- [ ] qBittorrent upload rules and existing tracker policies are unchanged.
- [ ] No public `v1.0.1` release changes.

## Deferred

- Reusing completed MonoTorrent download bytes with qBittorrent and verifying identical hash / data layout.
- Showing qBittorrent's full torrent job list, ratio, share duration and tracker announcements inside Player.
- A background transfer queue with crash recovery and user-approved path mapping.
- Third-party private tracker whitelist compatibility tests.

CI validates static source contracts and Windows compile. Real qBittorrent WebUI and private tracker compliance require user-side integration tests.
