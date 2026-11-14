# R5/P4.15.2 — P5 Telegram Hub

Base P4 commit: `9e313495`. Branch: `feature/v1.0.2-r5-p4152-telegram-ux-p5-hub`.

## Intent and truthful data sources

A dedicated Hub (route 13) is reachable from the sidebar, Home and Telegram. The media page remains channel-focused. A Hub never scans the entire Telegram account.

- **Yıldızlılar / Starred:** persisted TelegramFavoriteChatIds, displayed through currently known/accessible Telegram.FavoriteChats, and opened via OpenFavoriteChatAsync.
- **Son açılanlar / Recently opened:** the existing persisted RecentSources (up to 15 globally), filtered exclusively to stable `telegram:` or `telegram-archive:` sources. This is *actual opened history*, not files seen during browse.
- **İzlemeye devam et / Continue watching:** the existing persisted resume store, only unfinished stable Telegram playback sources with valid position/duration, opened through PlayContinueWatchingAsync to re-create the TDLib stream.
- **Bu oturumda görülenler / Seen this session:** existing Telegram.RecentMedia (up to 30 files enumerated from chats visited during this session); clearly session-scoped and never described as full account history.

Show only a sign-in prompt while disconnected. The underlying source data is device-local and not segregated per Telegram account. Switching Telegram accounts requires a specific manual acceptance check before public release.

The UI shares existing commands and is read-only until the user clicks an item. No modifications to ZIP/7z grouping, Range seek, subtitle synchronization, cache export, TDLib or the playback engine.

## Offline validation

New pure Core policy and synthetic smoke test, XAML/source contract verifier, TR/EN resources, P1–P4 regressions, Windows Release x64 CI. **No live Telegram login, downloading, seek or media playback before the P5 development/CI gate is complete.**

## Post-P5 combined acceptance checklist

1. DPI 100/125/150 and wide/narrow responsive layouts; keyboard focus.
2. Star/unstar, saved favorite IDs and access after restart.
3. Actually opened media vs merely browsed media; persistence and limits after restart.
4. Real Telegram MKV and split ZIP/7z playback, 20-minute seek, stopping and resuming transfers, missing/unverified parts.
5. Resume across app restart using stable IDs, global search and in-chat parity.
6. TR/EN switching and account switching: ensure no previous account's chat data or playback metadata is exposed.

Public Windows release and Android are unchanged.
