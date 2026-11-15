# VideoService — a video social network on .NET (tutorial series)

An Instagram-like app with video at its core, serving as the basis of a lesson series for developers.
Each lesson is a reproducible git tag (`lesson-NN`) with working code, tests, documentation, and the lesson text.

**Product priority — vertical video (9:16).** Horizontal videos are supported, but the UI, defaults, and test clips are vertical (demo 720×1280, no SAR). See ADR 0007.

## Hard constraints

- **Local only.** No cloud services. If a solution needs the cloud, a local replacement is proposed.
- Early lessons (1–10) use `docker-compose`; Kubernetes (kind/k3d + Helm) is introduced from lesson 11.
- Everything must fit on an ordinary work machine (target: 16 GB RAM, 8 CPU for the full stack).
- **Privacy of AI artifacts:** agent configuration (`.agents/`, `.claude/`, `CLAUDE.md`, `AGENTS.md`) and proprietary ML work (`private/`) **are not committed to git** (see `.agents/rules/privacy-ai.md`).

## Stack

| Purpose | Choice |
| --- | --- |
| Backend | **multi-service**: gateway (YARP) + `videos`, `users`, `analysis` (+ workers), each with its own DB; **latest stable .NET** (currently .NET 10, single upgrade point — `Directory.Build.props` + `global.json`), ASP.NET Core minimal APIs, Worker Service, Npgsql/Dapper (EF Core where justified), OpenTelemetry |
| Authorization | **Keycloak** (OIDC, PKCE): sign-in by username, e-mail **or phone**, registration with phone (custom provider `deploy/keycloak/spi`), external SSO: Google, Microsoft (Entra ID) via Identity Brokering |
| Web client (primary at start) | React + TypeScript + Vite, Shaka Player (DASH + HLS) |
| Mobile client (lesson 10) | Android: Kotlin, Jetpack Compose, Media3/ExoPlayer |
| Media | FFmpeg, CMAF (fMP4) — **one set of segments, two manifests: DASH (`.mpd`) and HLS (`.m3u8`)**, ABR ladder |
| Object storage | MinIO |
| Job queue (lessons 1–3) | PostgreSQL (`FOR UPDATE SKIP LOCKED` + lease + `LISTEN/NOTIFY`) → Kafka from lesson 4 |
| Service communication (lessons 1–3) | transactional outbox + internal HTTP (`X-Internal-Key`) → Kafka from lesson 4 (the `outbox` table stays) |
| Video description | ffmpeg-based analysis tools; optionally a local vision model via Ollama (compose profile `ml`, `make up-ml`) |
| Event bus | Kafka (Redpanda in compose / Strimzi in k8s) + Schema Registry |
| OLTP | PostgreSQL |
| Hot feed, counters | Tarantool (simplified — Redis) |
| Analytics | ClickHouse or DuckDB + Iceberg |
| Stream / batch | Flink / Spark, orchestration with Airflow or Dagster |
| Vector search | Qdrant |
| Observability | OpenTelemetry Collector, Prometheus, Grafana, Loki |
| Packages | Shared libraries `libs/*` (`Primitives`, `BuildingBlocks`, `Web`, `TestSupport`) are packed into NuGet (`make pack` → local feed `artifacts/nuget`, version `0.1.0-local.<hash>`); services consume them as packages |
| CI | GitHub Actions / `act` |

## Architectural style

**Hexagonal (ports & adapters) + Vertical Slice.** Details — `.agents/rules/architecture.md`.
- Domain and use cases do not depend on infrastructure; ports are interfaces, adapters (Postgres, MinIO, ffmpeg, Keycloak) live outside.
- Code is grouped by feature, not by technical layer.
- **Separate solutions (ADR 0008):** each service has its own solution `services/<svc>/Maksov.VideoService.<Svc>.slnx` (gateway, videos, users, analysis, comments) with its own `Directory.Build.props|targets`, `Directory.Packages.props`, `global.json`, `nuget.config`; shared MSBuild settings — `build/` (`Common.props|targets`, `Common.Packages.props`, `global.json`/`nuget.config` templates); libraries — the `libs/` solution. The root `.editorconfig` is shared. Docker build context is the repository root; `libs` are packed in the first Dockerfile stage.
- **Every HTTP service** (`services/videos|users|analysis|comments`) consists of assemblies `<Svc>.WebApi` (host), `<Svc>.Bll` (ports), `<Svc>.Dal` (Postgres adapters + migrations) + **per-feature assemblies** `<Svc>.Features.<Area>`. `Videos.Domain` is a project inside videos (not a library); media-worker — `services/videos/Maksov.VideoService.Videos.MediaWorker`; `MediaKeys` — in BuildingBlocks. The shared web layer is the NuGet library `libs/Maksov.VideoService.Web`. The prefix of all assemblies and namespaces is **`Maksov.VideoService.`**.
- **DB-per-service:** one Postgres instance, each service has its own DB and role (`deploy/postgres/init/00-databases.sh`); no shared tables or cross-service references.

## System architecture

`web` (React; bottom navigation Feed/Search/＋/Profile) → nginx → **`gateway`** (YARP, host port 8081: JWT, rate limit, routes) → services:
- `/api/videos/**`, `GET /api/feed`, `GET /api/users/{id}/videos` → **`videos`** (+ `media-worker`: CMAF, DASH+HLS, outbox dispatcher; DB `videos`: `videos`, `transcode_jobs`, `outbox`). The feed contains ready videos from followed users plus your own; `DELETE /api/videos/{id}`; ready videos are visible to any authenticated user;
- `/api/users/**` → **`users`** (profiles, follows `PUT/DELETE /{id}/follow`, search `GET /api/users?query`; DB `users`: `profiles`, `follows`);
- `GET /api/videos/{id}/description` → **`analysis`** (DB `analysis`, table `analyses` = queue; the owner sees any status, others only `done`) + `analysis-worker` (ffmpeg, optionally Ollama);
- `/api/videos/{id}/comments/**` → **`comments`** (DB `comments`: `comments`, `known_videos`; a comment can be deleted by its author or the video owner).
`/internal/**` is not exposed externally (404); services behind the gateway validate JWT themselves (zero trust); inter-service calls use the `X-Internal-Key` key (`INTERNAL_API_KEY`). "Interesting videos" — deferred (lessons 8–9).

Event chain: presigned upload to MinIO → `complete` → transcode job → `ready` → `video.processed.v1` event into `outbox` (in the same transaction) → `OutboxDispatcher` (media-worker) → subscribers `analysis` and `comments` (`POST /internal/v1/events/video-processed`, `X-Internal-Key`) → `analysis-worker` builds the description; `comments` registers the video (`known_videos`). `DELETE /api/videos/{id}` → `video.deleted.v1` → `analysis` (deletes the description), `comments` (cascades comment deletion). Outbox holds one row per (event, subscriber); addresses are `Outbox__Subscribers__<name>__BaseUrl`. Feed: videos → users `GET /internal/v1/users/{id}/following-ids` (20 s cache; if unavailable — only own videos).
Next (lesson 4+): the outbox transport is replaced by Kafka; `VideoProcessed` → `feature-worker` → `VideoEmbedded` → Flink/Spark → `feed`.

Decisions: ADR 0006 (services and gateway), 0007 (vertical video), 0008 (separate solutions and NuGet; 0004 partially superseded), 0009 (social features, comments). Contracts: `contracts/openapi/api.yaml` (public), `internal-analysis.yaml`, `internal-comments.yaml`, `internal-users.yaml` (internal), `contracts/events/video-processed.v1.json`, `video-deleted.v1.json`.

Events: `VideoUploaded`, `VideoProcessed`, `VideoFailed`, `VideoEmbedded`, `VideoViewed`, `VideoLiked`, `CommentAdded`, `UserFollowed`, `FeedRecomputed`.
Every event contains `event_id`, `occurred_at`, `schema_version`, `trace_id` (camelCase in the lesson 1 JSON). Schemas live in `contracts/events/` (JSON Schema for now, Avro/Kafka from lesson 4). Implemented: `video.processed.v1`, `video.deleted.v1`.

## Single source of truth

Before working, an agent **reads**; after working, it **updates**:

1. `docs/adr/` — architectural decisions.
2. `contracts/openapi/`, `contracts/events/` — API and event contracts.
3. `README.md` — how to start and verify.
4. `docs/lessons/` — lesson texts.

Agreements made in chat do not count as decisions until recorded in these artifacts.

## Cross-cutting rules (mandatory for all agents)

1. Contract first, then code. Event schema changes must be backward compatible only.
2. Every event/job handler is idempotent and tolerates redelivery.
3. Everything runs locally; a cloud solution is replaced by a local equivalent.
4. No secrets in the repository: configuration via environment variables and `.env.example`.
5. Every feature includes tests and documentation updates and passes review (including security review for auth, uploads, and public endpoints).
6. Embeddings and schemas have an explicit version (`model_version`, `schema_version`).
7. Resource limits (CPU/memory) are specified explicitly for every container.
8. Architectural decisions are recorded in ADRs, not in chat.
9. An agent does not change another's area of responsibility without a request via a shared artifact (contract, ADR, task).
10. The "done" criterion from the role description is mandatory.
11. Resource economy: avoid unnecessary work (streaming instead of buffering, no needless allocations, async only for I/O, no polling without backoff).
12. No traces of AI in git: no `Co-Authored-By`/"generated by" in commits, code, and documents.

Details — in `.agents/rules/`.

## Agent team

Subagents: `.agents/agents/` (mirror for Claude Code: `.claude/agents`). Role skills: `.agents/skills/<role>/SKILL.md`.
Models: strong = opus (Antigravity: pro), fast = sonnet/haiku (Antigravity: flash).

| Role | Subagent | Skill | Model | Area (writes to) |
| --- | --- | --- | --- | --- |
| Architect | `architect` | `architect` | strong | `docs/adr/`, `docs/diagrams/` |
| Systems analyst | `analyst` | `analyst` | fast | `contracts/`, `docs/stories/` |
| Backend .NET | `dotnet-backend` | `dotnet-backend` | fast | `services/gateway`, `services/videos`, `services/users`, `services/analysis`, `services/comments`, `services/feed`, `libs/` |
| Video engineer | `video-engineer` | `video-ffmpeg` | fast | `services/videos/Maksov.VideoService.Videos.MediaWorker`, ffmpeg part of `services/analysis` (Worker, by agreement), `tests/fixtures/videos` |
| ML engineer | `ml-engineer` | `ml-embeddings` | strong | open baseline captioner in `services/analysis`, `services/feature-worker`; everything proprietary (prompts, models, weights) — `private/` |
| Data engineer | `data-engineer` | `data-flink-spark` | fast | `data/` |
| DBA | `dba` | `dba` | fast | migrations, storage schemas |
| DevOps | `devops` | `devops-k8s` | fast | `deploy/`, `Makefile`, CI, NuGet packages |
| SRE | `sre` | `sre` | fast | `deploy/observability`, `docs/slo/`, `docs/runbooks/` |
| Security / pentest | `security` | `security-pentest` | strong | `docs/security/`, `tests/security/` |
| QA | `qa` | `qa-testing` | fast | `tests/` |
| Reviewer | `reviewer` | `code-review` | strong | findings only |
| Frontend React | `frontend-react` | `frontend-react` | fast | `clients/web` |
| Designer | `designer` | `ui-design` | strong | `docs/design/`, `clients/web/src/styles` |
| Android | `android-dev` | `android` | fast | `clients/android` |
| Methodologist | `lesson-writer` | `lesson-writer` | fast (haiku) | `docs/lessons/`, `README.md` |

## Workflows (skills with the `wf-` prefix)

| Scenario | Skill |
| --- | --- |
| Main "feature or lesson" cycle | `wf-feature` |
| New event | `wf-new-event` |
| Video upload and processing (end to end) | `wf-video-pipeline` |
| Embedding model change | `wf-embedding-model-change` |
| Review | `wf-review` |
| Incident and postmortem | `wf-incident` |

## Container limits (compose)

postgres 1.0/512M · minio 1.0/512M · keycloak 1.5/1G · videos 1.0/256M · media-worker 2.0/1.5G · users 0.5/192M · analysis 0.5/256M · analysis-worker 1.0/768M · comments 0.5/192M (+ comments-migrator 0.5/192M) · gateway 0.75/192M · web 0.25/64M. Total without ollama ≈ 10 CPU / 5.4 GB; ollama (profile `ml`) 4.0/4G.

## Repository structure

```
.agents/{rules,agents,skills}   agent configuration (not in git; .claude/* — symlinks)
docs/{adr,diagrams,postmortems,lessons,security,slo,runbooks,design}
contracts/{openapi,events}
build/                          shared MSBuild settings (Common.props|targets, Common.Packages.props, global.json/nuget.config templates)
libs/                           shared libraries solution → NuGet (Primitives, BuildingBlocks, Web, TestSupport; tests/)
artifacts/nuget/                local package feed (make pack; not in git)
services/                       each service has its own solution (Maksov.VideoService.<Svc>.slnx, Directory.*, global.json, nuget.config, Dockerfile, tests/)
  gateway/ videos/ users/ analysis/ comments/   gateway and services (HTTP services have: WebApi, Bll, Dal, Features.<Area>; videos also Domain, Features.Feed, MediaWorker; analysis — Worker)
  feature-worker/ feed/         later
data/{flink-jobs,spark-jobs,dags}
clients/{web,android}
deploy/{compose,postgres (DB per service),keycloak (realm + phone SPI),helm,observability}
tests/{integration,contract,load,security,fixtures/videos}
private/                        proprietary ML work (not in git)
Makefile                        up | up-ml | down | env-sync | test | test-dotnet | lint | demo | pack
```

## Commands

- `make up` / `make down` — start / stop the local stack; `make up-ml` — the same with the local vision model (Ollama, profile `ml`).
- `make verify-auth` — check sign-in/registration in Keycloak; `make demo` — end-to-end scenario (vertical video → Ready → description → profile → second user `demo-friend` → follow → feed → comments → deletion → isolation).
- `make pack` — NuGet packages `libs/*` (`Primitives`, `BuildingBlocks`, `Web`, `TestSupport`) into `artifacts/nuget` (version `0.1.0-local.<hash>`); required for local service builds.
- `make test` = `test-dotnet` + `test-web`; `make test-dotnet` packs libs and runs tests of each solution (`libs` and services), a temporary Postgres starts automatically; `make lint` — `-warnaserror` build and `dotnet format` for each solution + eslint/tsc.
- `make env-sync` — appends to `.env` variables that appeared in `.env.example`; `make up` depends on it.

## Lesson order

1. Video upload: web + gateway + services videos/users/analysis + Keycloak + MinIO + transcoding (DASH/HLS, ABR) + "my videos" + video description + profile. *(current)*
2. Profiles, posts, likes; multipart upload with resume.
3. Deeper Media Worker: validation, video bombs, previews, loudness, reference clips.
4. Events in Kafka, schemas, outbox, idempotency.
5. Feed: hot storage, fan-out.
6. Flink: aggregates, trends.
7. Data lake and Spark: marts, data quality.
8. Embeddings of video, music, hashtags; Qdrant.
9. Recommendations: candidates, ALS, two-tower, ranking.
10. Android: player, upload, view events.
11. Kubernetes, Helm, KEDA.
12. Observability, load, chaos tests.

Each lesson works without the following ones.
