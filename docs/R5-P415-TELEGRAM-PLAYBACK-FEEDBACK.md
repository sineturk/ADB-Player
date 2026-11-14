# R5/P4.15 — Windows feedback: Telegram & Playback UX

Branch: `feature/v1.0.2-r5-p415-feedback-telegram-playback`
Base: R5/P4.14.1 Settings UX + P4.14 cache + P4.13j Store Range.

## Feedback intake (2026-11-11)

A user reported (platform unknown): interrupted multipart content fails to resume, and Telegram search vs in-chat open behave differently. Windows users also requested favorite channel/group filtering, consolidated recent media, per-subtitle sync isolation, faster cursor/transport hide, and fit/fill actions. This is **not** a confirmed Android bug and no Android source was modified.

## Windows changes

1. Telegram: text-filter for channel/group names, persisted local starred-chat IDs, a favorite-list hub, and recently discovered media from chats/search already accessed. This is not yet a Telegram-wide latest-file index; only observed results are included.
2. Global Telegram search: searchMessages may return one physical archive part from a set. Before **opening** a searched archive, fetch the source chat and use its canonical archive grouping, consistent with in-chat playback. Some older/missing posts may still require opening a forum topic.
3. Resume: normal Continue Watching/Recent/History paths carrying a stable `telegram:` or `telegram-archive:` identity are routed through TDLib. The content is re-enumerated, the exact archive entry is selected by name, the existing persistent key is reused, and the existing resume store restores the playback time. The old localhost HTTP URL is never reused.
4. Subtitle controls: manual primary/secondary delays are remembered separately for each selected subtitle track within the current media; media change clears those mappings. User-adjusted delays cannot automatically carry into a different track.
5. Video controls: Fit (no crop) and Fill (mpv panscan) menu in the lower player deck; selection is carried between videos in the running session.
6. Native pointer: WinForms Hide/Show are paired for mpv's HwndHost; fullscreen/mini/overlay auto-dismiss timers are shorter and honor hover and active seek. Cursor is explicitly revealed on window deactivation and shutdown.

## Scope and safety

- Do not change Telegram Range/7z Store probing, archive byte mapping, local TDLib database and auth/cache cleanup. P4.13j smoke tests must stay green.
- Favorite IDs persist locally in PlayerSettings; no cloud or Telegram writes.
- Long-lived recent-media feed across all chats is not implemented: the hub intentionally includes seen/queried items only, with an accurate label.
- Old forum archives beyond chat-wide history limits may need separate topic enumeration. Do not promise the user that every old post can be reconstructed without reloading the forum.
- Android remains out of scope pending confirmation of platform and reproduction steps.
- Public/main/release branches are unchanged.

## Required acceptance on actual Windows UI

- Telegram: star and unstar, filter text and 'favorites only'; restart and reopen a favorite from another Telegram folder.
- Open an archive via its in-chat card, then via global search, including a result that matched only a `.002` part.
- Resume a multipart 7z Store media through **Home > Continue Watching** after app restart; confirm it opens the same entry and seeks.
- Local video: attach two different external subtitles, synchronize one, switch to the other and verify delay=0 by default; switch back and verify its original per-track delay.
- Let fullscreen and regular video idle, verify pointer and HUD hide; move mouse, verify both return; switch to another app, pointer must remain visible.
- Player lower bar > Fit/Fill; Fit must not crop, Fill intentionally crops letterbox edges.
- CI checks: `tool/verify-r5-p415-user-feedback.ps1`, previous P4.14.1 and P4.13j Windows x64/regression checks.

**Status:** Candidate, not a public V1.1 release or proof of complete runtime acceptance.
