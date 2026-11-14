# ADB Player Windows v1.0.2 — R5/P4.1 Discover Cinematic Refresh

**Source branch:** `feature/v1.0.2-r5-p41-discover-cinematic`  
**Base:** private P3.5 commit `d702a9478f93dd931977a919baa5b2545af013e5`  
**Public releases:** unchanged.

## Scope implemented

- Home-aligned cyan-accent glass hero; no unrelated onboarding, profile, login, or membership changes.
- Modern single-row search / category filter / refresh with clear-search action, existing `Addons.SearchText`, `SelectedFilter` and `RefreshCommand`.
- Real, live **filtered matches** and unfiltered movie / series / anime totals; no invented numbers. Uses the existing `AddonsPanelViewModel` and catalog data.
- Glass catalog panel; section header with current filtered result count and unchanged addon manager action.
- Responsive 164px poster cards with genre/year metadata, localized movie/series/anime badge and safe empty-artwork icon (never inserts a guessed poster).
- Selected and keyboard focus states; restrained 145ms mouse hover micro-animation disabled for Windows high contrast/reduced-motion preferences.
- Loading and no-content states separated with existing `IsBusy` and `HasItems` bindings.
- Narrow widths stack the detail viewer **below** the bounded catalog, rather than squeezing a 440px detail column alongside posters.
- Original 30-item incremental catalog presentation, selection, episode/stream choice, favorites, watchlist, Cloud lists and playback commands preserved.
- Turkish and English UI labels added.

## Deferred combined QA — do not run during P4.1 or P4.2

User decision: complete **P4.1 Discover**, **P4.2 Library**, **P4.3 List**, then run one consolidated private Windows Release x64 CI and interactive acceptance.

After P4.3, validate:

- [ ] XML/XAML and localized resource dictionaries parse; every named event handler and StaticResource exists.
- [ ] .NET 10 Windows Release x64, libmpv package, SHA-256, prior R4/R5 regression guards pass.
- [ ] 1500×860 / 1180×600 / narrow window: hero readable; category filter and refresh stay accessible; no horizontal clipping.
- [ ] Detail pane selected with mouse and keyboard: alongside at wide widths; stacked underneath at narrow widths; close returns focus to catalog.
- [ ] Search clear/reset; category switching; results counter equals filtered items; film/series/anime counters remain sourced from full catalog.
- [ ] Incremental catalog scrolling after 30 items retains references and selection; no redundant stream searches.
- [ ] Real posters display correctly; missing artwork shows the icon, not unrelated metadata.
- [ ] Desktop 125%/150% DPI, Tab/Enter/Space/Escape and reduced-motion/high-contrast focus states.
- [ ] Addon manager, episode chooser, stream playback and Cloud favorite/watchlist/add-to-list remain functional.
- [ ] Loading indication appears only during actual initial catalog loading and empty state when no items remain.
- [ ] No changes to player HWND, Mini Player, subtitle/audio timing, local source storage, Neon Auth or Membership/Premium.

## Continue development

Next: **R5/P4.2 — Library Refresh** on a new branch from P4.1. Then **R5/P4.3 — List Refresh**. Keep P4 branch CI trigger disabled until P4.3 is complete; use a final combined private validation workflow and artifact. Do **not** merge to public `sineturk/ADB-Player` without explicit user approval.
