# ADB Player Windows v1.0.2 — R5/P3.2

**Private development:** `feature/v1.0.2-r5-p32-username-source-cards`  
**Base:** `feature/v1.0.2-r5-p31-neon-login-artwork`  
**Scope:** Android-style username/email login and dedicated cloud source provider cards.  
**Exclusions:** No Premium/Membership gates, no Neon migration or production schema modifications, no public release.

## Normal ADB Cloud user login

- Production v0.8 ADB Cloud Function handles `POST /v1/auth/sign-in` with `identifier` (email or username), `password`, and `rememberMe`.
- The Windows app deliberately does **not** query username tables or embed Neon database credentials.
- Server response `authCookieHeader` is filtered to opaque Neon/Better Auth session tokens, attached only to the configured HTTPS Neon Auth host.
- The app checks `/get-session` before accepting sign-in and requesting metadata JWTs.
- Existing `/token`, Cloud Core, user/device sync, and poster refresh are preserved.
- The Account login field says **Kullanıcı adı veya e-posta**. Registration still requires a valid e-mail address.
- Remember-me enabled: session cookie saved with existing `ISecretStore`. Disabled: no persisted cookie.
- No Premium and Membership controls are enabled.

## Sources & Connections

- Six large source cards: Google Drive, Dropbox, OneDrive, pCloud Europe, pCloud US and WebDAV.
- Click a card to open a source-specific in-app connection sheet. OAuth providers use the existing secure browser/rclone connection workflow, where users supply Google/Dropbox/Microsoft/pCloud credentials; **ADB Player does not collect the OAuth provider password**.
- WebDAV has a dedicated account name, HTTPS URL, username and secure PasswordBox in the connection sheet; the existing secret store saves the password.
- Previous saved account IDs, rclone remotes, WebDAV profiles, links and history are preserved. No automatic disconnection, migration or cleanup.
- The old provider picker and inline WebDAV form were replaced with service cards; saved links remain editable and existing source list/browser remains in place.
- Keyboard Escape/cancel closes the connection sheet unless an active connection is ongoing.
- The card grid switches from three to two columns at smaller widths.

## Validation

The dedicated `tool/verify-r5-username-provider-cards.ps1` checks source contracts, locale keys, card handlers and preserved source connect/disconnect behavior. Existing R4/P0–P3 and P3.1 source checks, .NET 10 Windows Release x64, libmpv payload and SHA-256 also run in private GitHub Actions.

**Desktop acceptance tests** (must be checked locally): username case-insensitive login, email login, incorrect password, remember-me persistence, account logout and relogin, correct posters after sign-in, OAuth browser login for Drive/Dropbox/OneDrive/pCloud, duplicate pCloud account labels, WebDAV HTTPS credentials, canceled login, source browsing, low-width layout, Keyboard Tab/Escape. No credentials are to be posted in issues or chat.

Private testing does not publish to `sineturk/ADB-Player`.
