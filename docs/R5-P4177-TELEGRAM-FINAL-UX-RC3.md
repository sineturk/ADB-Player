# R5/P4.17.7 — Telegram Final UX Hotfix (Windows v1.0.2 RC3)

Scope is restricted to two reports from the Windows release candidate acceptance:
1. A valid phone number briefly displays the TDLib `authorizationStateWaitPhoneNumber` instruction after the user has already submitted it, then proceeds to code entry.
2. A verified Telegram resume item appears on the Home continue-watching rail with no resolved poster, even when the ADB Cloud session can enrich metadata for other sources.

## Fix

- The Telegram ViewModel tracks a pending phone submission. While TDLib accepts the number, the authorization label is a localized *verifying* status, the phone entry is temporarily hidden, and an actual TDLib request error restores the entry. TDLib retains full authority for verification, the code step, two-step passwords and sign-out.
- Both TR/EN give the phone entry a country-code example. No phone value, OTP, or credential is logged by this hotfix.
- Continue Watching only requests artwork for the items actually published to the Home rail *after* verified-account Telegram filtering. The ADB Cloud login gate, metadata trusted-match gate, HTTPS-only image URL requirement, remote source identity, and account-owner checks remain unchanged.
- Home hero and resume rail contain a bundled `app_icon.png` behind remote artwork, so a missing poster/failed image URL does not leave an empty rectangle. The fallback does not claim a movie match and does not contact a new provider.

## Automated regression

`tool/verify-r5-p4177-telegram-final-ux.ps1` validates the state transition, localized resource keys, XAML validity, bundled resource, account filtering and trusted metadata constraints. The Windows Release x64 CI also runs existing P4.15.4 Telegram account-scoping, Home, metadata and archive regressions.

## Human acceptance on the SAME RC3 binaries

1. Fresh Telegram authorization, with and without the leading `+` country code as appropriate: correct phone submits without a false invalid-format warning; wrong phone remains a real, actionable TDLib error; code and optional 2FA continue normally. Do not record OTPs or real numbers.
2. Log out and log back in; A → B → A account changes must never display prior account content, saved position or posters in the other account's Home rail.
3. While signed in to ADB Cloud, open one Telegram video with a metadata-recognizable release name and resume it. A trusted matching poster may appear asynchronously; unsupported/unmatched titles remain unchanged, and fallback branding appears with no fake poster.
4. Test Home in both TR/EN, with and without ADB Cloud, offline/slow artwork requests, and app restart. No cross-account title leak or blank WPF artwork area.
5. Repeat local/cloud resume, standalone MP3/FLAC/WAV, archive/7z seek and ArtCNN/Anime4K toggle smoke tests. A visible freeze/crash is a release blocker.

## Release invariant

The former RC2 candidate binaries and `RC2-FINAL-CANDIDATE-RECEIPT.json` are **not** RC3 acceptance artifacts. Source has changed. Rebuild installer and portable from the new, pinned RC3 commit, create new SHA-256 values, test that exact candidate in an isolated folder, and do not publish `sineturk/ADB-Player` until the manual upgrade and security gates pass.
