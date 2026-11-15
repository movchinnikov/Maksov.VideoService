---
name: wf-new-event
description: >-
  Workflow for adding a new event (currently JSON Schema + outbox + internal HTTP, from lesson 4 — Avro/Kafka): schema → compatibility check → contract test →
  parallel producer and consumer implementation → idempotency review → lag monitoring.
  Use when adding or changing any event (/wf-new-event <EventName>).
---

# Workflow: new event

Input: the event name, producer, consumers, purpose.

| # | Agent | Action | Artifact | Gate |
| --- | --- | --- | --- | --- |
| 1 | `analyst` | A schema with the envelope (`event_id`, `occurred_at`, `schema_version`, `trace_id`), partition key, topic `<domain>.<entity>.<event>.v1`, producer/consumers | `contracts/events/<topic>.avsc`, an entry in `contracts/events/README.md` | schema lint |
| 2 | `analyst` | Compatibility check (`BACKWARD_TRANSITIVE`) against Schema Registry | check log | compatible (or it is a new topic) |
| 3 | `qa` | Contract test: the producer emits a valid event; the consumer accepts it and is idempotent on redelivery | `tests/contract/<event>*` | test is red |
| 4a | producer developer | Publishing via outbox | code | producer contract test is green |
| 4b | consumer developer *(in parallel with 4a)* | Inbox dedup, retry → DLQ | code | consumer contract test is green |
| 5 | `reviewer` | Focus: idempotency, ordering by key, DLQ, compatibility | `docs/reviews/*` | no `BLOCKER` |
| 6 | `devops` | Create the topic and DLQ in init, register the schema, a dashboard and a consumer-group lag alert | `deploy/**` | the lag metric is visible in Grafana |

Changing an existing event: only adding optional fields with a default; otherwise — a new `.v2` topic and an ADR with a consumer migration plan.

## Current layout (after the move to services)
While there is no broker (lessons 1–3), the event contract is **JSON Schema draft 2020-12** in `contracts/events/<name>.v<major>.json` (sample — `video-processed.v1.json`, `additionalProperties: true`), and delivery is a **transactional outbox + internal HTTP**:
- Step 1 (`analyst`): a JSON Schema; the receiver is described in `contracts/openapi/internal-<svc>.yaml` (`POST /internal/v1/events/<name>`, security `apiKey` `X-Internal-Key`, responses 202/400/401).
- Step 2: compatibility check — only adding optional fields; comparison with the previous version's schema.
- Step 4a: the producer writes an `outbox` row in the same transaction as the state change; `OutboxDispatcher` sends with backoff.
- Step 4b: the receiver validates (`schemaVersion`, required fields, key ownership), is idempotent by entity key and `eventId`; the `/internal/**` path is not published on the gateway.
- Step 6 (`devops`): the `INTERNAL_API_KEY` key in `.env.example`, `Outbox__<Target>BaseUrl` on the sender.
From lesson 4: the same contract moves to Avro + Schema Registry, `OutboxDispatcher` publishes to the topic `<domain>.<entity>.<event>.v<major>` (for `video.processed.v1` the partition key is `videoId`), the HTTP receiver is replaced by a consumer.
- Implemented events: `video.processed.v1`, `video.deleted.v1`. The videos producer writes to outbox **one row per subscriber** (`VideoEvents.SubscribersOf` — currently `analysis`, `comments`); each subscriber has its own delivery and backoff. A new subscriber: add to `VideoEvents`, an internal `POST /internal/v1/events/<name>` in its service (`RequireInternalKey()`), the contract `contracts/openapi/internal-<svc>.yaml`, `Outbox__Subscribers__<name>__BaseUrl` in compose.
- Cascading events (deletion) are handled idempotently; a service holding a replica of a foreign entity keeps a "tombstone" so that a late `processed` does not resurrect it. Contract tests are in `services/<svc>/tests` (`AC_<area>_*` for 401/400/202 and replay).
