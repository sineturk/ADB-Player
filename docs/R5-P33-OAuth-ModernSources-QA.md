# ADB Player Windows v1.0.2 — R5/P3.3 OAuth & Modern Sources

**Private branch:** `feature/v1.0.2-r5-p33-oauth-source-ux`  
**Base:** `feature/v1.0.2-r5-p32-username-source-cards`  
**Scope:** cloud provider connection troubleshooting, optional OAuth app identity, modern saved source management. **No public release, Neon schema change, Membership activation or account migration.**

## Why a provider card used to fail silently

The prior `SourcePanelViewModel.ConnectCloudAsync` caught every exception and displayed the same generic message. rclone's `config create` exit code was treated as sufficient to persist a new remote; no credentialed listing was required before saving.

## R5/P3.3 changes

1. **Existing rclone OAuth** remains the default for Google Drive, Dropbox, OneDrive, pCloud Europe and pCloud US. A provider password is **not** requested inside ADB Player.
2. Advanced form (collapsed until opened): optional **OAuth Client ID / App Key** and **OAuth Client Secret / App Secret**. These are *developer application credentials*, not user account username/password. Require both or neither.
3. Optional custom app credentials are first staged in the existing Windows-protected `ISecretStore` for authorization. If authorization succeeds, they are stored **per newly created remote**, then original provider defaults are restored. Failed attempts roll back the previous OAuth settings, protecting multiple already-linked Dropbox/pCloud accounts. They are never written to plain app settings, GitHub or user-facing logs.
4. rclone uses the protected credential environment for authorization **and subsequent remote browsing, downloads and local secure stream server** (refreshing OAuth tokens remains possible).
5. **Verify-before-save**: after `config create`, attempt a `lsjson` at remote root. Only store the new account if it is actually usable. On failure, delete only that just-created temporary remote, leaving previously saved remotes untouched.
6. Provide privacy-safe, distinct diagnostics for missing rclone, timeout, rejected authorization, invalid client/redirect, localhost/browser callback failure, and network failure. Never display raw stderr, tokens, auth codes or callback URLs.
7. The source screen now groups saved accounts and links/WebDAV in dark, keyboard-accessible tabs. Connection cards stay at the top. Saved items display in compact cards with Open and Remove/Disconnect actions; large collections scroll within the tab rather than expanding the full page.
8. Fix the search-clear icon font. Preserve source filtering, saved link creation and existing playback browser. No saved source account or link is automatically deleted or merged.

## Acceptance checklist — real Windows required

- [ ] The source screen opens at 1500×860 and 1180×600; service cards remain legible. Existing saved account and link rows appear under the appropriate tabs.
- [ ] Dropbox and pCloud EU custom OAuth fields are present under **Özel OAuth uygulama bilgileri**. Blank fields still attempt normal provider authorization.
- [ ] Supplying only Client ID or only Secret yields in-app validation; no provider connection starts.
- [ ] Entering a valid provider app credential pair opens the provider's browser authorization. No account password is typed into ADB Player.
- [ ] After browser permission, the remote root is browsable before the account is added. Rejected or canceled auth leaves the previous account list unchanged.
- [ ] An invalid client, popup/browser callback issue or missing rclone produces a specific, safe help message inside the connection sheet — no duplicate pop-up.
- [ ] A previously saved Google Drive and three different pCloud accounts still open separately; no automatic unlinking.
- [ ] Video playback, subtitle attachment and HTTP streaming from newly authorized providers still work after app restart (refresh token path).
- [ ] WebDAV user/password and stored profiles continue working with the existing protected secret store.
- [ ] Search filters do not mutate original collections. All links remain available in the Links/WebDAV tab.
- [ ] Source sheet closes via cancel and Escape when not currently connecting; no access token, credential or signed URL is displayed.
- [ ] Normal Neon username/email login and poster refresh (P3.2 / P3.1) continue without membership gates.

## Test artifacts

The private Windows CI workflow checks P0–P3.2 and `tool/verify-r5-oauth-sources-p33.ps1`, compiles Release x64 and validates the libmpv payload and SHA-256. It does **not** perform live OAuth, which requires user action in the provider browser.

If an OAuth connection still fails, record the **safe on-screen diagnostic category and provider**, not your credentials or any raw logs with tokens.
