---
trigger: model_decision
description: Rules for API contracts and event schemas (currently outbox + internal HTTP, Kafka from lesson 4). Apply to any change in contracts/, producers and consumers.
---

# Contracts and events

- A contract changes **before** the code. A PR with code but no updated contract is rejected.
- **Event schemas, no broker yet:** JSON Schema draft 2020-12 in `contracts/events/<name>.v<major>.json` (example: `video-processed.v1.json`), `additionalProperties: true`. Transport is the transactional outbox + internal HTTP (ADR 0006). From lesson 4 — Kafka and Schema Registry; the format (Avro/Protobuf) is chosen in a separate ADR, the `outbox` table stays.
- Compatibility (JSON Schema now, Schema Registry later): `BACKWARD_TRANSITIVE`. Allowed: adding an optional field with a default. Forbidden: removing/renaming a field, changing a type.
- Required envelope fields: `event_id` (UUID), `occurred_at` (UTC, ISO-8601/timestamp-millis), `schema_version` (int), `trace_id` (W3C; may be null). In lesson 1 JSON events the names are camelCase: `eventId`, `occurredAt`, `schemaVersion`, `traceId`.
- The partition key is specified in the contract (usually `video_id` or `user_id`).
- Topic name: `<domain>.<entity>.<event>.v<major>`, e.g. `media.video.uploaded.v1`.
- A consumer deduplicates by `event_id` (inbox table or an idempotent operation). A producer publishes via the outbox.
- OpenAPI 3.1 in `contracts/openapi/`; breaking changes only via a new path version (`/v2`).
- **Public APIs go only through the gateway** (`contracts/openapi/api.yaml`); a route is added to both the contract and the gateway's `ReverseProxy`. "Someone else's = doesn't exist" is answered with 404.
- **Internal APIs** have a separate file `contracts/openapi/internal-<svc>.yaml`, paths `/internal/v1/...`, security `apiKey` (`X-Internal-Key`, key `INTERNAL_API_KEY`), responses 202/400/401. They are not published through the gateway (404).
- An event receiver checks `schemaVersion`, required fields and key ownership (e.g. `sourceKey` is built per the `MediaKeys` rule), and is idempotent by entity key; the sender retries delivery with backoff.
- Changing an event schema: a new file version only for breaking changes (`v2`), otherwise add optional fields.
- **Lesson 1 events:** `video.processed.v1` (ready) and `video.deleted.v1` (deletion; fields `eventId`, `occurredAt`, `schemaVersion`=1, `traceId`, `videoId`, `ownerId`). Files: `contracts/events/video-processed.v1.json`, `video-deleted.v1.json`.
- **Subscribers and delivery:** the outbox stores one row per (event, subscriber); `analysis` and `comments` receive both events independently (own backoff 5 s … 5 min); the subscriber list and internal paths are in `VideoEvents` in `Videos.Domain`; receiver internal paths are `/internal/v1/events/video-processed|video-deleted`. A new subscriber = update `VideoEvents`, the internal contract `contracts/openapi/internal-<svc>.yaml`, and the address `Outbox__Subscribers__<name>__BaseUrl`.
- **Deletion cascade:** the `video.deleted.v1` receiver deletes related data idempotently; if the service stores a video replica, it leaves a "tombstone" so a late `video.processed.v1` does not resurrect the video.
- **Internal synchronous APIs** (e.g. `GET /internal/v1/users/{id}/following-ids` for the feed) also go in `contracts/openapi/internal-<svc>.yaml` (`internal-users.yaml`), with `X-Internal-Key`; the consumer caches and degrades when the peer is unavailable.
- Internal contracts: `internal-analysis.yaml`, `internal-comments.yaml`, `internal-users.yaml`.
