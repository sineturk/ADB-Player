# ADB Player Windows v1.0.2 — R5/P4.5 Localized Content Metadata

**Base:** R5/P4.4 `31b9fc7b19e6650a916d90f5ca9edb28a2b60e64`  
**Development branch:** `feature/v1.0.2-r5-p45-localized-metadata`  
**Public releases:** untouched.

## Verified existing contract

- `AltyaziDbPlayerMetadataService` already calls `POST https://altyazidb.com/api/player/v1/metadata/resolve` via the signed-in ADB Cloud bearer token and per-device identity, and parses `media.overview`.
- `IMediaMetadataService.ResolveAsync(identity, includeDetail: true)` already supports an on-demand detail lookup by IMDb ID, TMDB ID or title/year.
- **No explicit content language field or language-selection guarantee is documented in this Player API contract.** A Turkish overview may be available; not every response can be assumed Turkish.
- `LocalizationManager.CurrentLanguageCode` and the live `LanguageChanged` event already reflect Windows language selection.

## Implemented P4.5

1. Keşfet spotlight `FeaturedOverview` and right-side selected detail `Addons.SelectedItemOverview` now use a shared, session-local `LocalizedCatalogOverviewService` in place of blindly displaying addon `Description`.
2. When UI language is **English**, use the source addon's original description immediately, with **no extra metadata request**.
3. When UI language is **Turkish**, first show the original description immediately, then check the authenticated AltyazıDB Player Metadata API. Use `media.overview` only if it is strongly recognizable as Turkish, the title/year/type identity is consistent, and a title-only match has at least 0.90 confidence. Never falsely label an English overview as localized Turkish.
4. Prefer IMDb/TMDB IDs when the Stremio addon provides them; otherwise use title+year. No new API keys, database updates or membership gates.
5. If API is inaccessible, user is signed out, no trustworthy match exists, no Turkish summary is returned, or language detection is inconclusive, keep the original addon text. No translation is invented and the layout does not go blank.
6. Session-local cache per normalized content identity and `tr-TR`: 12-hour successful response, 45-second negative response, at most 300 entries, one metadata lookup at a time. No persistent tokens, media history or user-specific metadata stored.
7. Live language changes, chosen catalog item changes, page unload and account sign-in cancel stale requests or refresh the displayed synopsis. No eager API sweep of the entire 240-item catalog.
8. Home, Library, mpv playback, subtitles, Neon membership, Cloud authentication, poster matching, addon catalog and public releases are unchanged.

## Tests and limitations

- `tool/verify-r5-p45-localized-content.ps1` checks the localization binding, original API parser, authentication reuse, lifecycle cancellation, cache/fallback rules and language switch hooks.
- Earlier P4.1–P4.4 source checks and Windows Release x64 still run in one private workflow.
- On-device Windows verification: logged out / logged in; Turkish overview present / absent; English selection; language switched while lookup pending; mismatch title/year; add-on English text fallback; rapid item switching; cloud sign-in retry.
- **This is a safe client-side preference, not a server-side locale guarantee.** True deterministic `tr-TR` responses require a verified server contract exposing language preference and confirmed language metadata. Do not silently add unsupported `lang` parameters to a production endpoint.
- The English fallback is not machine-translated; there is no new translation provider.

## Continue

If server exposes verified `language`/localized `overview`, extend `IMediaMetadataService` with an explicit language-aware contract and per-language cache. Only then expand the same logic to Home/Library previews. Do not publish to the public repo without explicit user approval.
