# R5/P4.6.1 — Cinema-First Discover refinement

Base: R5/P4.6 `f7336afe77f2c5da7fceb41555728d163be9b351`. Branch: `feature/v1.0.2-r5-p461-cinema-first`.

Windows P4.6 visual testing showed framed catalog chrome, posters that still looked too small and synopsis squeezed beside small artwork.

This iteration removes the heavy catalog frame and default card borders, enlarges posters to 152×228px, adds a subtle artwork fade, selected glow, improved typography and a 685px catalog viewport. The spotlight stays compact at 228px with full-height backdrop and bounded 132×190 poster. Right-hand details place title and metadata next to portrait artwork and the **complete localized synopsis below**, using full width. Episodes, source selection, playback and ADB Cloud features are preserved.

## Windows QA
- [ ] R5/P4.1–P4.6.1 guards, Release x64 build and SHA-256 check succeed
- [ ] 100%, 125%, 150% scaling: no distorted poster, clipped CTA, or missing focus
- [ ] Film cards are minimally framed, 152×228 artwork and list scroll show consecutive rows
- [ ] Details: full-width synopsis is readable, no text trimming; scroll as needed
- [ ] Play selects correct episode and stream; favorites/watchlist/custom list remain functional
- [ ] Turkish/English overview, genres, Read more/Show less unchanged
- [ ] Narrow 850/600px view stacks the right panel while retaining accessible controls
- [ ] No effects on auth, provider APIs, streaming logic, public v1.0.1 or user state

Windows visual acceptance is required separately from CI. Private test branch only; no public release.
