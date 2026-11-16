# SLO and resources: lesson 3 (likes, events, GraphQL)

> Extends [lesson 2](lesson-02.md) and [lesson 1](lesson-01.md). Only what the `likes` service, the domain events and the GraphQL layer add or change is listed; earlier SLOs, chaos scenarios and runbooks remain valid.

## SLI / SLO (additions)
| Service | SLI | SLO |
| --- | --- | --- |
| likes | share of non-5xx responses | 99.5 % |
| likes | p95 `PUT/DELETE like`, `GET /api/likes/...?ids=`, `GET /api/videos/{id}/likes` | < 300 ms |
| likes | a ready video becomes likeable after `video.processed.v1` | p95 < 30 s after `ready` |
| outbox (comments, likes, videos → likes) | an event is delivered to each subscriber that has an address | 99 % within 1 min (otherwise retries with backoff) |
| outbox | age of the oldest unsent row of subscribers with an address | < 5 min; rows of `lake` are an expected backlog until lesson 4 |
| gateway `/graphql` | share of non-5xx responses | 99.5 % |
| gateway `/graphql` | p95 latency of a feed or video query (owner, likes, comments) | < 500 ms locally, one downstream call per service per level |

Metrics: ASP.NET Core (`http.server.request.duration`) per service; for GraphQL watch the share of `errors` by `extensions.code` (`UNAVAILABLE` points at a downstream service) and the 429 rate; for the outbox — unsent rows per subscriber and their age.

## Resource budget (changes to the lesson-2 table)
| Container | CPU | RAM |
| --- | --- | --- |
| likes | 0.5 | 192 MB |
| gateway | 0.75 | 256 MB (was 192 MB: schema, HTTP clients, DataLoaders) |
| **Total without ollama** | **≈ 11.5** (lesson 2: ≈ 11) | **≈ 6.0 GB** (lesson 2: ≈ 5.8 GB) |

`likes-migrator` is a one-shot container (0.5 CPU / 192 MB) and is not included in the total. `comments` and `likes` run an in-process outbox dispatcher, so no extra container appears.

## Chaos scenario (additions)
1. Stop `likes` (`docker compose ... stop likes`), upload and process a video, start `likes`: the `video.processed.v1` row for subscriber `likes` waits in the `videos` outbox (`sent_at is null`) and is delivered with backoff; after delivery the video is likeable. No event is lost, rows for `analysis` and `comments` are sent independently.
2. Like the same video twice and unlike it twice: `likes.outbox` holds exactly one `video.liked.v1` and one `video.unliked.v1` row for `lake`; the counter returns to 0.
3. Stop `comments` and add a comment through GraphQL: the field fails with `UNAVAILABLE`, other fields of the same query still return data (partial data).
4. Leave `lake` unconfigured: its rows accumulate in `likes.outbox` and `comments.outbox` and stay (`sent_at is null`); nothing else is affected.

## Runbook: a like returns 404
1. Is the video known to `likes`? DB `likes`: `select * from known_videos where video_id = '<id>';`. No row — the `video.processed.v1` event has not arrived (the video was `ready` less than ~30 s ago, or it is not delivered).
2. Delivery from videos: DB `videos`: `select id, topic, subscriber, attempts, sent_at, next_attempt_at, last_error from outbox where subscriber = 'likes' order by created_at desc limit 5;` — `last_error` 401 means `INTERNAL_API_KEY` mismatch, "refused" means `likes` is not up; no row at all means `Outbox__Subscribers__likes__BaseUrl` is not set at the media worker.
3. `deleted_at is not null` in `known_videos` — the video was deleted (404 is correct).
4. For a comment like: `select * from known_comments where comment_id = '<id>';` — filled by `comment.added.v1` from `comments`; check `comments.outbox` (`subscriber = 'likes'`) and that the comment belongs to the video in the path.

## Runbook: events are not delivered
1. Unsent rows per subscriber: `select subscriber, topic, count(*), min(created_at) from outbox where sent_at is null group by 1, 2;` in the DB of the producer (`comments`, `likes`, `videos`).
2. A subscriber without an address is **not** claimed by design (`lake` before lesson 4); every other subscriber needs `Outbox__Subscribers__<name>__BaseUrl` at the producer.
3. `last_error` and `attempts` on the rows show the cause; the log of the producer (`make logs s=comments`, `s=likes`) shows the dispatcher.
4. The receiver answers 400? The payload failed validation (`schemaVersion`, ids) — compare with `contracts/events/*.json`.

Connection to the DB: `docker exec videoservice-postgres-1 psql -U likes -d likes` (password — `LIKES_DB_PASSWORD` from `.env`).

## Runbook: GraphQL errors or slowness
1. `401` — missing or expired token (the same token as REST). `429` — the per-user budget (300 per minute) is exhausted.
2. `errors[].extensions.code`: `UNAVAILABLE` — a downstream service is down or slow (`make ps`, `make logs s=<service>`); `NOT_FOUND` — the id is unknown or not visible; `VALIDATION` — a limit was exceeded (page size 1..50, feed 1..30) or the service rejected the input.
3. A query is rejected before execution — it is deeper than 8 levels or the body is over 64 KiB.
4. Slow screen — look for a field resolved per item without a DataLoader (the downstream access log shows N calls); a feed page must produce one call per service per level.
5. Introspection fails — it is on only with `GraphQL__AllowIntrospection=true` (local compose sets it).
