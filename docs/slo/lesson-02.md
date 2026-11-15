# SLO and resources: lesson 2 (friends and feed)

> Extends [lesson 1](lesson-01.md) (tag `lesson-01`). Only what the `friends` and `feed` services add or change is listed here; the lesson-1 SLOs, chaos scenario and runbooks (video processing, description, comments) remain valid. In lesson 1 the feed was served by `videos` (SLO `p95 GET /api/feed and GET /api/users/{id}/videos`) — the feed row below replaces it.

## SLI / SLO (additions)
| Service | SLI | SLO |
| --- | --- | --- |
| gateway + feed | p95 `GET /api/feed` (a page of ids) | < 300 ms (20 s follow cache) |
| gateway + videos | p95 `GET /api/videos/cards` and `GET /api/users/{id}/videos` | < 300 ms |
| friends | p95 follow/unfollow, stats, status, follower/following lists (≤ 50 per page), recommendations | < 300 ms (recommendations on a graph of a few thousand users) |
| feed | share of feed requests that are not degraded to "own videos only" | 99 % |

Metrics: ASP.NET Core (`http.server.request.duration`) per service; `feed` has no database, so alert on upstream errors towards `friends` and `videos`.

## Resource budget (changes to the lesson-1 table)
| Container | CPU | RAM |
| --- | --- | --- |
| friends | 0.5 | 192 MB |
| feed | 0.5 | 160 MB |
| postgres | 1.0 | 512 MB (now the pinned `pgvector/pgvector` image; same limits) |
| **Total without ollama** | **≈ 11** (lesson 1: ≈ 10) | **≈ 5.8 GB** (lesson 1: ≈ 5.4 GB) |

`friends-migrator` is a one-shot container (0.5 CPU / 192 MB) and is not included in the total. `feed` has no migrator and no database.

## Chaos scenario (additions)
1. Feed degradation: stop `friends` (`docker compose ... stop friends`) — `GET /api/feed` returns 200 with the user's own video ids only; after `friends` starts, follows return within 20 s at most (cache in `feed`). Stop `videos` — `GET /api/feed` returns 503.
2. Follow without `users`: stop `users` — `PUT /api/users/{id}/follow` returns 503 and creates nothing (profile existence cannot be confirmed; positive answers are cached for 5 min).

## Runbook: feed is empty
The feed is two calls: `GET /api/feed` (ids, service `feed`) and `GET /api/videos/cards?ids=` (cards, service `videos`).
1. Are there follows? `friends` DB: `select * from follows where follower_id = '<sub>';` (`sub` — the `id` field from `GET /api/users/me`).
2. Are the videos ready? Only `ready` videos appear: `videos` DB: `select id, status, owner_id from videos where owner_id = '<author id>' order by created_at desc;`.
3. Is `friends` unavailable? Then `feed` returns only the viewer's own ids: check `make ps` and `make logs s=friends`, then `Friends__BaseUrl` and `INTERNAL_API_KEY` of `feed`. A fresh follow shows up in the feed with up to 20 s delay (cache).
4. Ids come back but no cards? Cards are returned for `ready` videos only and keep the requested order (max 30 ids): call `GET /api/videos/cards?ids=` with the ids from the feed and check `make logs s=videos`.
5. `GET /api/feed` returns 503? `videos` is unavailable (`make logs s=videos`, `Videos__BaseUrl` of `feed`).

## Runbook: follow fails or recommendations are empty
1. `PUT /api/users/{id}/follow` returns 503: `friends` cannot confirm the profile with `users` — `make logs s=friends`, check that `users` is healthy and `INTERNAL_API_KEY` matches; 404 means the profile does not exist yet (the person has never opened the app).
2. Recommendations are empty: the viewer follows nobody, or all friends of friends are already followed (or the viewer). Check `friends` DB: `select user_id, model_version, updated_at from user_embeddings order by updated_at desc limit 5;` (`model_version` is `graph-hash-v1`) and `select * from follows where follower_id = '<sub>';`.
3. pgvector missing (`type "vector" does not exist` in the `friends`/`friends-migrator` log): the shared Postgres must be the `pgvector/pgvector` image and the extension is created by the init script only on the first volume initialisation — run `make down && make up` to recreate the volume.

Connection to the DB: `docker exec videoservice-postgres-1 psql -U friends -d friends` (password — `FRIENDS_DB_PASSWORD` from `.env`).