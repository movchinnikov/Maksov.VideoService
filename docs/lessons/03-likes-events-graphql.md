# Lesson 3. Likes, domain events, GraphQL at the gateway

> Tag: `lesson-03` · Time: ~4 h · Builds on [lesson 2](02-friends-feed-pgvector.md); the git tag `lesson-02` is the state before everything described here. The events written here are delivered to the data lake in [lesson 4](#whats-next).

## Goal
Add the first behaviour signals of the product — **likes** of videos and comments — as a new service derived from a domain, publish **domain events** (likes and comments) through a shared transactional outbox so that the data lake can be added later without touching producers, and add a **GraphQL** layer at the gateway so that one screen needs one request instead of a waterfall of REST calls.

## Result
```bash
make up && make demo
# ...
# ▸ Likes: separate likes service (the video becomes likeable after the video.processed event, up to 30 s)
#   ✓ demo-friend liked the clip, liking twice is idempotent (204), batch counts, likers list
# ▸ GraphQL at the gateway: one query for video, owner, likes and comments
#   ✓ video + owner + likes resolved in one request, 401 without a token
# ▸ Events for the data lake: like and comment events wait in the outboxes (lesson 4 delivers them)
#   ✓ the comment text is never published (textLength only)
# Demo passed.
```
In the browser: the heart under a video (and a double tap on the media) likes it, the counter opens the likers page (`/v/:id/likes`), comments have their own hearts.

## What changes compared to lesson 2
| Area | Lesson 2 | Lesson 3 |
| --- | --- | --- |
| Reactions | comments only | + service `likes` (videos and comments), domain [Engagement](../domains/engagement.md) |
| Events | `video.processed/deleted` from videos only | + `comment.added/deleted`, `video.liked/unliked`, `comment.liked/unliked`; shared outbox in `libs/BuildingBlocks` used by `comments` and `likes` |
| API for screens | REST only | + `POST /graphql` served by the gateway ([ADR 0012](../adr/0012-graphql-at-the-gateway.md)) |
| Gateway | 192 MB | 256 MB (schema, HTTP clients, DataLoaders) |
| Containers | — | + `likes`, `likes-migrator` (0.5 CPU / 192M); total without Ollama ≈ 11.5 CPU / 6.0 GB |

Decisions: [ADR 0011](../adr/0011-likes-service-and-domain-events.md) (likes and events), [ADR 0012](../adr/0012-graphql-at-the-gateway.md) (GraphQL), domain map [docs/domains](../domains/README.md). Contracts: [api.yaml](../../contracts/openapi/api.yaml), [gateway.graphql](../../contracts/graphql/gateway.graphql), [internal-likes.yaml](../../contracts/openapi/internal-likes.yaml), [events](../../contracts/events).

```mermaid
flowchart LR
  B[Browser] --> G[gateway: REST routes + POST /graphql]
  G --> L[likes]
  G --> C[comments]
  G --> V[videos]
  G --> U[users]
  V -- video.processed / deleted --> L
  V -- video.processed / deleted --> C
  C -- comment.added / deleted --> L
  L --- PL[(likes DB)]
  C --- PC[(comments DB)]
  L -. video.liked, comment.liked .-> LAKE[lake: backlog until lesson 4]
  C -. comment.added, comment.deleted .-> LAKE
```

## Step-by-step walkthrough

### Step 1. Domain first: Engagement
Services are derived from domains, not the other way round ([domain map](../domains/README.md)). Reactions of people to content form one bounded context, **Engagement**, with its own language (target, like, liker, known video) — see [engagement.md](../domains/engagement.md). Two services come out of it: `comments` (content with its own text and deletion rules, since lesson 1) and `likes` (a tiny, write-heavy fact with counters). Anything crossing the domain border is a contract (API or event), never a shared table.

### Step 2. The shared outbox library
The write path of the transactional outbox, claiming of due rows, backoff and the HTTP publisher moved into `libs/BuildingBlocks` ([Outbox](../../libs/Maksov.VideoService.BuildingBlocks/Outbox/OutboxStore.cs): `OutboxStore`, `HttpEventPublisher`, `OutboxDispatcher`, `AddOutbox`, `EventPaths`). A service writes event rows **in the same transaction** as its state change — one row per (event, subscriber) — and a hosted dispatcher inside the service delivers them over internal HTTP with `X-Internal-Key`, retrying with backoff. Subscribers are configured as `Outbox__Subscribers__<name>__BaseUrl`; **rows of a subscriber without an address stay pending** — this is how the `lake` backlog accumulates until lesson 4. `videos` keeps its own copy until Kafka replaces the transport (known duplication).

### Step 3. Comments publish events
[CommentEvents.cs](../../services/comments/Maksov.VideoService.Comments.Bll/CommentEvents.cs) builds `comment.added.v1` and `comment.deleted.v1` for the subscribers `likes` and `lake`; the migration [0002_outbox.sql](../../services/comments/Maksov.VideoService.Comments.Dal/Postgres/Migrations/0002_outbox.sql) adds the outbox table. Rules: the comment **text is never published** (the event carries `textLength`); a repeated delete writes no second event; a failing insert writes no outbox row (one transaction); deleting a whole video cascades comments inside the services without per-comment events — the lake learns about it from `video.deleted.v1`.

### Step 4. The `likes` service
[Likes.cs](../../services/likes/Maksov.VideoService.Likes.Features.Likes/Likes.cs) serves `PUT/DELETE /api/videos/{id}/like`, `PUT/DELETE /api/videos/{id}/comments/{commentId}/like`, `GET /api/likes/videos|comments?ids=` (≤ 50 ids, unknown ids → count 0, `likedByMe` for the caller) and `GET /api/videos/{id}/likes` (likers, newest first, keyset cursor, limit ≤ 50). The migration [0001_init.sql](../../services/likes/Maksov.VideoService.Likes.Dal/Postgres/Migrations/0001_init.sql) creates `likes` (PK target type + target id + user), `like_counts` (O(1) reads, updated **in the same transaction** as the like row), the replicas `known_videos` and `known_comments`, and the `outbox`. A like is idempotent: liking twice keeps one row and emits **no second event**; unliking emits `video.unliked.v1` only if a like existed. [PostgresLikeRepository.cs](../../services/likes/Maksov.VideoService.Likes.Dal/Postgres/PostgresLikeRepository.cs) does this in one transaction; [LikeEvents.cs](../../services/likes/Maksov.VideoService.Likes.Bll/LikeEvents.cs) builds the payloads (`video.liked.v1`, `video.unliked.v1`, `comment.liked.v1`, `comment.unliked.v1`, subscriber `lake`).

### Step 5. Replicas fed by events
`likes` never calls `videos` or `comments` at request time. It learns about targets from internal intake routes ([contract](../../contracts/openapi/internal-likes.yaml), all under `X-Internal-Key`): `POST /internal/v1/events/video-processed`, `video-deleted`, `comment-added`, `comment-deleted`. Intake is idempotent; a deletion leaves a tombstone, so a late `video.processed.v1` or `comment.added.v1` cannot resurrect a deleted video or comment; deleting a video removes its likes, its comments' likes and the counters. The price is eventual consistency: a video becomes likeable after its `processed` event is delivered (up to ~30 s after `ready`; `make demo` retries).

### Step 6. Gateway routes
In [appsettings.json](../../services/gateway/Maksov.VideoService.Gateway/appsettings.json) the three public like routes (`/api/videos/{id}/like`, `/api/videos/{id}/comments/{commentId}/like`, `/api/videos/{id}/likes`) and `/api/likes/**` go to `likes`. The comment-like route is **more specific** than the comments catch-all, so it has a lower `Order` and wins. The service publishes no port; the gateway waits for its health check.

### Step 7. GraphQL at the gateway
A new project [Gateway.GraphQL](../../services/gateway/Maksov.VideoService.Gateway.GraphQL/GraphQLExtensions.cs) in the gateway solution (HotChocolate, local, open source). The gateway maps `POST /graphql` next to the YARP routes — YARP matches only `/api/**`, so the path is never proxied. The schema ([gateway.graphql](../../contracts/graphql/gateway.graphql)) has queries (`me`, `user`, `users`, `video`, `feed`, `userVideos`, `recommendations`, `followers`, `following`) and mutations (`likeVideo`, `unlikeVideo`, `likeComment`, `unlikeComment`, `addComment`, `deleteComment`, `follow`, `unfollow`); no subscriptions. Resolvers ([Resolvers.cs](../../services/gateway/Maksov.VideoService.Gateway.GraphQL/Schema/Resolvers.cs)) call the REST services through typed clients ([ServiceClients.cs](../../services/gateway/Maksov.VideoService.Gateway.GraphQL/Clients/ServiceClients.cs)) and **forward the caller's `Authorization` header** (the services validate it again — zero trust); `/internal/**` is never used.

**DataLoaders** ([DataLoaders.cs](../../services/gateway/Maksov.VideoService.Gateway.GraphQL/Schema/DataLoaders.cs)) fix N+1: a feed of 12 cards resolves owners, likes and comment authors with **one downstream call per service** (`users?ids=`, `videos/cards?ids=`, `likes/videos?ids=`, `likes/comments?ids=`); friend stats are deduplicated per user. **Protection:** maximum nesting depth 8, page sizes ≤ 50 (feed ≤ 30), request body ≤ 64 KiB, execution timeout 20 s, a per-user rate limit (300 per minute), and introspection only when `GraphQL:AllowIntrospection=true` (set for the local stack). Errors from services become GraphQL errors with `extensions.code` (`NOT_FOUND`, `VALIDATION`, `UNAVAILABLE`); partial data is allowed. The gateway stays a thin composition layer without storage.

Example (one round trip instead of five REST calls):
```bash
TOKEN=...   # as in the README
curl -s -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' http://localhost:8081/graphql -d '{
  "query": "query($id: UUID!){ video(id:$id){ title owner{ username } likes{ count likedByMe } comments(limit:5){ items{ text author{ username } likes{ count } } } } }",
  "variables": {"id": "<video id>"} }'
```

### Step 8. Web: hearts, double tap, likers
[LikeButton](../../clients/web/src/shared/ui/LikeButton.tsx) (heart and counter), [LikeSurface](../../clients/web/src/shared/ui/LikeSurface.tsx) (a double click or double tap on the media likes and shows a brief heart), [useLikes](../../clients/web/src/shared/lib/useLikes.ts) (one batch call per page of cards or comments through `GET /api/likes/...?ids=`, optimistic toggle with rollback) and [LikersPage](../../clients/web/src/features/watch/LikersPage.tsx) (`/v/:id/likes`, paged). The web client still uses REST; screens may move to GraphQL one by one.

### Step 9. Compose and demo
In [docker-compose.yml](../../deploy/compose/docker-compose.yml): `likes-migrator` and `likes` (0.5 CPU / 192M each, no published port), `LIKES_DB_PASSWORD` (generated into `.env`; the init script creates the `likes` DB and role), subscribers `likes` for `videos` and `comments` (`Outbox__Subscribers__likes__BaseUrl`), the gateway at 256M with `GraphQL__AllowIntrospection=true`. `make demo` adds three steps: likes (idempotent like, batch counts, likers, unknown video → 404, comment like), a GraphQL query and mutation (401 without a token, `NOT_FOUND` for an unknown video) and a check that the `lake` rows wait in the outboxes and that no comment text is in any payload.

## Verification
```bash
make lint && make test-dotnet   # per solution, including likes and the GraphQL tests
make up && make demo && make verify-auth
```
Tests: `services/likes/tests` (`AC_likes_01..12`, adapter tests on a real Postgres, event payload tests), `services/comments/tests` (`AC_comments_*` and the outbox tests: one row per subscriber, no text, no event on repeat), `services/gateway/tests/Maksov.VideoService.Gateway.GraphQL.Tests` (`AC_graphql_01..13`). Acceptance criteria — [stories](../stories/lesson-03-likes-graphql.md); security — [threat model](../security/threat-model-lesson-03.md); SLOs and runbooks — [lesson-03.md](../slo/lesson-03.md).

## Common mistakes
| Symptom | Cause | Fix |
| --- | --- | --- |
| Liking a fresh video returns 404 | `likes` learns about the video from `video.processed.v1` (eventual consistency) | wait up to ~30 s, [runbook](../slo/lesson-03.md) |
| Counter and rows disagree | the counter was updated outside the like transaction | change `like_counts` in the same transaction as `likes` |
| A second `video.liked` event for a repeated like | the event was written without checking whether the row was new | write the event only when the insert changed something |
| Comment text appears in an outbox payload | the payload was built from the DTO | publish `textLength` only ([CommentEvents](../../services/comments/Maksov.VideoService.Comments.Bll/CommentEvents.cs)) |
| Outbox rows of `lake` never leave | no address for the subscriber: by design a backlog until lesson 4 | do nothing; do not configure a fake address |
| `/api/videos/{id}/comments/{commentId}/like` goes to `comments` | the comments catch-all route matched first | give the comment-like route a lower `Order` |
| GraphQL: 401 although REST works | the `Authorization` header is not forwarded or the token expired | check `ForwardAuthorizationHandler`, get a fresh token |
| GraphQL: `introspection is not allowed` | introspection is off by default | `GraphQL__AllowIntrospection=true` (local stack only) |
| GraphQL: N downstream calls for N cards | a resolver calls the client directly instead of a DataLoader | use the DataLoader per service ([DataLoaders.cs](../../services/gateway/Maksov.VideoService.Gateway.GraphQL/Schema/DataLoaders.cs)) |
| A deep query is rejected | depth limit 8 | flatten the query or fetch the nested part separately |

## Self-check exercises
1. Why is Engagement one domain with two services? What would you move if likes needed moderation?
2. Like the same video twice and read `likes.outbox`: how many rows and why? What changes after unlike?
3. Stop `likes` and like nothing; start it again: how do the `video.processed` rows for `likes` in the videos outbox behave (backoff, no loss)?
4. Why does `likes` keep `known_videos` instead of calling `videos`? What is the cost and what does the tombstone prevent?
5. Write a GraphQL query for a feed page with owners and like counters; count the downstream calls with the DataLoaders and without.
6. Which protections stop a client from sending a query that fans out to thousands of calls? Which one would you tune first?
7. ★ Add `likesCount` to the profile grid cards: where does the data come from and which batch endpoint is reused?

## What's next
Lesson 4 builds the data lake ingestion: a `lake-ingest` service receives the events (including the `lake` backlog of likes and comments) over internal HTTP and writes the bronze layer to MinIO as JSON Lines, idempotent by `eventId`. Lesson 5 deepens the Media Worker, lesson 6 moves the outbox transport to Kafka.
