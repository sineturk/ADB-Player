# R5/P4.15.2 — P4 Telegram Desktop Visual Polish

**Source:** P3 media list branch `feature/v1.0.2-r5-p4152-telegram-ux-p3-media-list`, validated at `036f9c06`.
**Branch:** `feature/v1.0.2-r5-p4152-telegram-ux-p4-visual-polish`.
**Test policy:** static XAML/source checks and CI Windows Release x64 build only. **No live Telegram, actual downloads, seek, or video playback tests before P5 is completed.**

## Visual design

- Flat, lightly bordered Telegram-only surfaces; reduced outer margins and panel gutters, compact folder, chat and media rows.
- Selected, hover and keyboard-focus styles remain separate in chat, folder and media lists; the application-wide cinematic theme is not replaced.
- The persistent P3 selected-media deck remains underneath the current chat's media; per-row playback or download buttons are not reintroduced.
- Starred-chat expander defaults collapsed in the left navigation; starred chat filtering stays in the chat list.
- No standalone recent-media feed in Telegram; the P1 Home quick-access surface retains that responsibility.

## Responsive behavior (device-independent WPF pixels)

1. **Workspace >= 1060:** folder rail 176, chat list 306, media remainder, 8px gutters.
2. **740–1059:** folder rail hidden; same `SelectedFolder` binding is available via the chat header ComboBox. Compact menu retains Logout. Chat list 306 and media remainder.
3. **Below 740:** a single pane is displayed at a time; chat pane has explicit **Open media**, media pane has **Back to chats**. Selecting a new chat opens the media pane. Ctrl+F brings the chats pane into view and focuses chat search; Escape can return from media to chats.

The layout is **pure view code-behind** and changes only WPF column sizes and visibility. The page keeps the same live ViewModel instance, existing source lists and selected media identities. Hiding a pane must not clear selections, start new Telegram requests or cancel playback. When width expands, the multi-pane view restores itself.

## Localized labels

TR/EN resources: `Telegram.Responsive.ShowMedia`, `BackToChats`, `PickFolder`, `MoreActions`. All new controls have translated labels/tooltips, and existing dynamic resources still apply.

## Regression gates

- `tool/verify-r5-p4152-p4-telegram-responsive.ps1`: XML parsing, scoped styles, adaptive named controls, code-behind handlers, selection guards, localization parity.
- P1/P2/P3 verification scripts and existing Telegram synthetic/Store/ZIP/7z fixture checks remain in CI.
- Windows x64 Release compilation required. **CI build does not establish on-screen visual acceptance.**
- P5 will add the Telegram Hub. Only after P5: a combined manual test of folder access, resizing (including narrow width), favorites, filters, file actions, real Telegram grouped archives, search vs group flow, seek, playback, and language switching.

**Release boundary:** no public main/release or Android changes.
