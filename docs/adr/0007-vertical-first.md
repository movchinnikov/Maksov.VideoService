# 0007. The product is optimized for vertical video (9:16)

- **Status:** Accepted
- **Date:** 2026-11-15
- **Lesson / feature:** lesson-01

## Context
The service is conceived as an Instagram analogue: videos are shot on a phone and watched on a phone. The main format is vertical 9:16. Horizontal videos and phone files with rotation (rotation metadata) also occur.

## Decision
Vertical video is the priority format; horizontal video is **supported**, but defaults, the UI and test material are designed for vertical.

| Area | What it means |
| --- | --- |
| UI | Screens and the player are designed for 9:16 first (details in `docs/design/principles.md`); horizontal videos play back fine |
| Quality ladder | Rungs are defined by the **short side** (240p … 1080p) — one logic for both orientations, no upscaling; a vertical 720×1280 yields the "720p" rung |
| Sources without SAR | Phone videos often have no SAR; `setsar=1` is added before `split`, otherwise DASH fails with "Conflicting stream aspect ratios" (rounding to even dimensions diverges across rungs) |
| Test videos | Fixtures and the demo are vertical 720×1280 without SAR (`scripts/demo.sh`, `FfmpegIntegrationTests`, `FfmpegInspectorTests`) |
| Description | Text and tags account for orientation (vertical / horizontal / square) |

## Alternatives considered
| Option | Why rejected |
| --- | --- |
| Horizontal only, like a classic video host | Does not match the target product (short phone videos) |
| Vertical only, reject everything else | Unnecessary restriction: horizontal files are valid and add no complexity |
| Separate ladders for each orientation | Duplicated logic; the short side solves the problem with one formula |

## Consequences
- (+) Defaults and the demo match the real usage scenario.
- (+) A single short-side ladder means less code and fewer tests.
- (−) Horizontal videos get less space in a UI designed for 9:16.

Related decisions: [ADR 0002](0002-cmaf-dash-and-hls.md) (CMAF), [ADR 0006](0006-microservices-gateway-videos-users-analysis.md) (services).
