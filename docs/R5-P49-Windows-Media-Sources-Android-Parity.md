# R5/P4.9 — Windows Media Sources: Android Parity

Private branch: `feature/v1.0.2-r5-p49-sources-android-parity`  
Base: `feature/v1.0.2-r5-p48-connections-setup-ux` (`8bd6b1c0d41153cb837c86468f4ab96770a1688e`).

## Changes

- Replace the old three-pane source manager with an Android-inspired screen: **Cloud accounts** (Google Drive, Dropbox, OneDrive, pCloud Europe, pCloud US), **Other sources** (WebDAV, Telegram, shared link), **Saved sources** underneath.
- Provider cards use short localized labels with icon and concise subtitle; no technical onboarding prose on the gallery.
- Remove the permanently visible public-link form and large privacy panel. Link Open/Save appears only in the source overlay after selecting a shared-link card.
- Reuse existing provider-specific OAuth/WebDAV modal and secure browser flows. Keep advanced OAuth secrets collapsed and preserve encrypted persistence.
- Telegram card routes to existing Telegram page rather than introducing a second credential form.
- Saved accounts and WebDAV/links retain original source objects, open/remove handlers, tabs and search, but search moves into a collapsed expander.
- Narrow layout adapts number of provider cards to available width. English/Turkish copy is short.
- When a link is successfully added, the shared-link overlay closes; failed validation keeps the draft visible.
- No changes to rclone service, Google OAuth, secure WebDAV storage, player or public `v1.0.1`.

## Windows acceptance

- [ ] All R5/P4.1–P4.9 source checks and Windows Release x64 build pass.
- [ ] Main Sources page shows two provider groups and saved-account section; no permanent quick-link/security forms or full-width search field.
- [ ] Each cloud card opens the correct existing auth dialog, with small optional account label and advanced fields collapsed.
- [ ] WebDAV opens its own address/username/password dialog; secret storage and validation unchanged.
- [ ] Telegram card opens existing Telegram page, not a fake cloud-login modal.
- [ ] Shared-link card opens a focused URL/name dialog. Open and Save commands still work; successful save adds a row.
- [ ] Saved Google Drive / pCloud / Dropbox accounts, shared links and WebDAV profiles open/remove correctly and remain after restart.
- [ ] Search expander filters saved accounts and links without modifying records.
- [ ] Layout passes approximately 600px, 850px, 1280px windows and 100/125/150% DPI; tab/escape and keyboard focus remain usable.
- [ ] Switch Turkish/English and check for truncation of labels; connection APIs unchanged.

**Validation note:** CI verifies source and build. Actual login, playback and visual behavior still need Windows acceptance. Private test only.
