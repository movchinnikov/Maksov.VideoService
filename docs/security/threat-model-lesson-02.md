# Threat model: lesson 2 (friends, recommended friends, feed service)

> Extends the [lesson 1 threat model](threat-model-lesson-01.md) (tag `lesson-01`). Only the new and changed items are listed; T1–T25 and T27–T29 of lesson 1 still apply (T26 is replaced below).

Trust boundaries added or changed: gateway ↔ `friends` and `feed`; `feed` → `friends` (following list) and `feed` → `videos` (ids of ready videos); `friends` → `users` (profile existence); `friends` ↔ its own PostgreSQL database with the pgvector extension. `videos` no longer calls `users`. Architecture — [ADR 0010](../adr/0010-friends-and-feed-services.md).

| # | STRIDE | Threat | Mitigation | Verification |
| --- | --- | --- | --- | --- |
| T26 | DoS | Feed: a huge following list, frequent following-list requests to friends, unbounded owner lists to videos | Lists are bounded (friends — up to 5000 ids, feed and videos — up to 2000 owners), keyset pagination, page ≤ 30, following cache 20 s in `feed`; cards are limited to 30 ids per request; friends unavailable → the feed degrades to own videos, videos unavailable → 503 | `AC_feed_03/04/05` (feed tests), `AC_feed_01` (videos tests: cards) |
| T30 | Elevation / Info disclosure | New internal calls — `feed → friends` (following-ids), `feed → videos` (ready-by-owners), `friends → users` (exists) — reached from outside or with a forged caller | All three are `/internal/v1/**`, protected by `X-Internal-Key` (constant-time comparison, empty key = closed), not routed by the gateway (404), services publish no ports; callers send the key from their own environment | `AC_friends_07`, `AC_users_11`, `AC_feed_03` (videos tests: `ready-by-owners` requires the key), `AC_gateway_03` |
| T31 | Elevation | IDOR on friends endpoints: reading stats or follow status "as another user"; following on behalf of another | The viewer is always taken from `sub`; `stats` and `status` expose only aggregate counters and the viewer's own follow flags (they reveal nothing about other people's follows); status is limited to 50 ids | `AC_friends_02`, `AC_friends_05` |
| T32 | Info disclosure | Recommended friends reveal the social graph (who follows whom) or leak data through embeddings | Items contain only `userId`, the number of mutual friends and a similarity score — no lists of other users' follows; candidates are limited to friends of the viewer's own friends; embeddings are derived only from follow data, stored per user in the `friends` DB and never returned. Accounts are public (no private accounts) and counters are already visible to signed-in users; residual risk: mutual-friend counts allow some inference about the graph | `AC_friends_06`, `FriendsAdapterTests` |
| T33 | Elevation | One shared internal key reused by all services (key reuse): a compromised service can call every other internal API | Known limitation of the docker-compose lessons: a single `INTERNAL_API_KEY`, generated locally and never committed; internal routes expose narrow operations (exists, following-ids, ready-by-owners, event intake); per-service keys or mTLS in lesson 13 | compose, `.env.example` |
| T34 | DoS | Follow requests flood `users` through the existence check; a users outage blocks following | Positive answers are cached for 5 minutes; a users outage returns 503 and writes nothing (a follow is never created for an unconfirmed profile); routes stay behind JWT | `AC_friends_04` |
| T35 | Tampering | Poisoned or stale embeddings skew recommendations | The embedding is deterministic (`graph-hash-v1`: a function of the follow graph only), recomputed on every follow/unfollow and stored with `model_version`; no user-supplied vectors exist | `GraphEmbeddingTests`, `FriendsAdapterTests` |
| T36 | Info disclosure | Follower and following lists reveal who follows whom | Deliberate (ADR 0010): lists, counters and relation flags are visible to any authenticated user, like profiles (accounts are public; no anonymous access, JWT required); flags are relative to the caller and never expose other people's relations; pages are bounded (limit ≤ 50, keyset cursor, a malformed cursor is ignored), `total` comes from the stats query, so a page never scans the whole list; the viewer comes from `sub` | `AC_friends_08..10` |

## Changed items of lesson 1
- **T15 (direct access bypassing the gateway):** the ports of `friends` and `feed` are not published either; each validates the JWT itself. Verification: `AC_gateway_01/03`, compose.
- **T18 (shared DB leak):** a new database and role `friends` with `FRIENDS_DB_PASSWORD`; `feed` has no database. The `vector` extension is created by the superuser in the init script only in the `friends` database; the service role cannot create extensions.
- **T24 (follow on behalf of another):** follows are now served by `friends`; follower and owner still come only from `sub`, self-follow is forbidden in the API and by a DB CHECK (`AC_friends_02/03`).
- **T26** (feed bounds) is replaced by the row above.
- **T27 (people search as a directory):** the batch lookup `GET /api/users?ids=` returns the same public summaries and is limited to 50 ids (`AC_users_10`).

## Residual risks
- Any authenticated user sees ready videos, profiles, follower/following lists and the user directory; private accounts and blocking are not implemented (a separate ADR if needed).
- A single `INTERNAL_API_KEY` is shared by all services, including `friends` and `feed` (T33).
- Rate limit at the gateway only for upload creation; follows, list reads and recommendation requests are not rate-limited.
- Recommendations: mutual-friend counts allow some inference about the graph (T32).
- The pgvector extension is created by the superuser in the init script only for the `friends` database; init scripts run only on a fresh volume.
