# Lesson 2: friends, recommended friends, feed service

> Builds on [lesson 1](lesson-01-upload.md) (tag `lesson-01`: follows in `users`, feed in `videos`). This file lists only the new and changed stories and acceptance criteria; everything else is unchanged.

- **ADR:** [0010](../adr/0010-friends-and-feed-services.md) (friends and feed services, pgvector), [0008](../adr/0008-separate-solutions-nuget.md), [0009](../adr/0009-social-features-comments.md)
- **Contracts:** [contracts/openapi/api.yaml](../../contracts/openapi/api.yaml), [internal-friends.yaml](../../contracts/openapi/internal-friends.yaml), [internal-users.yaml](../../contracts/openapi/internal-users.yaml), [internal-videos.yaml](../../contracts/openapi/internal-videos.yaml)
- **Lesson text:** [02-friends-feed-pgvector.md](../lessons/02-friends-feed-pgvector.md)

## Stories

**US-9 (changed).** As a user, I want to find people, follow them and see their ready videos and my own in the feed so that I can watch interesting content. The feed arrives as a page of video ids; the client loads the cards for those ids.
**US-10 (changed).** As a user, I want to open a person's profile — name, bio, counters, a grid of their videos — and watch their ready videos.
**US-13.** As a user, I want to see recommended friends — friends of my friends who are similar to me — so that I can quickly find people to follow.
**US-14.** As a user, I want to see who follows a person and whom they follow, with badges "Mutual" and "Follows you", so that I can recognise people and follow back.

## Acceptance criteria

New and changed criteria (IDs as in the test names: `AC_friends_NN` in `services/friends/tests`, `AC_feed_NN` in `services/feed/tests`; the videos tests named `AC_feed_01..03` are listed here as **AC-catalog-01..03**).

| ID | Given | When | Then |
| --- | --- | --- | --- |
| AC-users-05 | any signed-in user | GET /api/users/{id} | public profile: `id`, `username`, `displayName`, `bio` (no counters, no follow flag); unknown id — 404 |
| AC-users-07 | any signed-in user | GET /api/users/me, GET /api/users/{id} | profile responses carry no follow data (no counters, no `isFollowing`) |
| AC-users-08 | any signed-in user | PUT /api/users/{id}/follow sent directly to the users service | not served by users (follows belong to friends) |
| AC-users-09 | there are people in the DB | GET /api/users?query=<login or name prefix> | case-insensitive prefix matches, excluding self; empty query — empty list |
| AC-users-10 | known and unknown ids | GET /api/users?ids=a,b | summaries of existing profiles (at most 50 ids) |
| AC-users-11 | request without a key or with a wrong one | GET /internal/v1/users/{id}/exists | 401; with the key — 204 if the profile exists, otherwise 404 |
| AC-gateway-02 | valid token | `/api/videos/**`, `GET /api/videos/cards`, `GET /api/users/{id}/videos`, `GET /api/feed`, `PUT/DELETE /api/users/{id}/follow`, `/api/friends/**`, `/api/users/**`, `GET /api/videos/{id}/description`, `/api/videos/{id}/comments/**` | request goes to videos / videos / videos / feed / friends / friends / users / analysis / comments respectively (follow and feed routes take priority over the generic `/api/users/**` and `/api/videos/**`) |
| AC-friends-01 | request without a token | `/api/friends/*`, `/api/users/{id}/follow` | 401 |
| AC-friends-02 | user A, user B | PUT /api/users/{B}/follow twice, then DELETE | 204 each time (idempotent); `GET /api/friends/{B}/stats` — B's followers and A's following counters change by 1, `isFollowing` reflects the state |
| AC-friends-03 | user A | follow self / a user without a profile | 400 / 404 (self-follow is also forbidden by the DB) |
| AC-friends-04 | the users service is down | PUT /api/users/{id}/follow | 503 (the profile cannot be confirmed) |
| AC-friends-05 | A follows B, not C | GET /api/friends/status?ids=B,C | `{following: [B]}`; at most 50 ids are considered |
| AC-friends-06 | A follows B, B follows C | GET /api/friends/recommendations | friends of friends not yet followed (C), not self, ordered by embedding similarity and mutual friends; `limit` ≤ 30 (default 10) |
| AC-friends-07 | request without a key or with a wrong one | GET /internal/v1/friends/{userId}/following-ids | 401; with the key — `{ids}` |
| AC-friends-08 | A follows B, B follows A, A follows C | GET /api/friends/{B}/stats and /status?ids=B,C as A | stats carry `isFollowing`, `followsYou` and `isMutual` (true for B); status returns `following` and `followedBy` (B is in both) |
| AC-friends-09 | a person with several followers and followings | GET /api/friends/{id}/followers and /following with `limit` | newest follow first, keyset pagination (`nextCursor`), `total`, each item has `userId`, `followedAt`, `isFollowing`, `followsYou`, `isMutual` relative to the viewer; limit ≤ 50 (default 20) |
| AC-friends-10 | no token / a malformed cursor | GET /api/friends/{id}/followers | 401 without a token; a broken cursor is ignored (first page, 200) |
| AC-feed-01 | request without a token | GET /api/feed | 401 |
| AC-feed-02 | A follows B | GET /api/feed as A | the feed service asks videos for the owners {B, A} and returns video ids only (no cards) with `nextCursor` |
| AC-feed-03 | any `limit` | GET /api/feed?limit= | the limit is clamped to 1..30 (default 12) |
| AC-feed-04 | the friends service is down | GET /api/feed | 200 with the viewer's own videos only |
| AC-feed-05 | the videos service is down | GET /api/feed | 503 |
| AC-catalog-01 | ready and non-ready videos | GET /api/videos/cards?ids=a,b,c | cards of ready videos only, in the requested order; more than 30 ids — 400 |
| AC-catalog-02 | a person has ready videos | GET /api/users/{id}/videos | grid of their ready videos, newest first |
| AC-catalog-03 | request without a key / with the key | POST /internal/v1/videos/ready-by-owners | 401 without the key; with it — ids of the newest ready videos of the owners, paged by cursor |

Superseded lesson-1 criteria: `AC-users-07..10` (follows, counters, following-ids in `users`), `AC-feed-01..02` (feed and grid in `videos`) and the route list of `AC-gateway-02`; `AC-users-05` changed (no counters in the public profile).

## NFR (changes)
- Feed: p95 `GET /api/feed` (ids) < 300 ms with the follow cache (20 s); if friends is unavailable — degradation to own videos, not an error; cards (`GET /api/videos/cards`) p95 < 300 ms.
- Follower/following lists: ≤ 50 items per page, keyset pagination.
- Resources: friends ≤ 192 MB, feed ≤ 160 MB (see [lesson-02 SLO](../slo/lesson-02.md)).

## Traceability (new and changed)
| AC | Test | Status |
| --- | --- | --- |
| AC-users-01..11 | `UsersApiTests.AC_users_01_*` … `AC_users_11_*` (`services/users/tests`); `UsersAdapterTests.Exists_and_batch_lookup`; demo | green |
| AC-friends-01..10 | `FriendsApiTests.AC_friends_01_*` … `AC_friends_10_*`; `FriendsAdapterTests` (follow/unfollow, embeddings with `model_version` in a vector column, ranked recommendations on real PostgreSQL with pgvector); `GraphEmbeddingTests`; demo (follow, unfollow) (`services/friends/tests`) | green |
| AC-feed-01..05 | `FeedApiTests.AC_feed_01_*` … `AC_feed_05_*`; `Following_ids_are_cached_and_the_key_is_sent` (`services/feed/tests`); demo (feed before and after follow/unfollow) | green |
| AC-catalog-01..03 | `VideoEndpointsTests.AC_feed_01_cards_return_ready_videos_in_the_requested_order`, `AC_feed_02_profile_grid_lists_ready_videos_of_one_author`, `AC_feed_03_internal_ready_by_owners_requires_the_key_and_pages_by_cursor` (`services/videos/tests`) | green |
| AC-gateway-02 | `GatewayTests.AC_gateway_02_routes_to_the_owning_service` (follow, friends, feed, cards routes) | green |
