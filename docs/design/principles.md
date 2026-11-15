# "Film Strip" Design: Principles

The metaphor is a **editing desk and a film archive**. The interface speaks the editor's language: reel, frame, cut, reject, timecode.

## Principles
1. **Paper and ink.** Warm `--paper` background, black `--ink` text, one signal color `--signal` (record button/action, errors). No gradients, glass, or glows.
2. **Rules instead of cards.** Separation by 1–2 px lines and whitespace. Square corners, no shadows.
3. **Three typographic voices.** Serif (headings, video titles), grotesque (body text), monospace (data: timecodes, resolutions, bitrates, statuses).
4. **A grid without cards.** Profile posts are a dense 9:16 grid with 1 px rules instead of padded card tiles.
5. **Concrete words.** "The film is empty. Upload your first video", "Video is longer than 10 minutes", not "Oops!".
6. **Asymmetry.** 7/5 and 8/4 grids; heading on the left, explanation on the right.
7. **Motion only when it serves a purpose.** Upload progress, state changes; ≤ 120 ms; respect `prefers-reduced-motion`.

## Screens and states
| Screen | Empty | Loading | Error | Success |
| --- | --- | --- | --- | --- |
| Sign-in | — | "Checking session…" | return to the sign-in screen | redirect |
| Upload | "Drop a video here" zone | steps 01–03, progress bar, speed | error line at the step, "Try again" | go to the video page |
| Video | — | "Fetching the reel…" / "Editing in progress…" | "Not found" / reject reason | player, DASH/HLS, quality, video passport |

| Video: description | "The description will appear after processing" (video not ready) | "Analyzing frames…" (monospace, static, polling with backoff) | "Could not build the description. The video is unaffected." | serif paragraph + tag line separated by " · " in monospace |
| Profile | — | "Fetching the card…" | error line (loading — placeholder page, saving — above the button) | "Saved" next to the button; name in the header updates |

## Navigation (Instagram-like, in the "Film Strip" language)
A single centered column up to 480 px. A flat bottom bar (rule on top, no shadows or rounded corners): "Feed", "Search", "+ Add" (signal fill, leads to `/upload`), "Profile" (to your own `/u/{id}`). The active tab is bolder, underlined with `--signal`. The top header is just "Film Strip". The only exception to square corners is the round initial avatar.

Routes: `/` feed · `/search` search · `/upload` · `/u/:id` profile · `/profile/edit` · `/videos/:id` (comments — anchor `#comments`).

| Screen | Empty | Loading | Error | Success |
| --- | --- | --- | --- | --- |
| Feed | "The feed is empty. Follow someone — find people in the "Search" tab" + button | "Rewinding the film…" | error line + "Retry" | 9:16 cards; player on poster click, one active at a time; "Show more" |
| Search | "Enter the beginning of a name or username." / "No one found." | "Searching…" (debounce 300 ms) | error line | rows: avatar, username, displayName, "Follow"/"Following" (optimistic, rollback with a note) |
| Profile | grid: "No posts yet" (own profile: "Tap + and add your first video") | "Fetching the card…" | "Profile unavailable" | header, three counters, 3-column 9:16 grid with 1 px rules; own unprocessed videos get a "editing…"/"reject" label |
| Comments | "No comments yet." | "Reading comments…" | error line above the button | newest first, "Delete" only where `canDelete`; limit 500, Ctrl/Cmd+Enter submits |
| Video deletion | — | "Deleting…" | error line | confirmation "Delete the video permanently? Comments and description will be removed too." → go to profile |

## Vertical priority
The product is built around vertical 9:16 videos.
- The player screen (`.screen`) is 9:16, height at most `min(78vh, 720px)`, width derived from height, centered in its column. A horizontal video (`width > height`) is 16:9 at the full width of the same column.
- Video page: in a single column — author, player, title, "What's in the video" (for others' videos — only when `done`), comments, a collapsed "Video passport".
- Thumbnails are 9:16 everywhere: 3-column profile grid; no shadows or rounded corners.
- Upload: hint "vertical 9:16 works best, up to 10 minutes".
- Description — no chips, gradients, or emoji: tags are just a line separated by " · ".

Tokens — [clients/web/src/styles/tokens.css](../../clients/web/src/styles/tokens.css).
