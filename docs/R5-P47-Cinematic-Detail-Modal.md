# ADB Player R5/P4.7 — Netflix-Style Cinematic Detail Modal

**Private branch:** `feature/v1.0.2-r5-p47-content-detail-modal`  
**Base:** R5/P4.6.1, commit `e7637925ee1bc686a20bc8c2c8de33515228c2e1`

## User-facing changes

- Selecting a movie/series poster opens a cinematic detail overlay **inside Discover**. The 685px poster catalog no longer narrows or changes the number of columns when a detail is opened.
- Featured content's **Detayları aç / View details** opens the same dialog.
- The modal hero displays real source-supplied backdrop, selected poster, title and localized type/year/genre metadata. No made-up artwork or popularity ranking.
- The complete Turkish or original-language synopsis is readable without truncation. Hero stays visible; only the body of the modal scrolls.
- Episode choice, stream choice, Play, ADB Cloud favorites/watchlist/custom collections and back-to-lists retain their original bindings and backend behavior.
- X, Escape or a click outside the modal closes it. Keyboard Tab cycles inside the dialog. Focus is returned to the previous control and the catalog scroll offset is preserved.
- On narrow windows the synopsis and playback controls stack vertically; modal size remains bounded by the window.
- Previous R5/P4.5 summary expansion and R5/P4.6 translated genre labels continue to work.

## Windows acceptance

- [ ] P4.1–P4.7 source guards, XAML compile and Windows Release x64 CI pass.
- [ ] A poster click or Enter/Space opens the same modal. The catalog keeps full poster width behind it.
- [ ] Featured Nocturne action opens the same modal and shows full Turkish text.
- [ ] For an available title such as Wicker: episode choice, stream selector and Play start the expected media.
- [ ] For titles without streams, Play remains disabled and the empty-stream message displays.
- [ ] ADB Cloud Favorites / Watchlist / custom lists still work. No second API/metadata workflow is introduced.
- [ ] X, Escape and click-outside close the modal; Tab focus cannot escape behind the overlay.
- [ ] Modal close returns to the correct catalog scroll position and focused item.
- [ ] Layout is tested at approximately 600px, 850px and full-width at 100%, 125% and 150% DPI.
- [ ] Backdrop missing: dark fallback remains legible and no unrelated poster appears.
- [ ] Language switch English/Turkish while modal is shown does not lose item state.

**Release policy:** private test artifact only; no public v1.0.1 modification, merge or production release without separate approval.

**CI limitations:** GitHub Actions validates source contracts, XAML compilation and build artifacts. Actual UI responsiveness, focus cycling and playback require Windows visual/device testing.
