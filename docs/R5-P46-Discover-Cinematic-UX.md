# ADB Player R5/P4.6 — Discover Cinematic UX Refinement

**Branch:** `feature/v1.0.2-r5-p46-discover-cinematic`  
**Base:** `16291c090f2d37d74c5fca24869c27dc95249405` (R5/P4.5)

## Goals

- Keep existing real-addon catalog, localized AltyazıDB overview, search/filter and current featured item selection.
- Make the featured backdrop more legible with a 228px compact spotlight and up to 3 synopsis lines, preserving Read more / Show less.
- Replace four oversized statistics cards with live movie / series / anime counts in the catalog header. The filtered result count stays available.
- Increase catalog density with 154px items and proportionate 140x196px artwork, preserving selected-card outlines and accessibility.
- Preserve a complete scrollable Turkish/English overview. Avoid repeating the selected title in the right-hand header.
- Place episode and playback-source selection above Play and Cloud saving actions, without changing command bindings or stream search logic.
- Localize common genre labels only at the visual binding layer; preserve raw addon metadata, filters and origin names.
- Force genre presentation refresh upon language change without rewriting metadata or restarting stream discovery.
- No changes to player shell/HUD, API credentials, authentication, ADB Cloud state, public v1.0.1 or platform playback.

## Windows validation

- [ ] R5/P4.1–P4.6 PowerShell checks and Release x64 GitHub Actions success.
- [ ] Turkish UI: Featured Nocturne synopsis in compact 3-line space, Read more / Show less returns full/compact text.
- [ ] Details shows full synopsis and clean title hierarchy; playable sources appear above Play and Cloud save options.
- [ ] Selecting Wicker / another title retains episodes/sources and Play behavior.
- [ ] Favorites, Watchlist and custom list actions still work.
- [ ] Search term, filters, refresh, and lazy-loaded poster cards behave normally.
- [ ] Genres translate in Turkish (e.g. Crime / Drama / Thriller → Suç / Dram / Gerilim) and return to unchanged English after switching language.
- [ ] 850px and 600px windows stack details without hiding controls; keyboard focus/escape and 125%/150% DPI work.
- [ ] Art and posters do not stretch when expanded.
- [ ] Visual test must be performed on an actual Windows machine; CI only validates source/build.

**Private test build only; do not merge or create a public release without approval.**
