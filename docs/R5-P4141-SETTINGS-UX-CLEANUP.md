# R5/P4.14.1 — Settings UX Cleanup and TR/EN Localization

## Scope
Windows ADB Player v1.0.2 R5. Base: `feature/v1.0.2-r5-p414-telegram-cache-management`. Public branch and release are unchanged.

## Root cause / resolution
The previous SettingsView used **two advanced Expanders on `Grid.Row="6"`**: optional services and cloud credentials. Their headers overlapped in the lower viewport.

Changed to a vertically measured `ScrollViewer / StackPanel` layout with separate sections, so panels no longer compete for the same grid row. Section order:
1. Account & sync (ADB Cloud preferences)
2. Connections (Telegram and AltyazıDB, with collapsed optional credentials)
3. Storage (torrent download folder, Telegram cache and usage tiles)
4. Security
5. Advanced settings (optional services, user-provided cloud app credentials)

Status and action buttons are vertically separated in storage cards to improve readability. Existing save, credential, folder-selection and playback actions keep their bindings and handlers.

## Internationalization
- Labels, help copy and tooltip text use `DynamicResource` keys from `Strings.tr-TR.xaml` and `Strings.en-US.xaml`.
- Telegram cache size, archive scratch, protected account data, idle/clean status, cleanup confirmation and folder-switch text use localized resource keys, rather than Turkish C# literals.
- `TelegramPanelViewModel.RefreshLocalization` raises property changes for live usage and state text.
- `MainViewModel.RefreshLocalization` now updates the torrent storage free-space text.

## Safety / feature isolation
- No changes to verified P4.13j Store direct Range, multipart ZIP/7z, or Telegram playback/readers.
- No changes to P4.14 TDLib cache cleanup semantics and safety barriers.
- Telegram API credentials, cloud secrets, and user-specific paths are **not** stored in this document.
- `tool/verify-r5-p4141-settings-ux-i18n.ps1` checks XAML structure, no duplicate grid row, key parity, refresh notifications and protected Store code.
- Full Windows x64 CI retains previous smoke tests.

## Manual acceptance checklist
- At both 100% and 150% Windows display scaling, Settings sections scroll without collision or clipped footer.
- Switch Turkish -> English -> Turkish without restart: headings, cache size/status, confirmation dialog and free-space labels change with the language.
- Expand the optional Telegram and AltyazıDB credentials and verify existing values/secure storage are intact.
- Check the Telegram cache size and cleanup controls when playback is idle; cleanup is blocked while playing.
- Confirm P4.13j 7z Store playback and a mid-video seek still work immediately.

The CI status is reported separately in the GitHub Actions run for the final commit; manual UI validation is not implied by CI success.
