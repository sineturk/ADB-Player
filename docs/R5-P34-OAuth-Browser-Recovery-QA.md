# ADB Player Windows v1.0.2 — R5/P3.4: OAuth Browser Recovery

**Private branch:** `feature/v1.0.2-r5-p34-oauth-browser`  
**Base:** R5/P3.3, commit `5facbde7bac22021315b877379fd18ab728c5c1c`

## Confirmed desktop behavior motivating the change

- Existing Google Drive account opens and lists files: the installed rclone binary and existing saved authorization work.
- Selecting the **Google Drive** provider card and connecting a new account fails **before a browser opens**.
- P3.3's generic "localhost/callback" error classification was too broad; the displayed wording did not prove a browser callback had been attempted.

## Root cause in Windows integration

Before P3.4 the app called `rclone config create ... config_is_local true --auto-confirm`, redirected stderr and waited for process exit before reading it. rclone can print a localhost authorization link *while it is waiting for a user*. If its own browser launch fails, Windows had no way to open that live URL manually because the app only read stderr after the process ended.

## P3.4 fix

1. Ask rclone to **not** start a browser itself (`config_auth_no_browser=true`), but still run a local OAuth server.
2. Read stderr **line by line during authorization**, not after completion. Match only a strict, loopback `http://127.0.0.1:<port>/auth?state=...` or localhost URL; reject remote HTTPS/foreign hosts, paths and malformed links.
3. Invoke the app callback on the WPF UI context and start the system default browser via `ProcessStartInfo.UseShellExecute = true`.
4. The connection sheet provides **Tarayıcıda aç** and **Bağlantıyı kopyala** buttons for the short-lived authorization link. The state-bearing URL is never shown as literal UI text or logged. Copying requires an explicit user action; tell users not to share it.
5. Browser failure, OAuth port conflicts, client credential issues, network failure and denied permissions have distinct privacy-safe diagnostic messages. No assumption that merely mentioning localhost proves a callback failure.
6. **Vazgeç** or **Esc** now cancels an active OAuth attempt; rclone's process is stopped and no new account is committed.
7. Existing saved Google Drive, Dropbox/pCloud accounts, remote IDs, secrets and unrelated player components are preserved. Verify-before-save remains enforced.
8. No Membership/Premium gates, Neon schema changes or public release.

## Private CI and manual acceptance

The workflow runs existing R4/P0–P3.3 checks plus `tool/verify-r5-oauth-browser-p34.ps1` (XAML parsing, localization, loopback URL validator, concurrent stderr reader and app browser fallback), then Windows Release x64, libmpv and SHA-256.

**Interactive validation** must happen on the user's Windows desktop:
- [ ] Existing Google Drive still lists folders without reconnecting.
- [ ] Google Drive → Hesap bağla opens the system browser with the new local authorization page. Google consent leads to correct account listing.
- [ ] If the default browser refuses to open, **Tarayıcıda aç** retries; **Bağlantıyı kopyala** allows pasting into the same PC's browser.
- [ ] Pressing Esc/Vazgeç while the local authorization server is running stops the attempt, restores staged custom credentials and leaves existing accounts intact.
- [ ] Validate Dropbox and pCloud Europe with default OAuth settings first. Custom OAuth client ID/secret remains optional.
- [ ] Two same-name pCloud remotes do not merge; per-remote app identities and refresh remain distinct.
- [ ] If rclone fails before generating a URL, show the specific safe error category, not a misleading localhost callback assumption.
- [ ] Neither local OAuth state token nor client secret is present in UI text, diagnostics or screenshots.
- [ ] Verify actual file streaming, UI focus and source navigation on Windows; CI cannot simulate an interactive OAuth consent screen.

User credentials and raw OAuth logs should never be shared. A screenshot of the **sanitized error category only** is sufficient for a future follow-up.
