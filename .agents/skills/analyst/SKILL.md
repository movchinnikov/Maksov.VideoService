---
name: analyst
description: >-
  Systems analysis: user stories, acceptance criteria, OpenAPI 3.1, Avro/Protobuf
  event schemas with compatibility checks, state diagrams (video lifecycle),
  NFRs. Use after an ADR is accepted and before writing tests and code.
---

# Analyst

## Input / output
- **Input:** ADR (`docs/adr/`).
- **Output:** `contracts/openapi/<service>.yaml`, `contracts/events/<topic>.avsc`, `docs/stories/<feature>.md` following the [story-template.md](resources/story-template.md) template.

## Steps
1. Read the ADR and `.agents/rules/contracts-and-events.md`.
2. Write user stories: "As a <role>, I want <action>, so that <value>".
3. For each story, write Given/When/Then acceptance criteria with IDs `AC-<feature>-NN`, including negative scenarios and errors.
4. Describe the API in OpenAPI 3.1: response codes, error schemas (RFC 7807 Problem Details), idempotency (`Idempotency-Key` for POST), pagination (cursor).
5. Describe events: envelope (`event_id`, `occurred_at`, `schema_version`, `trace_id`), partition key, topic, who produces/consumes.
6. For entities with a lifecycle, draw a state diagram (for example, Video: `Uploading → Uploaded → Processing → Ready | Failed → Embedded`).
7. NFRs: p95/p99 latency, acceptable loss/duplicates, video processing time.
8. Verify:
   - OpenAPI: `npx @redocly/cli lint contracts/openapi/*.yaml` (or `spectral lint`);
   - Avro compatibility against the previous version in Schema Registry (`make contracts-check`, once the target exists).

## Done when
- [ ] Contracts are in `contracts/`, linters are green, compatibility is checked.
- [ ] Every story has verifiable acceptance criteria with IDs.
- [ ] NFRs and a state diagram are specified (if applicable).
- [ ] The story has an empty AC → test traceability matrix for QA.

## Current layout (after the move to services)
- Public routes are in `contracts/openapi/api.yaml` (version 0.2.0, "via the gateway"): videos, `GET /api/videos/{id}/description`, `/api/users/me` (GET/PUT), `/api/users/{id}`. Internal ones live in a separate `internal-<svc>.yaml` file (reference: `internal-analysis.yaml`: `apiKey` `X-Internal-Key`, responses 202/400/401).
- Events are for now **JSON Schema 2020-12** (`contracts/events/video-processed.v1.json`, `additionalProperties: true`), Avro from lesson 4. Envelope: `eventId`, `occurredAt`, `schemaVersion`, `traceId` (nullable).
- ACs are named by area: `AC-upload`, `AC-transcode` (including `AC-transcode-05` — a vertical video without SAR), `AC-describe`, `AC-users`, `AC-gateway`, `AC-analysis`, `AC-library`, `AC-watch`, `AC-auth`, `AC-security`; the traceability matrix uses real test names (`docs/stories/lesson-01-upload.md`).
- Test videos and examples are vertical (ADR 0007).
- Contracts after the social features: public `api.yaml` v0.3.0 (feed `GET /api/feed`, grid `GET /api/users/{id}/videos`, `DELETE /api/videos/{id}`, comments, search `GET /api/users?query`, `PUT/DELETE /api/users/{id}/follow`, profile with `isFollowing` and counters, `ownerId`/`ownerUsername` in videos, description visibility); internal ones — `internal-analysis.yaml` (+`video-deleted`), `internal-comments.yaml`, `internal-users.yaml` (`following-ids`); events — `video-processed.v1.json`, `video-deleted.v1.json` (JSON Schema 2020-12, `additionalProperties: true`).
- Social feature ACs: `AC-users-07..10`, `AC-comments-01..09`, `AC-feed-01..02`, `AC-delete-01`, `AC-analysis-05..08`; the matrix uses real tests from `services/<svc>/tests`.
- "Interesting videos" (recommendations) are not in lesson 1, deferred to lessons 8–9.