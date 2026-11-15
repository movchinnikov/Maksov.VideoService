# 0009. Social features: video visibility, subscription feed, comments as a separate service, deletion

- **Status:** Accepted
- **Date:** 2026-11-15
- **Lesson / feature:** lesson-01 (Instagram-style social features)
- **Note:** follows, the feed and recommended friends moved out of `users`/`videos` into the new `friends` and `feed` services (lesson 2, [ADR 0010](0010-friends-and-feed-services.md)). Where this ADR says the feed is built in `videos` from `users` follows ("Subscriptions and search", "Feed and profile grid"), read: `friends` owns follows, `feed` returns video ids, `videos` serves cards. Visibility, comments, deletion and the per-subscriber outbox are unchanged.
- **Related:** [ADR 0006](0006-microservices-gateway-videos-users-analysis.md) (services, outbox), [ADR 0007](0007-vertical-first.md), [ADR 0008](0008-separate-solutions-nuget.md)

## Context
In the first version everyone saw only their own videos. An Instagram-like service needs follows, people search, a feed, a profile grid, comments and deletion. The data belongs to different services (users — follows, videos — videos, comments — comments), and there are no shared DBs ([ADR 0006](0006-microservices-gateway-videos-users-analysis.md)).

## Decision
### Visibility
- **Ready (`ready`) videos are visible to any authenticated user:** `GET /api/videos/{id}`, the stream (`stream`), poster, feed, profile grid.
- A video in any other status (`awaiting_upload`, `uploaded`, `processing`, `failed`) is visible only to its owner; everyone else gets 404. A deleted video is 404 for everyone.
- **Description:** the owner sees any status (`queued`/`processing`/`done`/`failed`), others only `done`.
- There is no anonymous access: all routes require a JWT.

### Follows and search (users service)
- `PUT/DELETE /api/users/{id}/follow` — idempotent; following yourself is not allowed (in the API and as a CHECK in the DB); an unknown user gives 404. Table `follows` in the `users` DB.
- `GET /api/users?query=` — case-insensitive search by login or name prefix; the caller is excluded from results.
- `GET /api/users/{id}` — public profile: login, name, bio, follower/following counters, `isFollowing`.

### Feed and profile grid (videos service)
- `GET /api/feed` — ready videos of followed users and own, newest first, keyset pagination. videos gets the follow list from users via the **internal** `GET /internal/v1/users/{id}/following-ids` (`X-Internal-Key`), cached for 20 s; if users is unavailable the feed **degrades** to own videos only (with a log warning) instead of failing.
- `GET /api/users/{id}/videos` — profile grid (the author's ready videos).
- The author on a card is a snapshot of the login `owner_username` at upload time: the feed needs no request to users per card.

### Comments — a separate `comments` service
- `GET/POST /api/videos/{id}/comments`, `DELETE /api/videos/{id}/comments/{commentId}`; text 1…500 characters; the list uses keyset pagination and a total count. Deletion is allowed for the **comment author or the video owner** (`canDelete` flag); someone else's comment is indistinguishable from a nonexistent one (404).
- Own DB `comments`: `comments` and `known_videos` — a replica of known videos (video_id, owner_id, deleted_at). A video is registered by the `video.processed.v1` event (so only ready videos can be commented on); an unknown video gives 404.
- A deleted video stays as a "tombstone" in `known_videos`: a late `video.processed.v1` does not resurrect it.

### Deletion and event cascade
- `DELETE /api/videos/{id}` — owner only; response 204. The video is marked `deleted` (`deleted_at`), and the `video.deleted.v1` event is written in the same transaction; then the source and segments are removed from MinIO. A storage cleanup failure does not cancel the deletion (the DB status is the source of truth) and is logged.
- `video.deleted.v1` is received by **analysis** (deletes the description) and **comments** (forgets the video and cascade-deletes comments). Handling is idempotent.

### Outbox per subscriber
- The `outbox` table has one row per **(event, subscriber)**, primary key `(id, subscriber)`; each subscriber has its own delivery, retries and backoff (5 s … 5 min), so an unavailable `comments` does not block `analysis`.
- Subscribers and routes are in `VideoEvents`: `video.processed.v1` and `video.deleted.v1` → `analysis`, `comments`. Addresses are `Outbox__Subscribers__<name>__BaseUrl`; a subscriber without an address is not served, and with no addresses at all the dispatcher does not start. Internal paths: `/internal/v1/events/video-processed` and `/internal/v1/events/video-deleted` with the `X-Internal-Key` header.
- Event contracts: [video-processed.v1.json](../../contracts/events/video-processed.v1.json), [video-deleted.v1.json](../../contracts/events/video-deleted.v1.json); receiver APIs — `internal-analysis.yaml`, `internal-comments.yaml`, `internal-users.yaml`.

### Deferred
- "Interesting videos" (recommendations, the "Interesting" tab) — lessons 8–9 (embeddings and recommendations); there is currently neither an API nor a screen.
- Likes, comment replies, notifications — lesson 2 onward.

## Alternatives considered
| Option | Why rejected |
| --- | --- |
| Follows and videos in one service | Mixes domains and DBs; users owns identity and the follow graph, videos owns media |
| Comments inside videos | Different growth rates and load; comments are independent of transcoding; a separate service exercises boundaries and eventual consistency before Kafka |
| Feed as a JOIN of videos × follows | No shared DB ([ADR 0006](0006-microservices-gateway-videos-users-analysis.md)); an internal follow-list request with a cache is used instead |
| Feed via fan-out on write | Premature for lesson 1; the hot store and fan-out are lesson 5 |
| Comments get the video owner via a request to videos | A synchronous dependency on every request; the `known_videos` replica built from the event is resilient to videos failures |
| Deletion without an event (comments cleaned up on request) | Orphaned comments and descriptions; an idempotent event-driven cascade is simpler and safer |
| One outbox row per event | One unavailable subscriber blocks delivery to the others or repeats it for all |

## Consequences
- (+) Each domain stays in its own service; links go only through the gateway API, internal HTTP and events.
- (+) users being unavailable does not break the feed; comments/analysis being unavailable does not hinder upload and playback.
- (−) Eventual consistency: a video can be commented on after `video.processed.v1` is delivered (usually seconds; fallback polling is 30 s); a new follow or unfollow shows up in the feed with up to 20 s delay (follow-list cache).
- (−) Any authenticated user can watch others' ready videos and see their author; there are no private accounts (the risk is recorded in the threat model).
- (−) The `owner_username` snapshot becomes stale when the login changes.
- (−) More tables and migrations, one more container (comments 0.5 CPU / 192 MB + migrator).

## Next steps
- [ ] Lesson 4: outbox transport → Kafka, subscribers become consumer groups.
- [ ] Lesson 5: feed on the hot store and fan-out instead of querying follows.
- [ ] Likes, comment replies, notifications; private accounts (if needed) — a separate ADR.
- [ ] "Interesting videos" — after embeddings (lessons 8–9).
