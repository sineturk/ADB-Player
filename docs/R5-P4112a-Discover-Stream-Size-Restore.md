# R5/P4.11.2a — Restore Download Sizes in Discover Stream Picker

Private branch: `feature/v1.0.2-r5-p4112a-discover-stream-size`  
Based on successful R5/P4.11.2 Smart Torrent Handoff, commit `2c39e15376bae5561d7df346746ca26a971558f5`.

## Issue

Discover's cinematic detail modal used `ComboBox.DisplayMemberPath="Name"` for `Addons.Streams`. This hid existing `CatalogStream.SizeBytes` and `SizeLabel` values from both Prowlarr/Torznab and Stremio providers, preventing size-based source selection. Data mapping for Prowlarr was already correct.

## Changes

- Replace the one-field ComboBox with a two-column stream template: title/source at left, **size at right**, seed count underneath.
- The same template renders in the collapsed selection, so size remains visible before pressing Play.
- Show the selected stream's existing `DetailLabel` (quality, size, seeders) underneath for additional clarity.
- If provider does not supply a valid size, display a concise localized **Size unavailable / Boyut belirtilmemiş** label; do not synthesize or estimate bytes.
- Stremio add-ons now also read numeric `behaviorHints.videoSize` and top-level `size` bytes when supplied; retain regex fallback for human-readable titles, such as `5.3 GB`.
- Preserve stream ordering and exact selected `CatalogStream` objects, provider URL/infohash, Prowlarr/Torznab size mapping and Play action.
- Verify XAML, Turkish and English resources, Stremio metadata parsing, and streaming/Prowlarr regression contracts through private CI.

## Windows checks

- [ ] Open Keşfet → a movie/episode → source dropdown with Stremio and Prowlarr results.
- [ ] Each known-size result shows size (e.g. `1.6 GB`, `9.8 GB`) right aligned, without relying on the release name.
- [ ] Selected result shows size when dropdown is closed and selected detail beneath the selector.
- [ ] Unknown-size results display `Boyut belirtilmemiş` in Turkish / `Size unavailable` in English.
- [ ] Both short multiline Stremio titles and long release names remain usable at common Windows sizes.
- [ ] Stream Play still opens the exact selected source.
- [ ] New Prowlarr/Torznab search results still keep original `SizeBytes`.
- [ ] Existing Torrent, Smart Handoff and cloud functionality unchanged.
- [ ] No public `v1.0.1` release changes.

CI validates source/build. Screenshots and runtime source behavior still require Windows visual verification.
