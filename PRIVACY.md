# ADB Player Privacy — V1.0

This document describes the privacy boundaries implemented by the ADB Player V1.0 source.

## Local-first behavior

ADB Player can be used for local playback without an ADB Cloud account. Local application state, logs and diagnostics remain on the user's device unless the user explicitly shares them.

ADB Player V1.0 does not contain an analytics or telemetry SDK that automatically uploads playback or diagnostic logs.

## Optional ADB Cloud synchronization

When a user signs in to ADB Cloud, the account synchronization service may store and synchronize:

- profile information and locale;
- registered device identity/display information and app version;
- media identity and basic library metadata;
- watch position, duration and completion state;
- favorites, watchlist and custom collections;
- cross-device playback queue state;
- portable player preferences such as language, seek intervals and subtitle presentation preferences;
- add-on identifiers/enabled state and, for V0.7+ profiles, the add-on manifest profile needed to restore an installation.

ADB Cloud uses account-scoped access controls. Add-on manifest profiles may contain configuration tokens; the service stores those profiles encrypted with AES-GCM before writing them to the database. This encryption is service-side protection and is **not end-to-end encryption**: the authenticated ADB Cloud function can decrypt the profile in order to restore it to the user's device.

## Data intentionally kept device-local

The seamless restore design does not synchronize machine-bound or credential-bearing state such as:

- local filesystem paths and local NAS paths;
- WebDAV credentials;
- rclone/OAuth credentials;
- Telegram application/session credentials;
- API credentials;
- signed media URLs and private HTTP headers;
- audio-device calibration;
- GPU/hardware-decoding selection;
- torrent runtime/cache state.

Hydrated add-on manifest URLs are stored on Windows through the local secret store/DPAPI path used by ADB Player.

## Third-party services

ADB Player only contacts third-party services when a feature that uses them is configured or invoked. Examples include cloud storage providers, Telegram, user-configured WebDAV/NAS services, add-ons, indexers, subtitle services and update hosting.

Those services have their own privacy terms and data practices.

## Logs and diagnostics

Application logs and generated crash/diagnostic bundles are local files. ADB Player does not automatically upload those files. If a user chooses to attach diagnostics to a support request, the user controls that disclosure.

## Account and data deletion

For ADB Cloud account or cloud-data deletion requests, contact **destek@altyazidb.com** or use the AltyazıDB private contact form at **https://altyazidb.com/index.php?do=feedback**.

Do not post account email addresses, access tokens or other credentials in a public GitHub issue. Support may request enough account information to locate the account, but users should never send passwords, access tokens, API keys or third-party credentials.

## Changes

Privacy behavior may evolve in later versions. Material changes should be documented in the release notes and this file before publication.
