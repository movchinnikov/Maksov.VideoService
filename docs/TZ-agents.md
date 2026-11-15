# Specification for Antigravity: an agent team for a .NET video social network

This document describes the project, constraints, roles, skills, workflows, and repository structure. Task for Antigravity: build a set of rules, skills, workflows, and subagents from it.

## 1. Project context

- **Goal**: a series of educational materials for developers, built around an Instagram-like product centered on video.
- **Backend**: .NET (ASP.NET Core, Worker Service). **Client**: Android (Kotlin, Media3/ExoPlayer).
- **Event processing and big data**: Kafka, Flink, Spark, data lake.
- **Hard constraint**: everything must deploy locally. No cloud services.
- **Deliverable**: each lesson in the series is a reproducible git tag with working code, tests, documentation, and the lesson text.

## 2. Stack under the "local only" constraint

| Purpose | Choice | Replaces |
| --- | --- | --- |
| Object storage | MinIO | AWS S3 |
| Event bus | Kafka (Strimzi or Redpanda in docker-compose) + Schema Registry | MSK |
| OLTP | PostgreSQL | RDS |
| Hot feed, counters | Tarantool (or Redis as a simplified option) | Elasticache |
| Analytical storage | ClickHouse or DuckDB + Iceberg | Snowflake |
| Stream processing | Flink | Kinesis Analytics |
| Batch and training | Spark | EMR |
| Batch orchestration | Airflow or Dagster | MWAA |
| Vector search | Qdrant (or Milvus, pgvector) | cloud vector DBs |
| Cluster | kind / k3d / minikube, Helm | EKS |
| Observability | OpenTelemetry, Prometheus, Grafana, Loki | CloudWatch |
| CI | GitHub Actions or a local runner (act) | cloud CI |

The first lessons run on docker-compose; Kubernetes is introduced later.

## 3. High-level architecture

1. **API service (.NET)**: registration, profile, video upload, likes, comments, follows.
2. **Media Worker (.NET + ffmpeg)**: transcoding, HLS, previews.
3. **Feature Worker (Python or .NET + ONNX)**: embeddings for video, audio, text, hashtags.
4. **Feed Service (.NET)**: serves the feed from hot storage, blends candidates.
5. **Stream Jobs (Flink)**: aggregates of views, likes, watch-through, trends; updates the hot view.
6. **Batch Jobs (Spark)**: ALS, two-tower, segmentation, lake recomputation.
7. **Data Platform**: MinIO (lake), ClickHouse (analytics), Qdrant (vectors).
8. **Android client**: feed, HLS player, upload, view events.

### Core events (Kafka)

- `VideoUploaded`, `VideoProcessed`, `VideoFailed`
- `VideoEmbedded` (contains `model_version` and links to vectors)
- `VideoViewed` (with watch ratio), `VideoLiked`, `CommentAdded`, `UserFollowed`
- `FeedRecomputed`

All events are described in Avro or Protobuf; schemas live in the repository and are registered in Schema Registry. Compatibility rule: only backward-compatible changes. Every event contains `event_id`, `occurred_at`, `schema_version`, `trace_id`.

## 4. Roles and skills

Each role is a separate agent (or a set of skills) with its own instructions, inputs, outputs, and definition of done.

### 4.1 Architect

- **Skills**: C4 diagrams, ADRs, service boundaries, sync vs async, write-time vs read-time fan-out for the feed, load estimation, storage selection, consistency between stores.
- **Input**: lesson or feature goal. **Output**: ADR, diagram, list of affected services and events.
- **Done when**: the decision is recorded in `docs/adr/`, with alternatives and reasons for rejecting them.

### 4.2 Systems analyst

- **Skills**: user stories, acceptance criteria, OpenAPI, event schemas (Avro/Protobuf), state diagrams (e.g. video lifecycle), NFRs for latency and reliability.
- **Input**: ADR. **Output**: API and event contracts, acceptance criteria.
- **Done when**: contracts are in `contracts/`, pass compatibility checks, and every story has verifiable criteria.

### 4.3 Backend developer (.NET)

- **Skills**: ASP.NET Core, EF Core and Dapper, Confluent.Kafka, outbox pattern, idempotent handlers, Polly, OpenTelemetry, clean solution structure.
- **Input**: contracts and acceptance criteria. **Output**: code, migrations, configs.
- **Done when**: the code passes build, linters, and all tests, and the feature runs locally.

### 4.4 Video engineer (media pipeline)

- **Skills**: FFmpeg and ffprobe, ladder transcoding (240p-1080p), H.264/H.265/AV1, AAC, HLS/DASH (2-6 s segments, master playlist), CRF and presets, keyframes and GOP, previews and posters, loudness normalization, hardware acceleration when a GPU is available, input validation (format, duration, orientation, corrupt files, "video bomb" protection), running ffmpeg from .NET (FFMpegCore) with timeouts and progress parsing.
- **Input**: `VideoUploaded`. **Output**: renditions, HLS, previews in MinIO, `VideoProcessed` or `VideoFailed` events.
- **Done when**: processing is idempotent, CPU- and memory-limited, and passes a set of reference clips (vertical, rotated, no audio, corrupt, extremely long).

### 4.5 ML engineer: embeddings and recommendations

- **Skills**:
  - Visual: CLIP/SigLIP on keyframes, scene detection, pooling across frames; VideoMAE/X-CLIP for actions.
  - Audio and music: CLAP or MERT, track fingerprinting (Chromaprint), Whisper for speech.
  - Text and hashtags: multilingual embeddings (bge-m3, multilingual-e5), hashtag embeddings based on co-occurrence.
  - Modality fusion: normalization, concatenation with projection or a small trainable network, modality weights based on metrics.
  - Vector search: Qdrant, HNSW index, metadata filters (language, region, age).
  - Recommendations: content vectors for cold start, two-tower and ALS on behavior, candidate generation and ranking.
  - Inference: Python service (FastAPI) in a container, ONNX Runtime from .NET as an advanced option.
  - Evaluation: recall@k, offline metrics, labeled "similar/dissimilar" pairs, A/B simulation.
- **Input**: `VideoProcessed`, frames and audio in MinIO. **Output**: vectors in Qdrant, `VideoEmbedded` event.
- **Done when**: every vector has a `model_version`, a recompute procedure exists, quality metrics are recorded, and the embedding is stable for the same video at different resolutions.

### 4.6 Data engineer

- **Skills**: Flink (windows, state, watermarks, exactly-once, checkpoints), Spark (batch, ALS), bronze/silver/gold layers, Parquet and Iceberg formats, orchestration (Airflow or Dagster), data quality checks (Great Expectations or Soda), handling late and duplicate events.
- **Input**: Kafka events. **Output**: marts, aggregates, training datasets.
- **Done when**: jobs are reproducible, tested on test streams, and lag is monitored.

### 4.7 DBA and data models

- **Skills**: PostgreSQL (indexes, partitioning, zero-downtime migrations), Tarantool (spaces, indexes, feed storage), ClickHouse (sort keys, materialized views), consistency rules between stores, backup and restore strategies.
- **Input**: ADR and contracts. **Output**: schemas, migrations, index rationale.
- **Done when**: migrations are reversible and pass on both empty and populated databases, and query plans are verified.

### 4.8 DevOps and SRE

- **Skills**: Docker, docker-compose, Helm, kind/k3d, Strimzi for Kafka, KEDA (scaling workers by lag), Prometheus, Grafana, Loki, OpenTelemetry Collector, resource limits, a "bring everything up with one command" script.
- **Input**: list of services and resources. **Output**: manifests, charts, dashboards, alerts.
- **Done when**: `make up` brings the stack up from scratch, `make down` stops it cleanly, and resources fit on an ordinary workstation.

### 4.9 QA and test automation

- **Skills**: xUnit, integration tests with Testcontainers, contract tests (Pact and schema checks), load testing (k6), Flink stream tests, data quality checks, chaos tests (kill a worker mid-transcode, lose the broker).
- **Input**: acceptance criteria. **Output**: tests, coverage report, load scenarios.
- **Done when**: every acceptance criterion is covered by at least one test.

### 4.10 Reviewer

- **Skills**: checklists, Roslyn analyzers, SonarQube, spotting idempotency problems, races, resource leaks, unsafe migrations, contract violations.
- **Input**: diff. **Output**: findings with priority (blocking, major, nit).
- **Done when**: there are no blocking findings.

### 4.11 Android developer

- **Skills**: Kotlin, Jetpack Compose, Media3/ExoPlayer (HLS), upload via presigned URL with resume, preloading the next video, sending view events, offline cache.
- **Done when**: the player switches quality and view events are sent with the watch ratio.

### 4.12 Instructional designer and technical writer

- **Skills**: lesson structure, goal and outcome, step-by-step walkthrough, common mistakes, self-check exercises, a git tag per lesson, README with run instructions.
- **Done when**: a person starting from scratch can reproduce the result from the lesson text.

## 5. Cross-cutting rules (rules for all agents)

1. Contract first, then code. Event schema changes must be backward compatible.
2. Every event handler is idempotent and tolerates redelivery.
3. Everything runs locally. If a solution requires the cloud, a local replacement is proposed.
4. No secrets in the repository; configuration through environment variables and `.env.example`.
5. Every feature includes tests, documentation updates, and goes through review.
6. Versioning: embeddings and schemas have an explicit version.
7. Resource limits are specified explicitly for every container.
8. Architecture decisions are recorded in ADRs, not in chat.
9. An agent does not change another's area of responsibility without a request through a shared artifact (contract, ADR, task).
10. The "done" criterion from the role's section is mandatory.

## 6. Workflows

### 6.1 Main loop: "feature or lesson"

1. **Architect**: ADR and diagram.
2. **Analyst**: API and event contracts, acceptance criteria.
3. **QA**: tests against the contracts (red).
4. **DBA**: schemas and migrations, if needed.
5. **Developers** (backend, video, ML, data, Android): implementation.
6. **Reviewer**: checklist review, send back for rework.
7. **DevOps**: verify that everything comes up with one command.
8. **QA**: full run, load testing if needed.
9. **Instructional designer**: lesson text, git tag.

### 6.2 New event

The analyst describes the schema, compatibility is checked, QA writes a contract test, producer and consumer are implemented in parallel, the reviewer checks idempotency, DevOps adds lag monitoring.

### 6.3 Video upload and processing

The client gets a presigned URL and uploads to MinIO, the API publishes `VideoUploaded`, the Media Worker creates renditions, HLS, and previews and publishes `VideoProcessed`, the Feature Worker computes embeddings and publishes `VideoEmbedded`, Flink and Spark attach the video to the feed and recommendations.

### 6.4 Embedding model change

The ML engineer bumps `model_version`, runs a background recompute into a new Qdrant collection, compares metrics with the old one, switches the collection alias, and deletes the old collection after an observation period.

### 6.5 Review

Input: diff and related contracts. Checks: security, idempotency, migrations, event schemas, resources, tests, documentation. Output: a list of findings with priorities.

### 6.6 Incident and postmortem

Reproduction, a test for the bug, the fix, review, an entry in `docs/postmortems/`, and checklist updates if the problem could have been caught earlier.

## 7. Repository structure

```
/
├── .agent/
│   ├── rules/            # cross-cutting rules (section 5)
│   ├── skills/           # one folder per skill, with SKILL.md inside
│   │   ├── architect/
│   │   ├── analyst/
│   │   ├── dotnet-backend/
│   │   ├── video-ffmpeg/
│   │   ├── ml-embeddings/
│   │   ├── data-flink-spark/
│   │   ├── dba/
│   │   ├── devops-k8s/
│   │   ├── qa-testing/
│   │   ├── code-review/
│   │   ├── android/
│   │   └── lesson-writer/
│   └── workflows/        # workflows from section 6
├── docs/
│   ├── adr/
│   ├── diagrams/
│   ├── postmortems/
│   └── lessons/
├── contracts/
│   ├── openapi/
│   └── events/           # Avro or Protobuf
├── services/
│   ├── api/
│   ├── media-worker/
│   ├── feature-worker/
│   └── feed/
├── data/
│   ├── flink-jobs/
│   ├── spark-jobs/
│   └── dags/
├── clients/android/
├── deploy/
│   ├── compose/
│   ├── helm/
│   └── observability/
├── tests/
│   ├── integration/
│   ├── contract/
│   ├── load/
│   └── fixtures/videos/
├── Makefile
└── README.md
```

The exact folder names for rules, skills, and workflows should be checked against the conventions of your Antigravity version and adjusted.

## 8. Lesson series order

1. Monolith on .NET + Postgres: profiles, posts, likes.
2. Video upload via MinIO (presigned URL, multipart).
3. Media Worker: ffmpeg, HLS, previews.
4. Events in Kafka, schemas, outbox, idempotency.
5. Feed: hot storage, fan-out.
6. Flink: view and like aggregates, trends.
7. Data lake and Spark: batch, marts, data quality.
8. Embeddings for video, music, and hashtags, Qdrant.
9. Recommendations: content candidates, ALS, two-tower, ranking.
10. Android: HLS player, upload, view events.
11. Kubernetes, Helm, scaling workers by lag.
12. Observability, load testing, chaos tests.

Each step works without the following ones.

## 9. What is needed from Antigravity

1. Create the structure from section 7.
2. For each role in section 4, build a skill (SKILL.md with skills, inputs, outputs, definition of done, and a checklist) and, where supported, a separate subagent.
3. Express the cross-cutting rules from section 5 as rules.
4. Express the workflows from section 6 as executable scenarios, stating which agent performs each step and which artifacts it passes on.
5. Assign a stronger model to the architect, ML engineer, and reviewer, and a fast model to routine code, tests, and documentation.
6. Set up shared context: ADRs, contracts, and README as the single source of truth; agents read them before work and update them afterward.
7. Prepare a `Makefile` with the targets `up`, `down`, `test`, `lint`, `demo`.
8. Start with lessons 1-3 and add the rest following the loop in section 6.1.
