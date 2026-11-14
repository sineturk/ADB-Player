# R5/P4.15.2 — P2 Telegram Desktop-style Chat List

**Base:** P1 Home/Telegram information architecture (CI PASS, `f2748abe`).
**Branch:** `feature/v1.0.2-r5-p4152-telegram-ux-p2-chat-list`
**Status:** Development stage P2. **Live Telegram / playback acceptance is deferred until after P5.**

## UX scope

The middle Telegram panel uses a more familiar, compact Telegram Desktop-style list while keeping ADB Player's cinematic colors.

- **Search:** instant, case-insensitive local filtering over the currently loaded folder's chat titles. Search box has a magnifier, a clear action and Ctrl+F / Escape shortcuts. It never triggers message-history scans.
- **Exclusive filter chips:** All / Starred / Channels / Groups / Private. These operate on the currently loaded chat folder. Starred is the existing locally persisted list of chat IDs; the old `FavoritesOnly` VM property remains an alias for compatibility.
- **Source classification:** TDLib `chatTypeSupergroup.is_channel` distinguishes channels from supergroups. Basic groups and non-channel supergroups are Groups; private/secret conversations appear as Private. Forum topics remain inside their group; a separate **forum-only filter is intentionally deferred** until reliably available forum metadata is indexed without calling `getSupergroup` for hundreds of chats on every navigation.
- **Rows:** smaller circular initials avatar, clearly truncated title, localized chat type, unread count capped at 999+, and a less prominent star action, filled when saved. Hover/focus/selected states are independent of global ListBox styling.
- **Selection safety:** filtering a chat out of the list does not write null into `SelectedChat`, clear media, or restart playback. ListBox selection is OneWay; only a visible selected row explicitly updates the VM. The existing incremental `FilteredChats` reconciliation is preserved.
- **Favorites:** `TelegramChatItem.IsFavorite` now emits property-change notifications, so star icons update without relying on reloading the chat list.
- **Accessibility:** filter RadioButtons are keyboard focusable, active chip is indicated through color and checked state, focus outline is visible, search has automation label, mouse and keyboard navigation both work. Chat list remains virtualized for large folders.
- **Language:** all visible filter, search, chat-kind, unread summary, and empty-state descriptions have Turkish and English resources. Changing the application language refreshes visible result counts.

## Boundaries & test policy

- No layout change to the right-hand media pane or left-hand folder/sidebar in P2.
- No background Telegram discovery, no new chat-history requests, no new settings tables, no new media/cache/7z Store code.
- Existing P4.13j direct Range, P4.15 Continue Watching, subtitle timing, and cursor changes remain untouched.
- P2 verification is **offline only:** XML/XAML resource validity, 5-chip binding checks, chat source type check, selection safety, Windows Release x64 compile and previous fixture-based regressions. No live TDLib login/playback/seek/UI acceptance now.
- **After P5:** run a single integrated live Windows pass, including channel classification, star/unstarring, active chat switching with filters, forum navigation, library/Store seeking and TR/EN visual checks.

## Next

**P3:** Media list refresh (compact file rows, status tags, selected file action consistency).

**P4:** Unified spacing, responsive Telegram Desktop density, hover/motion and keyboard refinements.

**P5:** Telegram Hub — intentional persisted starred/recent/resume experience and final combined manual test gate.
