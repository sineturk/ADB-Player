# R5/P4.16.8.1 — RC2 embedded track export (local only)

## Release scope
- Audio is copied from embedded tracks and text subtitles are converted to SRT **only when the user opens a local media file**.
- Direct extraction from Telegram, Google Drive, WebDAV, cloud streaming, HTTP URLs, and UNC file shares is disabled.
- A media file downloaded to the local disk may be reopened as a local file and exported.
- PGS/VobSub image subtitles need OCR; they cannot be directly converted to SRT.
- Reject empty, malformed-timing, timing-only and markup-only SRT. Remove temporary output on errors. Never overwrite an existing destination.
- Remote playback, OAuth/session credentials, cache management and network streaming are not modified.

## Automated gates
- \`tool/verify-r5-p416-home-shared-drive.ps1\` guards the disabled remote feature and local-only restrictions.
- \`dotnet run --project tool/embedded-track-export-smoke/EmbeddedTrackExportSmoke.csproj --configuration Release\` exercises valid/empty/invalid SRT fixtures, codecs and remote URL rejection.
- The Windows x64 CI pipeline builds the RC2 branch.

## Manual RC2 tests (required before public release)
1. Open a local MKV with text subtitles; export a SRT and check its timestamps and text.
2. Export a local embedded audio track and verify its content plays.
3. Select PGS/VobSub subtitles: verify the OCR limitation is shown.
4. Open a Telegram and a Google Drive stream: export commands must show the local-only warning and create no output file.
5. Download a remote file, reopen the downloaded local file and export from there.
6. Confirm failed/cancelled exports leave no \`.partial\` file and never overwrite existing files.

**CI success does not replace local Windows live tests or authorize publication of v1.0.2.**
