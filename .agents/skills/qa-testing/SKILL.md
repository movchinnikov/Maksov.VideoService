---
name: qa-testing
description: >-
  Testing: xUnit, integration tests with Testcontainers, contract tests (Pact,
  schema validation), k6 load, Flink stream tests, data quality checks, chaos tests.
  Use for red tests against contracts before implementation and for the final run.
---

# QA / Testing

## Input / output
- **Input:** `docs/stories/<feature>.md` (AC), `contracts/`.
- **Location:** `<service>/tests/Maksov.VideoService.<Service>.Tests` (HTTP contract and unit), `libs/tests` (libraries); adapters on a real Postgres and ffmpeg integration live in the services' own test projects; Keycloak login and registration — `scripts/verify-auth.py` (`make verify-auth`).
- **Output:** tests, an AC → test traceability matrix, a coverage report, k6 scenarios.

## Modes
### A. Red tests (before implementation)
1. At least one test per AC, the name contains the ID: `AC_upload_01_returns_presigned_url`.
2. Contract tests: the API response is valid against OpenAPI; events are valid against the schema and contain the envelope.
3. A redelivery test for every consumer.
4. Make sure the tests fail for the right reason (does not compile → add stubs).

### B. Final run
1. Full `make test`; coverage (`coverlet`, threshold as agreed).
2. Load (if the NFRs include latency): `tests/load/*.js` k6, compare p95 with the NFR.
3. Chaos (lessons 4+): kill media-worker in the middle of transcoding → the video is processed exactly once; stop the broker → no losses after recovery.
4. Fill in the traceability matrix.

## Tools
xUnit + FluentAssertions, Testcontainers (Postgres, Redpanda, MinIO), WireMock.Net, Pact/schema validation, k6, Flink test harness, Soda/GE.

## Done when
- [ ] Every AC is covered by ≥1 test (matrix filled in).
- [ ] Tests are deterministic (no sleep, waiting via polling with a timeout).
- [ ] The coverage report and (if needed) the load report are attached.

## Current layout (separate solutions, social features)
- **Service tests** — `services/<svc>/tests/Maksov.VideoService.<Svc>.Tests` in the service solution: API AC tests (`WebApplicationFactory`, the `TestSupport` package — test authentication), adapters on a **real Postgres** (temporary per-service DBs via `TestDatabases`), unit. videos: `Videos.Tests` (`VideoEndpointsTests`, `PostgresAdaptersTests`, `OutboxTests`, `DomainTests`) and `Videos.MediaWorker.Tests` (`FfmpegArgsBuilderTests`, `FfprobeParserTests`, `MediaPolicyTests`, `FfmpegIntegrationTests`); users: `UsersApiTests`, `UsersAdapterTests`; analysis: `DescriptionsApiTests`, `AnalysisAdapterTests`, `FfmpegInspectorTests`, `DescriptionComposerTests`, `StatsParsersTests`, `OllamaClientTests`; comments: `CommentsApiTests`, `CommentsAdapterTests`; gateway: `GatewayTests`; libraries: `libs/tests/…BuildingBlocks.Tests` (`MediaKeysTests`). There are no shared test projects in `services/` anymore.
- **Naming:** `AC_<area>_NN_<what_is_checked>` (`AC_gateway_*`, `AC_users_*`, `AC_comments_*`, `AC_analysis_*`, `AC_feed_*`, `AC_delete_*`, `AC_upload_*` …); the traceability matrix — `docs/stories/lesson-01-upload.md`.
- **Running:** `make test-dotnet` packs libs (`make pack`), starts a temporary Postgres (`VIDEOSERVICE_TEST_POSTGRES`) and runs `dotnet test --solution <slnx>` for `libs` and each service (Microsoft.Testing.Platform, runner in `global.json`). Login check — `make verify-auth`; end-to-end scenario — `make demo`.
- **Mandatory social-feature cases:** visibility (a ready video is visible to others, not-ready/deleted — 404; the description shows any status to the owner, only `done` to others), feed (follows + own, only `ready`, degradation when users is unavailable), follows (idempotency, self-follow, unknown user), search (prefix, excluding self, escaping), comments (the author and the video owner delete, a stranger — 404, unknown video — 404), deletion (owner, MinIO cleanup, events for each subscriber), cascade (`video.deleted.v1` is idempotent, a "tombstone" against a late `processed`).
- **Reference videos are vertical** (9:16, 720×1280), including **without SAR** (`setsar=0` in the generator) — a regression for "Conflicting stream aspect ratios"; horizontal ones are separate cases.
- **Contracts:** internal event intake is checked for 401/400/202 and redelivery; schemas — `contracts/events/video-processed.v1.json`, `video-deleted.v1.json`; internal APIs — `internal-analysis|comments|users.yaml`.
