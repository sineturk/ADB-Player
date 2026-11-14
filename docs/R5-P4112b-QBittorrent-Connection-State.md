# R5/P4.11.2b — qBittorrent Connection State Clarity

Private branch: `feature/v1.0.2-r5-p4112b-qb-connection-state`  
Base: `feature/v1.0.2-r5-p4112a-discover-stream-size` at `faa151aac857bbe14626294f0df28d8df6fba730`.

## Problem

On restarting ADB Player, the saved qBittorrent WebUI URL and username reappeared while the password box was blank. The password is intentionally stored in Windows DPAPI and must **not** be repopulated into the UI. However, qBittorrent was not rechecked on page entry and the same generic text field showed connection state and transfer errors such as "Önce torrent açın". This was confusing, even when the credentials remained valid.

## Changes

- The qBittorrent connection header always shows a **status dot and localized state**, including with the setup Expander collapsed.
- States: not configured, saved but unverified, checking, connected, unavailable, saved password missing, and pending uncommitted edits.
- When Torrent page loads, automatically test the **saved URL/username and DPAPI password** against qBittorrent login and version endpoints. Never claim "Connected" from saved settings alone. Re-run on subsequent visits, and add an explicit **Check connection** action.
- Keep connection state separate from transfer-operation status. "Open a torrent first" is an action message and does not replace the connectivity indicator.
- A blank password field after relaunch is expected; a small **password stored securely** explanation appears only when secure-store retrieval confirms the credential exists.
- Connect and save still verifies before persisting, and now updates the connection-status dot. Edited URL/username/password invalidates a previous "Connected" indicator until it is saved and rechecked.
- Do not log credentials, server response contents, tracker passkeys or token URLs.
- When automatic handoff is checked but a custom/shared download folder is selected, show a short warning that it is deliberately disabled and to use Smart Handoff, rather than silently claiming it works.
- Preserve the existing qBittorrent API handoff, smart hash recheck, regular torrent playback and previously saved credentials. No public release changes.

## Windows acceptance

- [ ] CI R5/P4.1–R5/P4.11.2b source checks and Release x64 build pass.
- [ ] Restart with correct saved WebUI credentials: Torrent page shows **Checking**, then **Connected** in green, without entering password again.
- [ ] Password box stays empty and does not expose the secure-store value; explanation says saved password is available.
- [ ] Stop qBittorrent, return to Torrent page or select Check connection: shows **Unavailable**, not Connected.
- [ ] Wrong password/invalid URL on Connect and save shows failure without saving bad credentials.
- [ ] Edit URL, username or enter a password: state indicates changes need saving; old successful connection is not mistakenly shown.
- [ ] Click Send to qBittorrent without a torrent: operation message says open a torrent but connection header still shows the independent status.
- [ ] When auto handoff is checked with a user-selected download folder, explanatory warning is visible (handoff remains disabled to prevent dual writers).
- [ ] Change Turkish/English while connected; text translates without dropping the connection state.
- [ ] Verify Smart Handoff and Torrent tabs preserve existing functions.

CI and compile can verify contracts; a live qBittorrent WebUI is still needed to confirm runtime connection behavior.
