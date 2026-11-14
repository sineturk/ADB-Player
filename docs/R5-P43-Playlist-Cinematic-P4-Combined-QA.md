# ADB Player Windows v1.0.2 — R5/P4.3 Cinematic List Refresh

**Private branch:** `feature/v1.0.2-r5-p43-list-cinematic`  
**Based on:** P4.2 `70a8645cc17f2c09784187ac9f0da2c845d267b4`  
**Public repo/release:** unchanged, public `v1.0.1` remains untouched.

## Scope completed

- Cinematic Playlist hero: R5 glass + cyan line, decorative rings, real `Playlist.Count` and the existing `AutoPlayNext` toggle.
- Queued items: selected and keyboard-focus states, user-facing title, source category (`KindLabel`) plus host/provider (`DisplaySource`), no duplication/migration of stored playlist entries.
- Per-row **Oynat** action selects the actual `PlaylistItem` then invokes the original `MainViewModel.PlaySelectedAsync()`; double-click and keyboard Enter remain available.
- A dedicated Cloud playlist status area retains the real `CloudPlaylistStatus` and `CloudPlaylistCountText`; existing **Buluttan yükle** / **Yeniden eşitle** commands remain connected. No misleading fake "synced" badge.
- Bottom selection/action rail preserves `PlaySelectedCommand`, `RemoveSelectedCommand`, and `ClearPlaylistCommand`. Play and Remove disable when nothing is selected; empty queue hides the action rail.
- Responsive layouts for smaller widths: cloud buttons and footer actions break into a second row, hero art reduces or disappears, content remains in a virtualized scrollable ListBox.
- Scroll virtualization (Recycling), selected item, Cloud queue sync and order of playback retained; no changes to `MainViewModel` mutation and sync implementations.
- Turkish and English strings for header, accessibility and queue labels. No API credential or cloud token text added.
- Windows settings support the existing keyboard focus system; no constant looping animation or movement in queue rows.

## Unified P4 acceptance (requested after P4.1+P4.2+P4.3)

Private Actions workflow should execute:

1. `tool/verify-r5-p4-cinematic-pages.ps1`: XAML/XML, resource dictionaries, names, page handlers, ViewModel preserved bindings.
2. Preexisting R5/P0–P3.5 source regression checks.
3. .NET 10 Release x64, libmpv inclusion, hash and private downloadable artifact.
4. Interactive Windows checks (not verifiable by GitHub Actions):
   - Home, Discover, Library and Playlist styles consistent at 1500×900, 1180×720, 900×650; 125%/150% DPI.
   - Discover search, filter, poster selection, details, stream and episode; missing poster fallback and lazy catalog.
   - Library folders/scans, search/sort/filter, list/poster, grouped seasons, artwork paging, details and safe remove.
   - Queue title/type/source labels, selected row, direct Oynat, double-click/Enter, selected Oynat, Kaldır and Temizle.
   - AutoPlayNext state persists and Cloud playlist `Buluttan yükle` / `Yeniden eşitle` only when intended.
   - Signed-in Neon user, current Drive cloud remotes and their stored refresh tokens remain unchanged; P3.5 OAuth 53682 diagnosis is separate from UI.
   - No exceptions in tab switch, focus, scroll and narrow-window interactions; no rendering overlays that hide controls.

**Pass status:** code implemented; CI should be checked independently after workflow. GitHub CI cannot exercise third-party OAuth browser consent, interactive mpv playback, visual screenshots or per-device Windows virtualization. Do not claim those tests passed until verified on Windows.

## Files modified by P4.3

- `src/AltyaziDB.Player.App/Views/Pages/PlaylistView.xaml`
- `src/AltyaziDB.Player.App/Views/Pages/PlaylistView.xaml.cs`
- `src/AltyaziDB.Player.App/Themes/R2.Tokens.xaml`
- `src/AltyaziDB.Player.App/Localization/Strings.tr-TR.xaml`
- `src/AltyaziDB.Player.App/Localization/Strings.en-US.xaml`
- `tool/verify-r5-p4-cinematic-pages.ps1`

Earlier P4.1/P4.2 implementation notes: `docs/R5-P41-Discover-Cinematic-Deferred-QA.md`, `docs/R5-P42-Library-Cinematic-Deferred-QA.md`.
