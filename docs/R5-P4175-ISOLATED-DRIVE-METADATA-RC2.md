# R5/P4.17.5 — Public Google Drive filename metadata (RC2)

## Why
Public Drive playback can work and be resumable but display only "Google Drive dosyası". Google's *download confirmation* HTML often carries no media filename even though the public `/file/d/{id}/view` page displays the title.

## Contained change
- Reuse the existing bounded confirmation HTML first, without changing playback or Range behavior.
- If no valid filename is present, make one opportunistic GET to the public preview with a **different HttpClient**, `UseCookies=false`, no Authorization headers and a 2.5-second budget.
- Read at most 192 KiB of HTML. Only accept a valid media filename from `og:title` or the document title, with extension checking.
- Do not log file names, Google IDs, confirmation tokens, cookies, or preview page HTML.
- Synthetic Google Drive playback handlers cannot accidentally access the live internet.
- On metadata denial/timeout, continue to use the existing "Google Drive dosyası" title and leave the playback proxy alone.
- The resolved filename is already carried by `RemoteOpenRequest.DisplayName` into RecentSources, Continue Watching and eligible poster identity parsing.

## Live acceptance
1. In a new isolated RC2 Windows portable, open an anonymous public Drive URL whose browser preview displays a filename.
2. Confirm the file name (not the generic fallback) in recent history and Continue Watching after at least 15–30 seconds playback.
3. Confirm playback does not regress and seeking still works.
4. Restart and reopen the same media from a resume card; its source remains the original Drive sharing URL.
5. ADB Cloud sign-in may still be needed for poster lookup; metadata title detection itself requires no login.

## Caveat
Google Drive's HTML is not a stable API, so best-effort public metadata may fail for particular links. It must never prevent video playback. Storage capacity is distinct from downloadable-bandwidth quota; a full Google account alone does not establish why a given public link returns quota HTML.
