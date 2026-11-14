# ADB Player Windows v1.0.2 — R5/P4.4 Inner Page Hero Reduction

**Private branch:** `feature/v1.0.2-r5-p44-inner-page-refocus`
**Base:** `feature/v1.0.2-r5-p43-list-cinematic`, commit `873bd63ef28758516102a4b3a3a6f05bc16ebacb`
**Scope:** Presentation-first changes to **Discover / Library / Playlist**; Home and Mini Player unchanged.

## Discover: real media spotlight replaces decorative hero

- Replaced the former "Yeni dünyalar keşfedin" decorative top panel with an actual `CatalogItem` read from `AddonsPanelViewModel.Items`.
- Spotlight shows only source-provided title, year, genres, synopsis and poster/background URLs; no fabricated TMDB data, no unverified ratings and no added API key.
- Prefer a provider's `OriginCatalogId` containing **popular**, **trending** or **top** when such a catalog item has artwork. Otherwise use the first suitable illustrated catalog item, labeled simply **Kataloğundan Öne Çıkan**, not falsely represented as ranked popularity.
- Keeps the featured item stable while subsequent catalog batches load. Refreshes selection when filters replace catalog results. Hides spotlight when no items are available.
- **Detayları aç** sets the original `Addons.SelectedItem` and reveals the existing details panel with its established stream/episode actions; it does not try to auto-play a stream that has not yet been selected.
- Compact screen: hide side poster and reduce copy density. No duplicate catalog request or extra backend call.

## Library: compact operational header

- Replaced 178px decorative top hero with a **94px operational summary**, using live media/folder counts.
- Desktop quick actions: Add folder / Scan all / My lists, wired to existing commands. On narrower windows these duplicates hide; underlying left-side Add/Scan controls remain.
- Existing sidebar, media cards, search/filters, episode details and bottom selected play controls are unchanged.
- No new database fields, no made-up last-scan times, no disk operations beyond existing commands.

## Playlist: remove decorative hero completely

- Entire large hero removed. Playback queue fills the available space from the top.
- Queue title, live count and `AutoPlayNext` toggle share the functional queue header. At smaller widths, the toggle/count group wraps to its own row.
- Existing Cloud playlist status, load/sync actions, queue cards and footer Play/Remove/Clear actions preserved.

## Combined private QA

- [ ] XML/XAML resources and event handlers compile with .NET 10 Windows x64 Release; existing R4/R5 source checks plus `tool/verify-r5-p44-inner-page-refocus.ps1` pass.
- [ ] Keşfet: a real addon popular/trending content is promoted when available; when not, an actual catalog item is selected without claiming ranking.
- [ ] Empty catalog shows search and no-content state without an empty featured panel.
- [ ] Featured background/poster failures gracefully leave a readable gradient/fallback, no broken layout.
- [ ] Featured Details opens original episode/stream picker; no accidental playback or repeated network requests.
- [ ] Kütüphane 1500px and 900px: compact header remains readable, at least one row of additional posters visible, Add/Scan and folder selection operational.
- [ ] Liste 1500px and 900px: no decorative hero, correct auto-play toggle, cloud sync and queue controls; more queued items visible.
- [ ] Cross-check Home's "Kaldığın Yerden" hero remains unchanged.
- [ ] At 800–1500px widths and 125%/150% DPI, no clipped controls or inaccessible keyboard focus.
- [ ] Windows interactive playback, remote OAuth, cloud token refresh, real screenshots and actual provider ranking still require on-device user testing.

## Release posture

This remains **private v1.0.2 development**. Public `sineturk/ADB-Player` v1.0.1 unchanged. Do not publish or merge without explicit approval.
