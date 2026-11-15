---
name: sre
description: >-
  SRE: SLI/SLO and error budget, OpenTelemetry Collector, Prometheus, Grafana, Loki, alerts with
  runbooks, resource planning from measurements, k6 load, chaos tests, incident analysis.
  Use when adding a service, setting up observability, handling incidents and estimating capacity.
---

# SRE

## Input / output
- **Input:** ADR, NFR, the set of services, metrics.
- **Output:** `docs/slo/<service>.md`, `deploy/observability/{otel-collector,prometheus,grafana,loki}/`, alerts, `docs/runbooks/<alert>.md`, load/chaos reports.

## SLI/SLO (reference values for the learning stand)
| Service | SLI | SLO |
| --- | --- | --- |
| gateway and services (videos, users, analysis, comments) | share of successful (non-5xx) requests; p95 latency | 99.5 %; p95 < 300 ms (excluding file uploads) |
| media-worker | share of valid videos processed successfully; time to `Ready` | 99 %; p95 < 3× video duration |
| outbox → analysis | event delivered / age of the oldest unsent one | 99 % within 1 min |
| analysis | time for the description to appear after `ready` (video ≤ 1 min) | p95 < 30 s locally |
| playback | share of successful segment requests; TTFF | 99.5 %; p95 TTFF < 2 s locally |
The error budget is in `docs/slo/`, alerts on burn rate (fast 1 h / slow 6 h).

## Observability
- All services → OTLP → OTel Collector → Prometheus / Loki / Tempo (or Jaeger). Collector resources are limited (`memory_limiter`, batching).
- RED metrics for the API, USE for workers; queue metrics: depth, age of the oldest job, attempts, transcoding duration.
- Dashboards are provisioned from the repository (JSON), no manual edits in the UI.
- The `observability` profile in compose keeps the base stack light.

## Resources
- Container limits are based on measurement (`docker stats` under k6 load) + 30 % headroom; a budget table in the README.
- For workers — the rule "one job per N CPUs", scaling by queue depth (KEDA in lesson 11).

## Reliability
- Chaos: kill `media-worker` in the middle of transcoding → the job is taken over via lease, the video becomes `Ready` exactly once; restart of Postgres/MinIO → services recover without manual intervention.
- Graceful shutdown: on SIGTERM the worker finishes or releases the job (`HostOptions.ShutdownTimeout`).

## Done when
- [ ] SLI/SLO and error budget are described.
- [ ] A dashboard and alerts with runbooks for the new service.
- [ ] Limits are justified by measurement.
- [ ] The chaos scenario is executed, recovery is confirmed.

## Current layout (after the move to services)
- Services: `gateway`, `videos`, `media-worker`, `users`, `analysis`, `analysis-worker`; one DB per service.
- Limits (CPU/RAM): postgres 1.0/512M, minio 1.0/512M, keycloak 1.5/1G, videos 1.0/256M, media-worker 2.0/1.5G, users 0.5/192M, analysis 0.5/256M, analysis-worker 1.0/768M, comments 0.5/192M (+ comments-migrator 0.5/192M), gateway 0.75/192M, web 0.25/64M; ollama (`ml` profile) 4.0/4G. Total without ollama ≈ 9.5 CPU / 5.2 GB. The source of truth is `docs/slo/lesson-01.md`.
- Queue and outbox metrics: depth of `transcode_jobs` and `analyses` (`queued`/`processing`), age of the oldest job, `outbox` rows with `sent_at is null`, `attempts`, `last_error`.
- Runbook "description did not appear": `outbox` (`sent_at`, `last_error`: 401 — `INTERNAL_API_KEY`, refused — `analysis` is not up) → `analyses.last_error` / `status` → `analysis-worker` logs; an analysis failure does not affect the video. Details — `docs/slo/lesson-01.md`.
- The end-to-end `trace_id` is taken from the `video.processed.v1` event (nullable field).
- Total without ollama ≈ 10 CPU / 5.4 GB (comments added 0.5 CPU / 192 MB). Outbox is one row per subscriber (`subscriber`): an alert on the age of the oldest unsent one, separately for `analysis` and `comments`.
- Runbooks: "comments do not work" (`outbox` for `comments` → `known_videos`), "feed is empty" (`follows`, video status, 20 s cache, `Users service unavailable` in the videos log) — `docs/slo/lesson-01.md`.
- Feed SLI: p95 `GET /api/feed` < 300 ms with the follows cache; share of degraded responses (users unavailable) — from the warning log.
