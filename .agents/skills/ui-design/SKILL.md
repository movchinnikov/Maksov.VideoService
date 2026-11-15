---
name: ui-design
description: >-
  Web client interface design with character that does not look like typical AI-generated design:
  principles, tokens, typography, grid, screen states, microcopy, an anti-AI checklist.
  Use when creating or changing screens and the design system.
---

# UI Design (anti-AI)

## Input / output
- **Input:** stories, AC, existing screens.
- **Output:** `docs/design/{principles.md,tokens.md,screens/*.md}`, `clients/web/src/styles/tokens.css`.

## Direction
The video service as an **editing desk / film archive**: editorial typography, warm paper and ink, grid and rules, technical captions in a monospaced font (timecodes, resolutions, bitrates), a single signal color. The interface speaks the language of an editor, not of marketing.

## What "AI design" looks like — and what we do instead
| Typical AI template | Ours |
| --- | --- |
| Purple-blue gradients, glassmorphism, glow | Flat surfaces, paper `#F3EFE6`, ink `#141414`, signal `#E2401C` |
| All corners `rounded-2xl`, soft shadows on every card | Square corners, separation by 1px rules and whitespace, no shadows |
| Inter everywhere, uniform weight | Contrast of typefaces: serif for headings, grotesque for text, mono for data |
| Centered hero "Unleash your creativity ✨" | Straight to the point: an action heading, a workspace from left to right |
| A grid of identical 3×N cards | A list-table like an edit sheet: preview, timecode, status, resolutions |
| Emoji and icons on every item | Text labels, an icon only when it is faster than a word |
| Empty states with a robot illustration | Empty state = instruction + action ("The film is empty. Upload your first video.") |
| Boilerplate microcopy ("Oops! Something went wrong") | Specifics: what happened, what to do |

## Tokens (minimum)
- Colors: `--paper`, `--paper-2`, `--ink`, `--ink-2` (secondary text), `--rule` (rules), `--signal` (action/recording), `--ok`, `--warn`.
- Fonts (system, no network): headings — `"Iowan Old Style", "Charter", Georgia, serif`; text — `"Helvetica Neue", Arial, sans-serif`; data — `ui-monospace, "SF Mono", Menlo, monospace`.
- Scale: 12/14/16/20/28/44; line height 1.35–1.5; 8px grid; content width ≤ 1200px, an asymmetric column.
- Motion: functional only (progress, state change), ≤ 150 ms, `prefers-reduced-motion` is respected.

## States of every screen
empty · loading/skeleton (no shimmer — static rules) · partial · error · success · no permission.

## Anti-AI checklist (mandatory)
- [ ] No gradients, glassmorphism, neon glows.
- [ ] No ubiquitous rounded corners and shadows.
- [ ] At least two typefaces with roles; data in monospace.
- [ ] No emoji in the interface; icons only where useful.
- [ ] Texts are specific, in the user's language, without marketing clichés.
- [ ] The composition is asymmetric and relies on the grid/rules.
- [ ] AA contrast, visible focus, the keyboard works.
