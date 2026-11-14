# ADB Player Windows v1.0.2 — R5/P3.1 Neon Auth + poster flow

Scope: normal user login and authenticated poster/backdrop refresh. **No Premium, Membership, entitlement or schema changes.** Private GitHub branch only.

## Confirmed production infrastructure (2026-11-04)

Neon production project `adb-player-cloud`: active Better Auth (email/password sign-in enabled) and deployed `adbcloudapi` Neon Function.

The verified **public HTTPS service addresses** are passed through environment variables in the private Windows CI workflow when generating `GeneratedAdbCloudConfig.g.cs`:

- `ADB_CLOUD_AUTH_URL`: Neon Auth's production `/neondb/auth` endpoint.
- `ADB_CLOUD_API_URL`: the production `adbcloudapi` Function invocation URL.

These are public service addresses, **not** DB connection strings or credentials. The generator's existing `.env.local`/environment resolution remains intact for local development, and production credentials, session tokens, passwords, TMDB tokens, and private keys are **not** bundled.

## Login and artwork behavior

1. Neon Auth REST sign-in/restore/sign-up creates a normal user session.
2. The new `CloudAuthReady` event fires immediately on successful authentication, independent of whether profile/bootstrap/collection sync works.
3. Home artwork is resolved first via the existing `AltyaziDbPlayerMetadataService` authenticated metadata endpoint; then History and Library are refreshed. A short 28-second background deadline covers the initial cold batch.
4. Poster/backdrop results are still subject to existing metadata identity/confidence checks, HTTPS URLs, and on-disk cache. Failed metadata responses are **not** stored as negative successful matches.
5. `CloudSessionReady` remains responsible for separate Cloud Core bootstrap/playlist/preferences/sync. Poster loading is not gated on Cloud Core or Premium.

## Security boundaries

- Windows never connects directly to Neon Postgres. It uses Neon Auth and the existing HTTPS server APIs.
- No Membership or Premium checks are added to v1.0.2.
- No production Neon database schema or user data is modified by this change.
- The AltyazıDB metadata endpoint at `https://altyazidb.com/api/player/v1/` is unchanged; a real authenticated request still needs to be verified on Windows with an actual user session.
- Local builds with a private `.env.local` are unchanged; the public production addresses are injected only in the private CI test artifact.
- Do not share credentials, JWT, signed URLs or log files containing them in public issues.

## Checks

CI: `tool/verify-v1.0.2-feedback-r1.ps1`, `tool/verify-r5-home-p0.ps1`, `tool/verify-r5-neon-auth-artwork.ps1`, .NET 10 Release x64, libmpv and SHA-256, private Actions artifact.

Interactive Windows acceptance still required:

- [ ] Sign-in controls on the Account page are enabled (not "endpoint missing").
- [ ] Existing account can sign in; email/username and profile load.
- [ ] Home, History and Library poster/backdrop refresh starts after successful sign-in.
- [ ] Poster matches the correct film/series; multiple episodes keep distinct playback sources.
- [ ] Cloud Core failure does not block poster requests once authentication succeeds.
- [ ] Sign-out or expired token prevents unauthenticated metadata requests; cached safe local artwork may remain visible.
- [ ] Wrong password gives a proper sign-in error without exposing authentication details.
- [ ] No premium paywall appears on v1.0.2.
- [ ] R4.1.1 Mini Player and R5/P0–P3 playback/navigation continue working.

If user sign-in works but artwork is still blank, check authorized metadata API response statuses and matching decisions before modifying the backend. CI cannot perform authenticated API tests without user credentials.
