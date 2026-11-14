# R5/P4.11.1 — Choose Torrent Download Location

Private branch: `feature/v1.0.2-r5-p4111-download-location`  
Based on `feature/v1.0.2-r5-p411-qbittorrent-handoff`, commit `cf0fae55b37574a4984e4e1726d12353c70c399a`.

## Windows UI

**Settings → Download location** (localized Turkish and English):
- Shows the currently selected folder; default is the app-specific `torrent-cache` folder.
- **Choose folder** uses the native Windows folder picker. New directories can be created.
- **Open folder** opens the selected location in Explorer.
- **Restore default** resets to the existing default location, without deleting or moving any files.
- Displays available free drive capacity when readable.
- Confirms the selection and asks to **restart ADB Player** to apply it to new torrents. Current playback is never re-targeted during an active session.

## Data and seeding safety

- The new path is stored in user `PlayerSettings.TorrentDownloadDirectory`, default empty for compatibility.
- MonoTorrent and aria2 read the same `TorrentEngineConfiguration.CacheDirectory`, so the option affects whichever streaming engine is active on **next initialization**. A running torrent engine retains its already initialized directory until restart.
- Existing downloads stay at their original paths. No automatic move/rename/cleanup is attempted.
- Root drive and known Windows/program/application directories cannot be chosen. The target is tested for write permission without writing actual content.
- When the active download location is user-selected, **closing a torrent never deletes the content**, even if the user's qBittorrent is sharing from that folder.
- The default internal temporary cache keeps its existing explicit-delete option.
- Using the same folder alone does NOT make qBittorrent automatically take over a partially downloaded torrent. P4.11 hands off the original torrent/magnet, but a later Smart Handoff stage must verify the same-machine path mapping, matching infohash and file layout, stop competing writers, and explicitly trigger qBittorrent's recheck. Never claim that recheck is already implemented.

## Windows acceptance

- [ ] CI static checks and Windows Release x64 build pass.
- [ ] Settings shows the chosen folder, free space and two controls, in Turkish and English.
- [ ] Custom drive/folder selection is persistent after restart and new torrent data is written to that folder.
- [ ] Existing data is untouched after switching folders or restoring the default.
- [ ] An unwritable/protected folder shows a short localized error without changing the existing selection.
- [ ] Opening the folder selects the active path; if settings changed during playback, restart first for the new location.
- [ ] Closing a custom-folder torrent preserves its files; default cache deletion still requires confirmation.
- [ ] Cloud, Prowlarr/Discover, direct .torrent and magnet entry points keep opening correctly.
- [ ] qBittorrent handoff remains opt-in and does not delete or silently migrate downloaded bytes.
- [ ] No changes to public v1.0.1.

**Status:** UI and storage path integration; live Windows and qBittorrent recheck must be confirmed separately.
