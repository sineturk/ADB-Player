# ADB Player Windows v1.0.2 — R5/P4.5 Featured Summary Clipping Fix

**Branch:** `feature/v1.0.2-r5-p45-overview-expand`  
**Base:** P4.5 Localized Content Metadata successful private build `396eff1bfc6809179681c1fe9a66fc0440493bc3`

## Runtime finding

A real Turkish AltyazıDB summary was successfully returned for **Nocturne**. The Discover spotlight still showed only its first ~2 lines because `DiscoverFeaturedDescription` specified `MaxHeight="36"` with `TextTrimming="CharacterEllipsis"` and the entire spotlight was fixed at `Height="194"`. The right-side details area also truncated the synopsis at 95px.

**This is a presentation bug, not an API language or login problem.**

## Changes

- Keep the Home-compatible compact 194px featured banner by default.
- For longer descriptions, render **Devamını oku / Read more** next to **Detayları aç**. Toggling **Daha az göster / Show less** collapses the banner again.
- Expanding removes the text's maximum-height and trimming constraints and lets the banner grow naturally within the existing vertically scrollable Discover page, without silently truncating the full summary.
- At narrow widths, expansion also unhides text previously omitted from the compact teaser.
- Image backdrop remains top-cropped at 194px and the poster remains fixed at 112x162px instead of stretching to an unnatural height as the banner grows.
- The existing selected-content details panel now shows the complete `Addons.SelectedItemOverview` in its own vertical scroll viewer, without the obsolete 95px cutoff.
- Live translation, language switch, cache, metadata verification, cancellation, Details navigation, add-on catalog, search and streaming code all remain unchanged.
- Both interface languages get localized expand/collapse button labels and accessible automation name.

## QA

- [ ] All R5/P4.1–P4.5 guards, XAML/XML validation and Windows Release x64 CI pass.
- [ ] With Turkish UI and signed-in Cloud account, Nocturne summary is initially short; **Devamını oku** reveals complete Turkish text.
- [ ] **Daha az göster** returns to compact banner and does not scroll to another item.
- [ ] **Detayları aç** shows complete summary inside the existing scrollable details panel.
- [ ] Switching UI to English and back retains actual source metadata and correct button labels.
- [ ] Narrow widths (850/600px), 125%/150% DPI; no stretched poster, clipped buttons or lost keyboard focus.
- [ ] Short summaries need no unnecessary expand link; empty fallback is safe.
- [ ] No changes to local/cloud media, playback history, Neon login, passwords, OAuth or Membership.
- [ ] A real Windows visual/device test is required; CI source/build success alone cannot verify exact line wrapping.

**No public GitHub release or merge until separately approved.**
