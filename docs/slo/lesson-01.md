# SLO and resources: lesson 1

## SLI / SLO (local stand)
| Service | SLI | SLO |
| --- | --- | --- |
| gateway | share of non-5xx responses | 99.5 % |
| gateway + services | p95 API latency (excluding streaming) | < 300 ms |
| videos | p95 latency (excluding streaming) | < 300 ms |
| media-worker | share of valid videos that reach `ready` | 99 % |
| media-worker | time to `ready` | p95 < 3× duration |
| playback | share of successful segment requests | 99.5 % |
| gateway + videos | p95 `GET /api/feed` and `GET /api/users/{id}/videos` | < 300 ms (20 s follow cache) |
| comments | p95 latency (listing and adding comments) | < 300 ms |
| outbox | event (`video.processed.v1`, `video.deleted.v1`) delivered to each subscriber (analysis, comments) | 99 % within 1 min (otherwise — retries with backoff) |
| analysis | description appears after `ready` (video ≤ 1 min) | p95 < 30 s on a local machine |
| analysis | share of `ready` videos that reach `done` | 99 % (an analysis failure does not affect the video) |

Metrics: ASP.NET Core (`http.server.request.duration`) in every service, the transcoding worker (`transcode.jobs{outcome}`, `transcode.duration`). Export is enabled by `OTEL_EXPORTER_OTLP_ENDPOINT` (collector and dashboards — lesson 12).

## Resource budget (compose limits)
| Container | CPU | RAM |
| --- | --- | --- |
| postgres | 1.0 | 512 MB |
| minio | 1.0 | 512 MB |
| keycloak | 1.5 | 1 GB |
| videos | 1.0 | 256 MB |
| media-worker | 2.0 | 1.5 GB |
| users | 0.5 | 192 MB |
| analysis | 0.5 | 256 MB |
| comments | 0.5 | 192 MB |
| analysis-worker | 1.0 | 768 MB |
| gateway | 0.75 | 192 MB |
| web (nginx) | 0.25 | 64 MB |
| **Total without ollama** | **≈ 10** | **≈ 5.4 GB** |
| ollama (profile `ml`, `make up-ml`) | 4.0 | 4 GB |

Migrator containers (`videos-migrator`, `users-migrator`, `analysis-migrator`, `comments-migrator`, 0.5 CPU / 192–256 MB each) are one-shot containers and are not included in the total. Limits are ceilings, not steady-state consumption; the target machine is 16 GB RAM / 8 CPU ([ADR 0006](../adr/0006-microservices-gateway-videos-users-analysis.md)).

## Chaos scenario (manual)
1. Upload a video ≥ 2 minutes long, wait for `processing`.
2. `docker compose ... kill media-worker` → `make up`.
3. Within ≤ `LeaseSeconds` (120 s) the job is picked up again, the video reaches `ready`, `transcode_jobs.attempts` = 2, and objects in MinIO are not duplicated (keys are deterministic).
4. Repeat for delivery: stop `analysis` (`docker compose ... stop analysis`), bring a video to `ready` — the `outbox` row for subscriber `analysis` remains (`sent_at is null`), while the one for `comments` is sent independently; start `analysis` — the description appears after the next attempt (backoff 5 s … 5 min).
5. Feed degradation: stop `users` (`docker compose ... stop users`) — `GET /api/feed` returns 200 with the user's own videos (a warning in the videos log); after `users` starts, follows return within 20 s at most (cache).

## Runbook: video stuck in processing
1. `make logs s=media-worker` — ffmpeg / Postgres errors.
2. In the `videos` DB: `select status, attempts, locked_until, last_error from transcode_jobs where video_id = '...';`
3. If `locked_until` is in the past and the worker is alive — check `LISTEN` (fallback polling picks it up within 15 s).
4. `failed` with `transcode_error` — look at `last_error`, reproduce locally with a test in `FfmpegIntegrationTests`.

## Runbook: description did not appear
The video is `ready` and plays anyway — analysis is independent. Follow the chain left to right:

| Step | Check | What it means |
| --- | --- | --- |
| 1. Event written | `videos` DB: `select id, topic, subscriber, attempts, sent_at, next_attempt_at, last_error from outbox order by created_at desc limit 10;` (one row per subscriber: `analysis`, `comments`) | No row — the video did not reach `ready`. A row with `sent_at is null` — event not sent: look at `last_error` (401 — `INTERNAL_API_KEY` mismatch; connection refused — `analysis` is not up) |
| 2. Dispatcher enabled | `make logs s=media-worker`; media-worker has `Outbox__Subscribers__analysis__BaseUrl` (and `…__comments__BaseUrl`) set | Without subscriber addresses the dispatcher does not start and events accumulate |
| 3. Analysis accepted | `analysis` DB: `select video_id, status, attempts, locked_until, last_error, failure_reason from analyses order by created_at desc limit 5;` | No row — the event was not accepted (see step 1). `queued` for a long time — `analysis-worker` is not running. `processing` with an expired `locked_until` — the worker crashed, the lease will be taken over |
| 4. Failure cause | `analyses.last_error` (full ffmpeg output) and `make logs s=analysis-worker` | `failed` — attempts exhausted (`max_attempts` = 3); analysis can be requeued: `update analyses set status='queued', attempts=0, run_after=now(), locked_until=null, locked_by=null, failure_reason=null where video_id='…'; select pg_notify('analysis_jobs','retry');` (without NOTIFY the worker picks the job up by fallback polling within 15 s) |
| 5. Vision model | with `ANALYSIS_CAPTIONER=ollama`: `make logs s=ollama`, model downloaded (`make up-ml`) | A model error does not block the description: the text is built from the tools' readings |

DB connection: `docker exec videoservice-postgres-1 psql -U <videos|analysis> -d <videos|analysis>` (password — `VIDEOS_DB_PASSWORD` / `ANALYSIS_DB_PASSWORD` from `.env`) or the `POSTGRES_USER` superuser from `.env`.

## Runbook: comments do not work ("video not found" on add)
Comments are available only for videos known to the `comments` service: a video is registered by the `video.processed.v1` event after `ready` (dispatcher fallback polling — 30 s).

| Step | Check | What it means |
| --- | --- | --- |
| 1. Event for comments | `videos` DB: `select id, topic, sent_at, attempts, last_error from outbox where subscriber = 'comments' order by created_at desc limit 5;` | `sent_at is null` — not delivered: `last_error` (401 — `INTERNAL_API_KEY` mismatch, refused — `comments` is not up); no row — `Outbox__Subscribers__comments__BaseUrl` is not set on media-worker |
| 2. Video known | `comments` DB: `select * from known_videos where video_id = '…';` | No row — the event was not accepted. `deleted_at is not null` — the video is deleted, comments were cascade-deleted |
| 3. Service alive | `make logs s=comments`, `make ps` | Migrations ran (`comments-migrator`), healthcheck is green |

Connection: `docker exec videoservice-postgres-1 psql -U comments -d comments` (password — `COMMENTS_DB_PASSWORD`).

## Runbook: feed is empty
1. Are there follows? `users` DB: `select * from follows where follower_id = '<sub>';` (`sub` — the `id` field from `GET /api/users/me`).
2. Are the videos ready? The feed shows only `ready`: `videos` DB: `select id, status, owner_id from videos where owner_id = '<author id>' order by created_at desc;`.
3. Is `users` unavailable? The `videos` log has the warning "Users service unavailable, feed shows only own videos"; check `Users__BaseUrl` and `INTERNAL_API_KEY`. A fresh follow shows up in the feed with up to 20 s delay.
