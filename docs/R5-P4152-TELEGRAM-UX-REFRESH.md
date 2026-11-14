# R5/P4.15.2 — Telegram UX Refresh

**Baseline:** R5/P4.15 Windows Telegram playback/favorites and P4.13j direct Store Range.
**Working branch:** `feature/v1.0.2-r5-p4152-telegram-ux-p1-information-architecture`
**Delivery strategy:** P1 → P2 → P3 → P4 → P5; **no manual/live Telegram, real-media, or end-user acceptance testing before P5 is complete.** Static source checks and Windows CI compilation are allowed at each stage.

## Product direction

Telegram Desktop-inspired interaction, but with ADB Player's cinematic dark visual language. A channel/media browser is not also a discovery dashboard. Use simple lists, unobtrusive actions, and a clear source scope.

### Information architecture (P1)

| Surface | Responsibility | Out of scope |
|---|---|---|
| **Home → Telegram Quick Access** | Bounded preview of **recently discovered files from chats visited in this app session** (4 items) and **starred chats available in current Telegram model** (5 items); shortcuts to open media or navigate to a chat | No Telegram-wide background crawling, no extra TDLib requests on Home, no pretending data persists across restart |
| **Telegram → folders/chat list** | Select a Telegram folder/chat; filter and star chats; jump between topics | No global last-seen feed above the chat's media |
| **Telegram → selected chat/topic media** | Source-specific media search, archive/file list, attachment action bar, topic navigation | No unrelated recent-media expander |
| **Future P5 Telegram Hub** | Dedicated destination or richer hub concept for starred chats, recents, continue watching, with reliable persistence/aggregation decisions | Do not ship empty placeholder navigation or invent a global index in P1 |

### P1 implementation

- Removed the `Telegram.Hub.RecentTitle` expander from the Telegram media list; this returns the entire content height to the selected chat's media list.
- Added a responsive two-column Home module **Telegram Quick Access**, with a slim, list-first recent file preview and starred chat shortcuts.
- Home subscribes to `Telegram.RecentMedia` and `Telegram.FavoriteChats` through existing ViewModel events, and unsubscribes upon unload. No independent network/indexing/persistence is created on Home.
- Home recent item actions reuse the existing `Telegram.OpenRecentMediaAsync` path; starred-chat actions navigate to Telegram and reuse `Telegram.OpenFavoriteChatAsync`.
- Added TR/EN labels and honest empty states. The current recent list is **seen during app session**, not all files added recently across Telegram.
- Responsive Home stacks its Telegram columns below 880 DIPs, like the existing source previews.
- No modifications to actual TDLib, archive detection, split ZIP/7z Store Range, resume identities, player, or cache.

### Planned subsequent stages (not implemented by P1)

- **P2 — Telegram-like chats:** compact rows, integrated search/filter chips, star button and hover/selection behavior, stable chat selection across filtering.
- **P3 — Media list:** compact file rows, file type/part states, channel-only search context, selected file actions.
- **P4 — Telegram Desktop feel:** consistent header, visual density, spacing, sizing, sidebar minimization, keyboard navigation, responsive behavior.
- **P5 — Hub:** a deliberate centralized starred/recent/continue-watching experience, with data-source and persistence semantics; final integrated regression and live manual tests happen **after** this stage.

## Verification policy

- Per-stage: static XAML/source contracts, TR/EN key parity, Windows x64 CI compilation and existing offline test fixtures. These are **not** live Telegram tests.
- **After P5:** live Telegram login/connected navigation, starred chat persistence across restart, real archive and media playback, search vs in-chat consistency, Home/Hub shortcuts, resume & seek, narrow window layout and TR/EN switches.
- Preserve all P4.13j Store fast path and P4.15 playback functionality.

**Public release:** unchanged. **Manual live test:** deliberately deferred.
