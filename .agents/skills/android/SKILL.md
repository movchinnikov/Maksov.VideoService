---
name: android
description: >-
  Android client in Kotlin and Jetpack Compose: feed, Media3/ExoPlayer with HLS and ABR,
  upload via presigned URL with resume (WorkManager), preloading of the next video,
  view events with watch ratio, offline cache.
---

# Android

## Input / output
- **Input:** `contracts/openapi/`, the `VideoViewed` schema.
- **Output:** the app in `clients/android/` (Gradle, Kotlin DSL).

## Architecture
Compose + ViewModel + Kotlin Flow; Retrofit/Ktor client, generated or written strictly from OpenAPI; Hilt/Koin for DI; the base URL points to the local stack (`10.0.2.2` for the emulator).

## Key mechanics
- **Player:** Media3 ExoPlayer + `HlsMediaSource`, ABR by default; a pool of 2–3 players for the vertical feed; preloading of the next video (`PreloadManager`/`DefaultPreloadManager`).
- **Cache:** `SimpleCache` + `CacheDataSource` (limit, LRU).
- **Upload:** request presigned multipart → `WorkManager` uploads the parts, resuming after a connection drop → complete.
- **View events:** `watch_ratio = max_position / duration`, sent in batches on pause/swipe/going to background, offline queue in Room, `event_id` is generated on the client (idempotency).

## Verification
Unit tests (ViewModel, watch-ratio calculation), instrumented player tests against the local HLS from `make up`, manual check of quality switching (Network throttling in the emulator).

## Done when
- [ ] The player switches quality when bandwidth changes.
- [ ] View events are sent with the watch ratio and are not duplicated.
- [ ] Upload resumes after a network drop.
