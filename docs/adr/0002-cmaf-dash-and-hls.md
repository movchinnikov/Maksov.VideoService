# 0002. CMAF: one set of segments for DASH and HLS

- **Status:** Accepted
- **Date:** 2026-11-15
- **Lesson / feature:** lesson-01

## Context
Both protocols are needed: DASH (Android/ExoPlayer, Chrome via MSE) and HLS (Safari/iOS natively). Resources are limited: transcoding is the most expensive operation in the system.

## Decision
Encode **once** into fragmented MP4 (CMAF) and write **two manifests** over the same segments: ffmpeg `-f dash -hls_playlist 1` → `manifest.mpd` + `master.m3u8` + `media_N.m3u8` + `init-streamN.m4s` / `chunk-streamN-XXXXX.m4s`.
- H.264 High + AAC-LC (maximum compatibility), 240p–1080p ladder by the short side, no upscaling.
- A keyframe every 2 s in all renditions (`-force_key_frames expr:gte(t,n_forced*2)`, `-sc_threshold 0`), 4 s segments → boundaries align, ABR switching without artifacts.
- Single pass: decode once, `split` into N renditions.

## Alternatives considered
| Option | Why rejected |
| --- | --- |
| HLS on MPEG-TS + separate DASH on fMP4 | Double encoding/storage (×2 CPU and disk) |
| HLS only (fMP4) | DASH is needed by the Android client and the ABR part of the course |
| N separate ffmpeg runs per rendition | N decodes of the source — wasted CPU |
| H.265/AV1 | Expensive in CPU on a local machine, worse compatibility; can be added as a separate rendition later |

## Consequences
- (+) ~2× savings in CPU and storage, a single ladder.
- (−) Old devices without fMP4-HLS support (iOS < 10) are not supported — acceptable.
