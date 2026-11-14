# ADB Player Windows v1.0.2 — R5/P4.2 Cinematic Library Refresh

**Private development branch:** `feature/v1.0.2-r5-p42-library-cinematic`  
**Base:** `feature/v1.0.2-r5-p41-discover-cinematic`, commit `ff2fa61fa844a9bdcd455819c1c001d5a731e6c5`  
**Scope:** Library UI/UX, responsive presentation and localization only. **No changes to scan implementation, media database, history, local files, cloud auth, Premium/Membership, playback backend or public release.**

## Implemented

1. **Cinematic Library hero** based on Home's R5 glass palette: cyan top line, large title, concise help, true current media/folder counters, decorative rings and a separate existing `ShowCloudListsCommand` action.
2. **Media folders workspace**: glass panel, recognizable folder glyphs, accessible selected/keyboard-focus states, source path tooltip, existing `SelectedFolder` binding and unmodified Add / Scan all / Remove actions.
3. **Library toolbar**: cinematic search icon, visible placeholder, clear-search action, preserved smart-query backend, existing type filters, sort choices, list/poster commands and refresh command. Scan selected / open library data / scope chooser are retained in a compact secondary rail.
4. **Active view selection**: list/poster buttons now show a clearly selected cyan state rather than merely disabling the active view button.
5. **Media list and poster cards**: consistent glass card surfaces, stronger active/focus outlines, type badges on posters, year/size/title/progress/season metadata still sourced from existing `LibraryMediaItem` / `LibraryGridItem`. Artwork failure still shows the existing safe placeholder icon.
6. **Movie & series details**: existing metadata, episodes, streams and play controls preserved; glass/soft stroke visual polish. Wide windows use the existing right-side detail dock; narrower windows float selected details above the catalog instead of shrinking the visible poster grid.
7. **Responsive behavior**: two/three-row search/filter toolbar at 1220/930px, adaptive folder pane width, smaller hero layout, reduced-or-disabled ornamental artwork, and an accessible fallback to existing media list/grid view.
8. **Motion and accessibility**: restrained poster hover (145ms), automatically disabled under Windows high-contrast or reduced-motion preferences. Keyboard focus is emphasized on folders and media cards. No fake media, separate data copies or manual metadata mutations.
9. **States and actions**: scanning/loading and empty results are visually differentiated; if there are no folders the existing Add Folder action remains visible, while with connected folders the user can reset search/type/scope filters. Lower selected-item playback bar is refreshed without changing the `PlaySelectedCommand`.
10. **Localization**: new R5 Library labels in Turkish and English using resource dictionaries.

## Critical untouched contracts

- `LibraryPanelViewModel` and `SqliteMediaLibrary` code and schema are unchanged.
- `AddFolderCommand`, `ScanSelectedCommand`, `ScanAllCommand`, `RemoveFolderCommand`, `OpenDatabaseFolderCommand`, `RefreshCommand`, `ShowGridViewCommand`, `ShowListViewCommand`, `PlaySelectedCommand`, `PlaySeriesNextCommand`, `PlaySeriesEpisodeCommand`, `ShowCloudListsCommand`, selection, filtering, and sorting remain connected to the same ViewModel.
- Library file paths and local metadata remain in place. There is no remote authorization, cache purge, profile migration or disk deletion.
- Existing lazily loaded artwork and scroll handlers are retained.
- No Windows binary has been built or tested for P4.1/P4.2, by user decision. Both remain implementation-only until the unified P4 test phase.

## Deferred combined Windows QA, after P4.3

- [ ] Parse XAML and locale dictionaries; check named event handlers, StaticResource names and code-behind compilation.
- [ ] Windows .NET 10 Release x64; sha256/libmpv; all prior R4/R5 regression guards and private smoke checks.
- [ ] Layout at 1500×900, 1200×800, 1000×700, narrow 800×600 and 125%/150% DPI: search and options remain accessible, folder list and panels do not cover action buttons.
- [ ] Add/scan/remove folder, display selected folder path and scope filter, confirm actual disk files remain intact when removing a catalog folder.
- [ ] Existing 276-video-sized libraries or larger, artwork fetching/paging, movie title and season-hub poster grouping, file size/progress information, missing artwork fallback, grid/list virtualization.
- [ ] Search smart query, year/type filter, sort choices, clear search, reset filters, folder scope, refresh and active mode indicator.
- [ ] Opening movie/series details; narrow overlay vs wide dock; episode selection and playback; keyboard Tab, Enter, Esc, high contrast and reduced-motion states.
- [ ] Empty library, empty results, busy scan states; no false empty warning during scan.
- [ ] Saved Cloud lists and playlist routing; existing Neon login/posters (P3.x); no Premium gate.

## Next development step

Start **R5/P4.3 — Cinematic List Refresh** on a fresh private branch from this P4.2 head. Only when P4.3 is finished should a combined P4 Windows CI trigger, artifact, and user acceptance test be prepared. Do not merge/publish to `sineturk/ADB-Player` without explicit user approval.
