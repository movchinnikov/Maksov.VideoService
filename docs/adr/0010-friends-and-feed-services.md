# ADR 0010: Friends service (pgvector) and Feed service

- Status: accepted
- Date: 2026-11-15
- Lesson: 2 (the `lesson-01` tag is the state before this ADR)
- Related: ADR 0006 (services and gateway), ADR 0008 (separate solutions), ADR 0009 (social features, comments)

## Context

Follows lived in the `users` service and the feed was built inside `videos` (it called `users` for following ids).
That mixed three responsibilities (profiles, the social graph, feed assembly) and made `videos` depend on `users`.
We also want "recommended friends" and a place to grow the feed (fan-out, ranking) in later lessons.

## Decision

Two new services, each with its own solution (`services/friends`, `services/feed`), following ADR 0008.

### `friends` — the social graph + recommendations

- Owns follows (moved out of `users`) in its own database `friends` (PostgreSQL with the **pgvector** extension).
- Tables: `follows(follower_id, followee_id, created_at)`, `user_embeddings(user_id, embedding vector(32), model_version, updated_at)` with an HNSW index (`vector_cosine_ops`).
- **Recommended friends = friends of friends**, ranked by cosine similarity of graph embeddings.
  Baseline embedding `graph-hash-v1` (deterministic, local, no ML model): every user has a stable pseudo-random
  base vector `h(user)` (SHA-256 expanded to 32 floats, L2-normalised); the user's embedding is
  `normalize(h(user) + 0.5 * sum(h(f) for f in following(user)))`. Users who follow the same people get similar vectors.
  The embedding is recomputed for the follower on follow/unfollow; the followee gets a row (its own `h`) if missing.
  `model_version` is stored with every vector so a better model can replace it (lessons 8-9) without downtime.
- Candidates are friends of the viewer's friends that the viewer does not follow yet and is not; ordered by
  `embedding <=> viewer_embedding` and then by number of mutual friends.
- Following a user requires the profile to exist: `friends` asks `users` (`GET /internal/v1/users/{id}/exists`, positive answers cached 5 min).

### `feed` — assembles the feed as a list of video ids

- Stateless (no database): takes the viewer's following ids from `friends`, adds the viewer, asks `videos` for the newest ready
  videos of those owners (ids only) and returns a page of ids with a keyset cursor.
- The client then loads the cards for those ids from `videos` (`GET /api/videos/cards?ids=`), keeping the order.
- `videos` no longer has a feed endpoint and no longer depends on `users`; it only exposes an internal "ready videos by owners" query
  and the batch cards endpoint. The profile grid (`GET /api/users/{id}/videos`) stays in `videos`.
- The following ids are cached for 20 s inside `feed`; if `friends` is unavailable the feed degrades to the viewer's own videos.

### `users` shrinks to profiles

- Follow endpoints, follower/following counters, `isFollowing` and the internal following-ids endpoint are removed from `users`
  (table `follows` is dropped by migration `0003`). Profile responses no longer contain counters.
- `users` gets batch lookup `GET /api/users?ids=a,b` and the internal existence check.

## Public API (through the gateway)

| Route | Service | Notes |
| --- | --- | --- |
| `PUT/DELETE /api/users/{id}/follow` | friends | 204; self -> 400; unknown profile -> 404; idempotent |
| `GET /api/friends/{id}/stats` | friends | `{followersCount, followingCount, isFollowing, followsYou, isMutual}` |
| `GET /api/friends/{id}/followers`, `/following` | friends | `{items: [{userId, followedAt, isFollowing, followsYou, isMutual}], nextCursor, total}` (cursor, limit <= 50, default 20) |
| `GET /api/friends/status?ids=a,b` | friends | `{following: [ids], followedBy: [ids]}` (max 50 ids) |
| `GET /api/friends/recommendations?limit=` | friends | `{items: [{userId, mutualFriends, similarity}]}` (limit <= 30, default 10) |
| `GET /api/feed?cursor&limit` | feed | `{ids: [videoId], nextCursor}` (limit <= 30, default 12) |
| `GET /api/videos/cards?ids=a,b,c` | videos | `{items: [FeedItem]}` in the requested order; only ready videos (max 30 ids) |
| `GET /api/users?query&limit` / `?ids=a,b` | users | `{items: [{id, username, displayName, bio}]}` |
| `GET /api/users/me`, `GET /api/users/{id}` | users | profile without counters |

Internal (header `X-Internal-Key`, not routed by the gateway):

| Route | Service |
| --- | --- |
| `GET /internal/v1/friends/{userId}/following-ids` -> `{ids}` | friends |
| `GET /internal/v1/users/{id}/exists` -> 204 / 404 | users |
| `POST /internal/v1/videos/ready-by-owners` `{ownerIds, cursor?, limit}` -> `{items: [{id}], nextCursor}` | videos |

Note: **mutual = both follow each other** (`isMutual = isFollowing && followsYou`); all flags are relative to the viewer. Follower and following lists are visible to any authenticated user, like profiles.

## Infrastructure

- The shared PostgreSQL image becomes a pgvector build (`pgvector/pgvector`, pinned); `CREATE EXTENSION vector` runs once in the `friends` database by the init script.
- Compose: `friends-migrator`, `friends` (0.5 CPU / 192M), `feed` (0.5 CPU / 160M); gateway depends on both.
- Contracts: `contracts/openapi/api.yaml` (public), `internal-friends.yaml`, `internal-users.yaml`, `internal-videos.yaml`.

## Consequences

- `videos` is independent of `users`; the feed can evolve (fan-out into Tarantool in lesson 5, ranking in lesson 9) without touching `videos`.
- A feed page costs two calls from the client (ids, then cards) and one internal call chain `feed -> friends`, `feed -> videos`.
- Unfollow/follow consistency is eventual for the feed (20 s cache), immediate for stats and status.

