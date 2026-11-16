# ADR 0011: Likes service and domain events for the data lake

- Status: accepted
- Date: 2026-11-17
- Lesson: 3
- Related: ADR 0006, 0009 (comments), 0010 (friends and feed)

## Context

Lesson 3 adds likes. Likes and comments are the first user-behaviour signals of the product; they must reach the data lake
(lesson 4 builds the ingestion; lessons 6-9 build analytics and recommendations on top). Until Kafka (a later lesson) the
transport stays "transactional outbox + internal HTTP".

## Decision

### Domain: engagement

A new bounded context, **engagement**, owns reactions of people to content. Today it has one service, `likes`.
Comments stay in their own service (`comments`, content created by users), because a comment has its own life cycle, text
and moderation; a like is a tiny fact "user U likes target T". Both publish domain events.

### `likes` service (own solution `services/likes`, database `likes`)

- Targets: a **video** or a **comment**. A like is idempotent: liking twice keeps one like and emits no second event.
- Tables: `likes(target_type, target_id, user_id, video_id, created_at)` (PK `(target_type, target_id, user_id)`),
  `like_counts(target_type, target_id, count)` (maintained in the same transaction, O(1) reads),
  `known_videos(video_id, owner_id, deleted_at)` and `known_comments(comment_id, video_id, author_id)` (replicas fed by events,
  used to answer 404 for unknown targets without calling other services), `outbox`.
- Public API (through the gateway):

| Route | Notes |
| --- | --- |
| `PUT/DELETE /api/videos/{videoId}/like` | 204; unknown or deleted video -> 404; idempotent |
| `PUT/DELETE /api/videos/{videoId}/comments/{commentId}/like` | 204; unknown comment -> 404; idempotent |
| `GET /api/likes/videos?ids=a,b` and `GET /api/likes/comments?ids=a,b` | `{items:[{id, count, likedByMe}]}`, max 50 ids, unknown ids -> count 0 |
| `GET /api/videos/{videoId}/likes?cursor&limit` | likers, newest first: `{items:[{userId, likedAt}], nextCursor, total}` (limit <= 50, default 20) |

- Inbound events (internal, `X-Internal-Key`, idempotent): `POST /internal/v1/events/video-processed`, `video-deleted`,
  `comment-added`, `comment-deleted`. Deleting a video removes its likes, its comments' likes and counters.
- Outbound events (outbox, subscriber `lake`): `video.liked.v1`, `video.unliked.v1`, `comment.liked.v1`, `comment.unliked.v1`.

### Events

All events carry `eventId`, `occurredAt`, `schemaVersion`, `traceId` (camelCase JSON, like the existing ones).

| Event | Producer | Subscribers | Extra fields |
| --- | --- | --- | --- |
| `comment.added.v1` | comments | likes, lake | `commentId, videoId, authorId, videoOwnerId, textLength` |
| `comment.deleted.v1` | comments | likes, lake | `commentId, videoId, authorId, deletedBy` |
| `video.liked.v1` / `video.unliked.v1` | likes | lake | `videoId, ownerId, userId` |
| `comment.liked.v1` / `comment.unliked.v1` | likes | lake | `commentId, videoId, commentAuthorId, userId` |
| `video.processed.v1` / `video.deleted.v1` (existing) | videos | analysis, comments, **likes** | unchanged |

- The comment **text is not published** (privacy: the lake gets `textLength` only).
- Deleting a video cascades comments and likes inside the services; the lake learns it from `video.deleted.v1`
  (no per-comment events for the cascade).
- Rows for the `lake` subscriber are always written; they wait in the outbox (a backlog) until lesson 4 configures
  `Outbox__Subscribers__lake__BaseUrl`. The dispatcher only claims rows of subscribers that have an address.

### Shared outbox

The outbox write path, claiming, backoff and the HTTP publisher move to `libs/BuildingBlocks` (`Outbox` namespace:
`OutboxStore`, `HttpEventPublisher`, `OutboxDispatcher`, `AddOutbox`). `likes` and `comments` use it; `videos` keeps its own
copy until the Kafka lesson replaces the transport (known duplication).

### Gateway, resources

- Gateway routes: `/api/likes/**` and the two `.../like` routes -> likes (the comment-like route is more specific than the
  comments catch-all).
- Compose: `likes-migrator`, `likes` (0.5 CPU / 192M); `comments` and `likes` run an in-process outbox dispatcher.

## Consequences

- Product signals are captured from day one; the lake can be added without changing producers.
- A like needs no call to `videos` or `comments` at request time (replicas), at the price of eventual consistency
  (a video becomes likeable after its `processed` event is delivered).
- Counters are duplicated state (`like_counts`) and must be updated in the same transaction as the like row.
