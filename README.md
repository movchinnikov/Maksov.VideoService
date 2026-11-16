# VideoService

A tutorial series: an Instagram-class video social network on **.NET 11**, fully local.
Each lesson is a git tag `lesson-NN` with working code, tests and the lesson text in [docs/lessons](docs/lessons).

| Lesson | Text | State |
| --- | --- | --- |
| 1 | [Upload, transcoding, profiles, comments](docs/lessons/01-upload-transcode-watch.md) | tag `lesson-01`: gateway, videos, users (with follows), analysis, comments; the feed is built inside `videos` |
| 2 | [Friends, recommended friends on pgvector, feed service](docs/lessons/02-friends-feed-pgvector.md) | follows move into the new `friends` service (PostgreSQL + pgvector), the feed becomes the stateless `feed` service returning ids, `videos` serves cards |
| 3 | [Likes, domain events, GraphQL](docs/lessons/03-likes-events-graphql.md) | **current**: the `likes` service (domain [Engagement](docs/domains/engagement.md)), comment and like domain events through a shared outbox (the `lake` subscriber waits as a backlog), GraphQL at the gateway |
| 4 | Data lake ingestion *(planned)* | a `lake-ingest` service receives the events (including the `lake` backlog of likes and comments) and writes the bronze layer to MinIO as JSON Lines, idempotent by `eventId` |

This README describes the **current main state**. The parts introduced in lesson 3 (`likes`, comment/like events, GraphQL) are marked *(lesson 3)*. The parts introduced in lesson 2 — the `friends` and `feed` services, pgvector and the changed feed/follow API — are marked *(lesson 2)*; the `lesson-01` tag is the state before them.

## Quick start

You need: Docker Desktop (≥ 8 GB RAM for the VM; with `make up-ml` ≥ 10 GB), `make`, Node.js 24+ (for `make test-web`), `python3` and `curl` (for `make demo`).
A local .NET SDK is **not required**: builds and tests run in containers.

```bash
make up      # start the stack (first run ~3–5 min: image builds); make env-sync first appends new variables to .env
make demo    # end-to-end scenario: vertical video → Ready → description → profile → follow → feed → comments → likes → GraphQL → events for the lake → deletion
make verify-auth  # sign-in and sign-up in Keycloak: login, e-mail, phone
make pack    # NuGet packages of the shared libraries (needed to build services locally)
make test    # all tests (.NET per solution + web)
make down    # stop and delete data
make up-ml   # (optional) stack with a local vision model for frame captioning
```

> **Secrets are local only.** `make up` runs `scripts/gen-env.sh`, which creates a git-ignored `.env` and fills every password/key (databases, MinIO, Keycloak admin, internal API key, demo user) with random values generated on your machine. Nothing secret is stored in the repository; `.env.example` contains only empty placeholders.

Open http://localhost:3000, sign in as **demo** with the `DEMO_PASSWORD` from your local `.env` — or by phone **+70000000001** with the same password — or sign up (a phone in the format `+79991234567` is required), and upload a video.

> Open the web app via `localhost` or `127.0.0.1` — both are allowed in Keycloak (redirect URI) and in MinIO CORS. Add any other host (for example, a LAN IP) to `deploy/keycloak/realm-videoservice.json` and `WEB_ORIGINS` in `.env`; the realm is imported only into an empty Keycloak — after editing run `make down && make up`.

| Service | Address | Access |
| --- | --- | --- |
| Web "Plyonka" | http://localhost:3000 | `demo` / `DEMO_PASSWORD` from `.env` |
| Gateway (single API entry point) | http://localhost:8081 | Keycloak JWT |
| Keycloak | http://localhost:8080 | admin / from `.env` |
| MinIO Console | http://localhost:9001 | from `.env` |
| PostgreSQL | localhost:5433 | from `.env` (databases `videos`, `users`, `analysis`, `comments`, `friends`, `likes`; pgvector build of PostgreSQL 18) |
| Ollama (`make up-ml` only) | http://localhost:11434 | no auth, localhost only |

The `videos`, `users`, `analysis`, `comments`, `friends`, `feed`, `likes` services publish no ports — only the gateway talks to them.

### Services

| Service | Folder | Purpose | Storage | CPU / RAM limit |
| --- | --- | --- | --- | --- |
| `web` | `clients/web` | React, nginx | — | 0.25 / 64 MB |
| `gateway` | `services/gateway` | YARP: JWT validation, rate limit, routing; *(lesson 3)* `POST /graphql` (HotChocolate, project `Gateway.GraphQL`) | — | 0.75 / 256 MB |
| `videos` | `services/videos` | upload, statuses, "my videos", DASH/HLS delivery, video cards (`GET /api/videos/cards`), profile grid, deletion, internal "ready videos by owners" query | DB `videos`: `videos`, `transcode_jobs`, `outbox` | 1.0 / 256 MB |
| `media-worker` | `services/videos/Maksov.VideoService.Videos.MediaWorker` | CMAF transcoding, outbox delivery to subscribers (part of the videos service) | DB `videos`, MinIO | 2.0 / 1.5 GB |
| `users` | `services/users` | profiles, people search, batch lookup by ids, internal existence check | DB `users`: `profiles` | 0.5 / 192 MB |
| `analysis` | `services/analysis` | event intake, description API | DB `analysis`: `analyses` | 0.5 / 256 MB |
| `analysis-worker` | `services/analysis` | ffmpeg video analysis, optional frame captioning | DB `analysis`, MinIO | 1.0 / 768 MB |
| `comments` | `services/comments` | video comments, event intake; *(lesson 3)* publishes `comment.added/deleted` | DB `comments`: `comments`, `known_videos`, `outbox` | 0.5 / 192 MB |
| `likes` *(lesson 3)* | `services/likes` | likes of videos and comments, counters, likers; event intake; publishes `video.liked/unliked`, `comment.liked/unliked` | DB `likes`: `likes`, `like_counts`, `known_videos`, `known_comments`, `outbox` | 0.5 / 192 MB |
| `friends` *(lesson 2)* | `services/friends` | follows, stats/status, recommended friends (graph embeddings in pgvector) | DB `friends`: `follows`, `user_embeddings` (pgvector) | 0.5 / 192 MB |
| `feed` *(lesson 2)* | `services/feed` | feed as a page of video ids (stateless) | — | 0.5 / 160 MB |
| `*-migrator` | service images | one-off DB migrations (`videos`, `users`, `analysis`, `comments`, `friends`, `likes`) | — | up to 0.5 / 256 MB |
| `postgres` · `minio` · `keycloak` | `deploy/` | infrastructure (`postgres` is the pinned `pgvector/pgvector` image since lesson 2) | — | 1.0 / 512 MB · 1.0 / 512 MB · 1.5 / 1 GB |
| `ollama` (profile `ml`) | — | local vision model | volume `ollama-data` | 4.0 / 4 GB |

## What works

```mermaid
flowchart LR
  B[Browser] -- PKCE --> K[Keycloak]
  B -- presigned PUT --> M[(MinIO)]
  B -- /api --> G[gateway]
  G --> V[videos]
  G --> U[users]
  G --> F[feed]
  G --> FR[friends]
  G -- GET description --> A[analysis]
  G -- comments --> C[comments]
  G -- likes --> L[likes]
  V -- video.processed / deleted --> L
  C -- comment.added / deleted --> L
  L --> PL[(likes DB)]
  F -- following-ids --> FR
  F -- ready-by-owners --> V
  FR -- profile exists --> U
  FR --> PF[(friends DB, pgvector)]
  V -- job + NOTIFY --> PV[(videos DB)]
  W[media-worker + ffmpeg] -- SKIP LOCKED --> PV
  W -- CMAF: DASH + HLS --> M
  W -- outbox: video.processed / video.deleted --> A
  W -- outbox: video.processed / video.deleted --> C
  A --> PA[(analysis DB)]
  AW[analysis-worker + ffmpeg] -- SKIP LOCKED --> PA
  AW -- reads source --> M
  U --> PU[(users DB)]
  C --> PC[(comments DB)]
  V -- manifests and segments --> G
```

- Sign-in via Keycloak (OIDC + PKCE) **by login, e-mail or phone**; sign-up with a phone (unique, E.164); Google and Microsoft are connected with `make idps` (keys in `.env`).
- File upload **straight into MinIO** via presigned URL with progress.
- Transcoding into a 240p–1080p ABR ladder, **one set of CMAF segments and two manifests — DASH and HLS**.
- "My videos" with live statuses; Shaka player with DASH/HLS and quality switching.
- **Vertical video (9:16) is the priority**; horizontal is supported ([ADR 0007](docs/adr/0007-vertical-first.md)).
- **Automatic video description:** once a video becomes `ready`, the event reaches the `analysis` service via the outbox; the analysis worker examines the video with ffmpeg tools and builds a text and tags (in Russian).
- **User profile:** name and "about" (`/api/users/me`).

### Social features

| What | How |
| --- | --- |
| Web bottom navigation | "Feed · Search · ＋ · Profile" |
| People search | `GET /api/users?query=` — by login or name prefix, case-insensitive; `GET /api/users?ids=a,b` — batch lookup |
| Follows *(lesson 2: moved from `users` to `friends`)* | `PUT` / `DELETE /api/users/{id}/follow` — served by `friends` (idempotent, cannot follow yourself, the profile must exist) |
| Profile *(counters and relation flags: lesson 2)* | `GET /api/users/{id}`: login, name, "about"; counters and the relation to you (`isFollowing`, `followsYou`, `isMutual`) — `GET /api/friends/{id}/stats`; follower and following lists with paging and the same flags — `GET /api/friends/{id}/followers` and `/following`; relation of many users — `GET /api/friends/status?ids=` (`following`, `followedBy`); video grid — `GET /api/users/{id}/videos`. The web app shows "Mutual" / "Follows you" badges and has the pages `/u/:id/followers` and `/u/:id/following` |
| Recommended friends *(lesson 2)* | `GET /api/friends/recommendations`: friends of friends, ranked by cosine similarity of graph embeddings (pgvector, baseline `graph-hash-v1`), then by mutual friends |
| Feed *(lesson 2: the `feed` service; in lesson 1 `GET /api/feed` lived in `videos` and returned cards)* | two calls: `GET /api/feed` returns a page of video ids (followed users + your own, newest first), then `GET /api/videos/cards?ids=` returns the cards in that order; follows are cached in `feed` for 20 s, when `friends` is unavailable the feed shows only your own videos |
| Likes *(lesson 3)* | `PUT`/`DELETE /api/videos/{id}/like` and `.../comments/{commentId}/like` (idempotent, 204; a video or comment becomes likeable after the `processed` / `comment.added` event reaches `likes`, up to ~30 s); counters `GET /api/likes/videos|comments?ids=` (≤ 50 ids, `count` + `likedByMe`); likers `GET /api/videos/{id}/likes`. Web: heart under a video and a comment, double tap on the media, likers page `/v/:id/likes` |
| Comments | `GET`/`POST /api/videos/{id}/comments`, `DELETE …/{commentId}`: the comment author or the video owner can delete; only ready videos can be commented on (the comments service learns about them from an event, up to 30 s) |
| Deletion | `DELETE /api/videos/{id}` (owner only, 204): cleans up MinIO, description and comments (`video.deleted.v1` event) |
| Visibility | ready videos are visible to any authenticated user; non-ready and deleted ones are not (404) |
| "Interesting videos" | deferred until embeddings and recommendations (lessons 10–11) |

### GraphQL *(lesson 3)*

The gateway serves `POST /graphql` (queries and mutations, no subscriptions) next to the REST routes; one request can return a video with its owner, like counters and comments. Same Keycloak token as REST; the gateway forwards it to the services. Limits: depth 8, page size ≤ 50, body ≤ 64 KiB, 300 requests per user per minute. Introspection is on only locally (`GraphQL__AllowIntrospection=true` in compose). Schema — [contracts/graphql/gateway.graphql](contracts/graphql/gateway.graphql), decision — [ADR 0012](docs/adr/0012-graphql-at-the-gateway.md).

```bash
curl -s -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' http://localhost:8081/graphql -d '{
  "query": "query($id: UUID!){ video(id:$id){ title owner{ username } likes{ count likedByMe } comments(limit:5){ items{ text author{ username } likes{ count } } } } }",
  "variables": {"id": "<video id>"} }'
```

Decisions — [ADR 0009](docs/adr/0009-social-features-comments.md) (visibility, comments, deletion) and [ADR 0010](docs/adr/0010-friends-and-feed-services.md) (lesson 2) (friends and feed services).

### How to view a video description

The description appears a few seconds after `ready`. Statuses: `queued` → `processing` → `done` (or `failed`; this does not affect the video). The owner sees any status, other users only `done`.

```bash
TOKEN=$(curl -s http://localhost:8080/realms/videoservice/protocol/openid-connect/token \
  -d grant_type=password -d client_id=videoservice-cli -d username=demo -d password="$DEMO_PASSWORD" | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')
curl -s -H "Authorization: Bearer $TOKEN" http://localhost:8081/api/videos/<id>/description
# {"videoId":"…","status":"done","text":"…","tags":["…"],"updatedAt":"…"}
```

The description is a set of "tool readings": scene changes, brightness, saturation, hue, motion, sound and pauses. What is actually depicted becomes known only with the vision model enabled: `make up-ml` starts a local Ollama (default model `moondream`, the first start downloads the model) and captions a few frames. The model is off by default. The same data is shown in the UI, on the video page.

Decisions: [ADR 0001](docs/adr/0001-lesson-01-upload-transcode-watch.md), [0002](docs/adr/0002-cmaf-dash-and-hls.md), [0003](docs/adr/0003-keycloak-identity.md), [0004 — solution structure (partially superseded by 0008)](docs/adr/0004-solution-structure.md), [0005 — phone sign-in](docs/adr/0005-phone-login-keycloak.md), [0006 — services and gateway](docs/adr/0006-microservices-gateway-videos-users-analysis.md), [0007 — vertical video](docs/adr/0007-vertical-first.md), [0008 — separate solutions and NuGet](docs/adr/0008-separate-solutions-nuget.md), [0009 — social features and comments](docs/adr/0009-social-features-comments.md), [0010 — friends and feed services, lesson 2](docs/adr/0010-friends-and-feed-services.md), [0011 — likes service and domain events, lesson 3](docs/adr/0011-likes-service-and-domain-events.md), [0012 — GraphQL at the gateway, lesson 3](docs/adr/0012-graphql-at-the-gateway.md). The domain map is in [docs/domains](docs/domains/README.md).

Contracts: [public API](contracts/openapi/api.yaml); internal APIs — [analysis](contracts/openapi/internal-analysis.yaml), [comments](contracts/openapi/internal-comments.yaml), [users](contracts/openapi/internal-users.yaml), [friends](contracts/openapi/internal-friends.yaml), [videos](contracts/openapi/internal-videos.yaml), [likes](contracts/openapi/internal-likes.yaml); GraphQL — [gateway.graphql](contracts/graphql/gateway.graphql); events — [video.processed.v1](contracts/events/video-processed.v1.json), [video.deleted.v1](contracts/events/video-deleted.v1.json), [comment.added.v1](contracts/events/comment-added.v1.json), [comment.deleted.v1](contracts/events/comment-deleted.v1.json), [video.liked.v1](contracts/events/video-liked.v1.json), [video.unliked.v1](contracts/events/video-unliked.v1.json), [comment.liked.v1](contracts/events/comment-liked.v1.json), [comment.unliked.v1](contracts/events/comment-unliked.v1.json).

## Commands

| Command | What it does |
| --- | --- |
| `make up` / `make down` | start / tear down the stack; `up` depends on `env-sync` |
| `make env-sync` | append to `.env` variables that appeared in `.env.example` (existing values are untouched) |
| `make up-ml` | stack + local vision model (Ollama, profile `ml`) for frame captioning |
| `make demo` | end-to-end scenario: vertical 720×1280 video → Ready → description → profile → second user `demo-friend` → follow → feed → comments → deletion → isolation and `/internal` being closed |
| `make pack` | NuGet packages `libs/*` (`Primitives`, `BuildingBlocks`, `Web`, `TestSupport`) into `artifacts/nuget`, version `0.1.0-local.<hash>` (`VERSION_SUFFIX=ci.42` adds a suffix) |
| `make test` | `test-dotnet` + `test-web` |
| `make test-dotnet` | `make pack`, then tests of each solution (`libs` and services; a temporary Postgres starts automatically; ffmpeg integration runs in a container) |
| `make test-web` | web (vitest) |
| `make lint` | build with `-warnaserror` and `dotnet format` per solution, eslint, tsc |
| `make verify-auth` | Keycloak sign-in/sign-up "as in the browser": login, e-mail, phone, duplicate number, redirect_uri |
| `make idps` | connect Google / Microsoft SSO in Keycloak |
| `make logs s=media-worker` | service logs |

## Structure

```
build/                    shared MSBuild settings: Common.props/targets, Common.Packages.props, global.json and nuget.config templates
libs/                     shared libraries solution (NuGet): Primitives, BuildingBlocks (Postgres, S3, OTel, ProcessRunner, MediaKeys), Web (JWT, HTTP helpers, internal-call key), TestSupport; tests/
artifacts/nuget/          local package feed (make pack; not in git)
services/
  gateway/                YARP gateway: JWT, rate limit, routes (appsettings.json); Gateway.GraphQL project *(lesson 3)* — its own solution
  videos/                 video service: WebApi · Bll · Dal · Domain · Features.Videos · Features.Catalog (cards, profile grid, internal query) · MediaWorker (+ tests/)
  users/                  profiles service: WebApi · Bll · Dal · Features.Profiles (+ tests/)
  friends/                social graph and recommendations: WebApi · Bll (incl. GraphEmbedding) · Dal (pgvector) · Features.Friends (+ tests/)
  likes/                  likes service *(lesson 3)*: WebApi · Bll · Dal · Features.Likes (+ tests/)
  feed/                   feed (video ids, stateless): WebApi · Bll · Dal (HTTP clients) · Features.Feed (+ tests/)
  analysis/               descriptions service: WebApi · Bll · Dal · Features.Descriptions · Worker with ffmpeg (+ tests/)
  comments/               comments service: WebApi · Bll · Dal · Features.Comments (+ tests/)
                          in each service folder: Maksov.VideoService.<Service>.slnx, Directory.*, global.json, nuget.config, Dockerfile
clients/web               React + TypeScript + Vite, Shaka Player
contracts/{openapi,events,graphql}  contracts: public and internal APIs, event schemas, GraphQL schema
deploy/compose            lesson docker-compose
deploy/postgres           init script: a database and role per service (+ `create extension vector` in `friends`)
deploy/keycloak           realm, phone sign-in/sign-up provider (Java), theme, SSO script
docs/                     adr, domains, stories, design, security, slo, lessons
scripts/                  demo.sh (end-to-end scenario), verify-auth.py (sign-in and sign-up)
```

### How NuGet packages and the build work

Each service is a separate solution, shared code arrives as packages from the local feed ([ADR 0008](docs/adr/0008-separate-solutions-nuget.md)):

- `make pack` builds `libs/*` into `artifacts/nuget` with version `0.1.0-local.<source hash>`; in services the version is floating (`0.1.0-local.*`), and the service's `nuget.config` points at the feed `../../artifacts/nuget`.
- If you build a service manually (`dotnet build` in `services/<service>`), run `make pack` first, otherwise package restore for `Maksov.VideoService.*` fails. If you changed `libs/`, run `make pack` again.
- The Docker build context is the repository root; the first Dockerfile stage packs `libs/`, the second builds the service.
- The shared .NET version and analyzers come from `build/Common.props`; third-party package versions — from `build/Common.Packages.props`.


## Troubleshooting

| What you need | Where to look |
| --- | --- |
| Why a video has the "defective" status | `docker exec videoservice-postgres-1 psql -U videos -d videos -c "select v.title, v.status, v.failure_reason, j.attempts, j.last_error from videos v left join transcode_jobs j on j.video_id = v.id order by v.created_at desc limit 5;"` — `last_error` holds the full ffmpeg output |
| Service logs | `make logs s=media-worker` (or `gateway`, `videos`, `users`, `analysis`, `analysis-worker`, `comments`, `friends`, `feed`, `keycloak`) |
| 401 on `/api/*` with a valid token | the service behind the gateway validates the JWT itself: `make logs s=gateway` and `s=videos`/`users`/`analysis`/`comments`/`friends`/`feed`; check issuer/audience (`videoservice-api`) |
| `/internal/**` returns 404 | by design: internal endpoints are not published through the gateway |
| Files in storage | MinIO Console http://localhost:9001 → bucket `media`: `uploads/<id>/source` — the source, `videos/<id>/stream/` — segments and manifests |
| MinIO login/password | `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` from `.env` |
| Keycloak admin | http://localhost:8080 → `KC_BOOTSTRAP_ADMIN_USERNAME` / `KC_BOOTSTRAP_ADMIN_PASSWORD` from `.env` |
| The description did not appear (video `ready`) | 1) was the event sent? in DB `videos`: `select id, attempts, sent_at, last_error from outbox order by created_at desc limit 5;` (one row per subscriber — column `subscriber`; 401 — `INTERNAL_API_KEY` mismatch, connection refused — `analysis` is not up; without `Outbox__Subscribers__<name>__BaseUrl` addresses the dispatcher does not start). 2) accepted and processed? in DB `analysis`: `select video_id, status, attempts, last_error from analyses order by created_at desc limit 5;`. 3) `make logs s=analysis-worker`. Details — [runbook](docs/slo/lesson-01.md) |
| Cannot comment: "video not found" | the comments service registers a video from the `video.processed.v1` event (up to 30 s after `ready`): in DB `videos` — `select subscriber, sent_at, last_error from outbox order by created_at desc limit 5;`, in DB `comments` — `select * from known_videos;`. Details — [runbook](docs/slo/lesson-01.md) |
| The feed is empty | follows (DB `friends`: `select * from follows where follower_id = '<sub>';`), video status (only `ready` appear), follows are cached in `feed` for 20 s; when `friends` is unavailable only your own videos are returned — [runbook](docs/slo/lesson-02.md) |
| A like returns 404 | `likes` learns about a video from `video.processed.v1` (up to ~30 s after `ready`): DB `likes` — `select * from known_videos;`, DB `videos` — `select subscriber, sent_at, last_error from outbox where subscriber = 'likes' order by created_at desc limit 5;`. [Runbook](docs/slo/lesson-03.md) |
| Events are not delivered | unsent rows per subscriber in the producer's DB: `select subscriber, topic, count(*) from outbox where sent_at is null group by 1, 2;` — rows of `lake` wait by design until lesson 4. [Runbook](docs/slo/lesson-03.md) |
| GraphQL returns `UNAVAILABLE`, 401 or 429 | a downstream service is down (`make ps`), the token expired, or the 300/min budget is spent; introspection works only with `GraphQL__AllowIntrospection=true` — [runbook](docs/slo/lesson-03.md) |
| Following fails with 503 | `friends` cannot confirm the profile with `users` (`make logs s=friends`, check `users` and `INTERNAL_API_KEY`) |
| Recommended friends are empty | the viewer follows nobody, or friends of friends are all already followed; check `user_embeddings` in DB `friends` (rows carry `model_version = graph-hash-v1`) |
| `NU1101` / package `Maksov.VideoService.*` not found when building a service | `make pack` (the `artifacts/nuget` feed is empty or stale) |
| `docker compose`: "required variable … is missing" after an update | `make env-sync` (called by `make up`) |
| Retry processing of a failed video | `update videos set status='uploaded', failure_reason=null where id='…'; update transcode_jobs set status='queued', attempts=0, run_after=now(), locked_until=null, locked_by=null where video_id='…'; select pg_notify('transcode_jobs','retry');` |

## Resources

Limits are set for every container; the total without Ollama is ≈ 11.5 CPU / 6.0 GB RAM (≈ 11 CPU / 5.8 GB at the `lesson-02` tag, ≈ 10 CPU / 5.4 GB at `lesson-01`; with `make up-ml` — another 4 CPU / 4 GB). Details — [docs/slo/lesson-01.md](docs/slo/lesson-01.md) [docs/slo/lesson-02.md](docs/slo/lesson-02.md) and [docs/slo/lesson-03.md](docs/slo/lesson-03.md).

## Web development without Docker

```bash
make up                       # backend in containers
cd clients/web && npm ci && npm run dev   # http://localhost:5173, /api is proxied to :8081
```

## Configuration

All configuration is via environment variables; the template is [.env.example](.env.example) (`make up` appends missing variables to `.env`: `make env-sync`). Secrets are not committed to the repository.

> MinIO no longer publishes official images to Docker Hub/Quay. A Chainguard build pinned by digest is used; it can be replaced with the `MINIO_IMAGE` variable.
