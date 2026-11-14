# R5/P4.10 — General Turkish / English Localization Audit

Private branch: `feature/v1.0.2-r5-p410-localization-audit`  
Based on R5/P4.9 at `a17981d6f1c5097f88df820c93b6cfa0989bbfec`.

## Audit results

- Turkish and English localization dictionaries each started with **1,091 matching string keys** (no missing keys).
- Missing translations came predominantly from **hardcoded C# status/error strings**, not missing resource keys.
- `AppDialog` previously used hardcoded Turkish **error/confirmation/warning headings and Yes/No/OK buttons**, even in English.
- `SourcesView` displayed `CloudConnectErrorText` and `OAuthBrowserStatusText` with raw bindings instead of the existing user-facing translation converter.
- The Add-ons and Library detail pages had unconverted status labels; legacy shell still contained unconverted status and warning text.
- pCloud Europe/US card titles were Turkish hardcoded rather than dynamic resources.
- The old language switch swapped `DynamicResource` dictionaries, but did not necessarily reevaluate converters for status values assigned earlier.

## Fixes

1. Add localized dialog headings and buttons, including the close tooltip.
2. Add more than 100 explicit user-facing translations and new patterns for cloud errors, OAuth diagnostics, Telegram, add-ons, playback, subtitle/audio/torrent and other common status messages.
3. Use paired resource-dictionary values to translate existing UI texts captured **before switching language**, in either direction. Resource keys stay aligned.
4. After changing the language, refresh WPF TextBlock/ContentControl binding targets so localized status converters rerun without restarting the application.
5. Translate cloud sign-in diagnostics and OAuth browser status directly in the live modal; re-localize its title/description if the language changes while the modal is open.
6. Translate pCloud card titles and accessibility labels with `DynamicResource`.
7. For a **previously unknown, strongly Turkish failure message** returned by a backend, display a short English fallback instead of leaking raw Turkish error text or implementation details. This fallback is only for detected failure messages, not arbitrary content titles or ordinary success messages.
8. Keep original exception logging, secret redaction, OAuth, server connections and playback unchanged.

## Limits / follow-up

- This is a static source audit and regression fix, not proof that every provider/backend-supplied or third-party error has a natural English translation. Unknown backend errors deliberately use a short localized fallback.
- Long technical diagnostics in logs or advanced audio-sync internals are not translated wholesale.
- Windows visual acceptance remains necessary at runtime, including changing the language while a source modal is open.

## Acceptance checklist

- [ ] Private Windows Release x64 CI and source guard pass.
- [ ] English: show an error dialog. Heading, description and OK button are English.
- [ ] English: show confirmation dialog. Heading, Yes/No buttons and text are English.
- [ ] Cloud: invoke unavailable OAuth or invalid Client ID/Secret; the visible error is English.
- [ ] English: open pCloud Europe/US, check card names and modal titles.
- [ ] English: trigger invalid link, Telegram verification, add-on, Library/detail and connection errors.
- [ ] With a status visible, change Turkish → English → Turkish and check the text updates without navigating away.
- [ ] Back to Turkish: all headings, controls and statuses return to Turkish.
- [ ] Saved profiles, encrypted OAuth secrets and media playback still work.

Public v1.0.1 is unchanged.
