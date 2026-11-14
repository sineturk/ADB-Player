# ADB Player R5/P4.7.1 — Cinematic Modal Full-Width Layout

**Branch:** `feature/v1.0.2-r5-p471-modal-fullwidth`
**Base:** `feature/v1.0.2-r5-p47-content-detail-modal` (`440530ccc7db830cdb559a4c51c6d7513447f0ff`)

## Bug confirmed

In Windows screenshots the selected title's synopsis had a large unused left area, while stream selection and all ADB Cloud controls were squeezed into a fixed `338` px right-hand modal rail. The Cloud custom-list buttons wrapped and the right edge felt cramped.

## Changes

- Replace the modal body's left/right split with **two full-width rows**: complete translated overview, then action content.
- The first action row displays a broad episode/stream selector and a `210` px Play button side by side; below `750` px dialog width, Play shifts underneath the source selector.
- Move the existing complete ADB Cloud group beneath the playback controls at full dialog width, preserving favorites, watchlist, custom collection, and return-to-lists commands and bindings.
- Reduce hero height from `270` to `250` px on desktop, increasing the visible body area without stretching the portrait or backdrop.
- Keep the modal scroll area, X/Escape/backdrop close, keyboard focus cycle, poster catalog scroll preservation, provider stream search and content metadata intact.
- Update Turkish/English hint text to refer to controls **below** the synopsis instead of “on the right.”
- Retain existing P4.4–P4.7 validation contracts; add structural checks that Cloud is a full-width sibling of playback, not nested inside a fixed-width rail.

## Windows acceptance

- [ ] All R5/P4 guards, P4.7.1 modal structural check, Release x64 build and runtime artifact checks pass in GitHub Actions.
- [ ] Street Fighter / Wicker: complete synopsis readable in full width, no huge unused left-side area.
- [ ] Stream selector is wide enough to show meaningful release names; Play button sits beside it in normal layout.
- [ ] ADB Cloud saving controls span the body width, including favorite/watchlist/custom-list actions.
- [ ] At dialog widths below 750px, the Play button stacks below source controls and stays clickable.
- [ ] At 600/850px app width and 100/125/150% DPI, modal remains inside window and scrolls without horizontal clipping.
- [ ] A title without stream options keeps Play disabled and message visible.
- [ ] Backdrop, poster, full Turkish/English synopsis, localized genres, cloud, episode selection and actual playback still behave correctly.
- [ ] ESC/X/outside-click close returns keyboard focus and preserves catalog position.

CI passing proves only build and static contracts; actual Windows visual and playback acceptance are separate. Private test build only, do not modify public v1.0.1.

## CI delivery note

Windows Release x64 packages must be downloaded only from a successful workflow run whose head commit belongs to this branch. Earlier P4.7 builds do not contain the full-width layout.
