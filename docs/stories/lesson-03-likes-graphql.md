# Lesson 3: likes, domain events, GraphQL

> Builds on [lesson 2](lesson-02-friends-feed.md). This file lists only the new and changed stories and acceptance criteria; everything else is unchanged.

- **ADR:** [0011](../adr/0011-likes-service-and-domain-events.md) (likes and domain events), [0012](../adr/0012-graphql-at-the-gateway.md) (GraphQL at the gateway); domain page — [Engagement](../domains/engagement.md)
- **Contracts:** [api.yaml](../../contracts/openapi/api.yaml), [internal-likes.yaml](../../contracts/openapi/internal-likes.yaml), [gateway.graphql](../../contracts/graphql/gateway.graphql), events [comment.added](../../contracts/events/comment-added.v1.json), [comment.deleted](../../contracts/events/comment-deleted.v1.json), [video.liked](../../contracts/events/video-liked.v1.json), [video.unliked](../../contracts/events/video-unliked.v1.json), [comment.liked](../../contracts/events/comment-liked.v1.json), [comment.unliked](../../contracts/events/comment-unliked.v1.json)
- **Lesson text:** [03-likes-events-graphql.md](../lessons/03-likes-events-graphql.md)

## Stories

**US-15.** As a viewer, I want to like and unlike a video (a heart, or a double tap on the media) and see how many people liked it so that I can show appreciation and see what is popular.
**US-16.** As a viewer, I want to like comments and open the list of people who liked a video so that I can see who reacts.
**US-17.** As a client developer, I want one request that returns a video with its owner, like counters and comments so that a screen does not make a waterfall of REST calls.
**US-18.** As the product team, I want likes and comments to be published as domain events (without comment text) so that analytics and recommendations can be built later without changing the producers.

## Acceptance criteria

| ID | Given | When | Then |
| --- | --- | --- | --- |
| AC-likes-01 | request without a token | any `/api/likes/**`, `.../like`, `.../likes` | 401 |
| AC-likes-02 | an unknown or deleted video / comment | PUT like | 404 |
| AC-likes-03 | a known video | PUT like twice, then DELETE twice | 204 each time (idempotent); the counter changes by one |
| AC-likes-04 | a comment of another video | PUT /api/videos/{id}/comments/{commentId}/like | 404 (the comment must belong to the video in the path) |
| AC-likes-05 | videos liked by several users | GET /api/likes/videos?ids=… | `count` and `likedByMe` per id; unknown ids → count 0 |
| AC-likes-06 | malformed or empty ids | GET /api/likes/videos?ids=… | 400 |
| AC-likes-07 | more than 50 ids | GET /api/likes/videos?ids=… | 400 |
| AC-likes-08 | several likers | GET /api/videos/{id}/likes | newest first, keyset cursor, `total`, limit ≤ 50 |
| AC-likes-09 | request without a key or with a wrong one | `POST /internal/v1/events/*` of likes | 401 |
| AC-likes-10 | `video.processed.v1` / `video.deleted.v1` | event intake, including repeats and a late `processed` after `deleted` | idempotent; the video becomes likeable; deletion removes likes and counters and is not resurrected |
| AC-likes-11 | `comment.added.v1` / `comment.deleted.v1` | event intake | the comment becomes likeable; deletion removes it and its likes; a late `added` after `deleted` is blocked |
| AC-likes-12 | wrong `schemaVersion`, empty ids | event intake | 400 |
| AC-events-01 | a comment is added | POST /api/videos/{id}/comments | one `comment.added.v1` outbox row per subscriber (`likes`, `lake`) in the same transaction; the payload has `textLength` and **no text** |
| AC-events-02 | a comment is deleted by its author or the video owner | DELETE comment, repeated | one `comment.deleted.v1` row per subscriber; a repeated delete writes none |
| AC-events-03 | a video is forgotten (`video.deleted.v1`) | cascade of comments | no per-comment events (the lake learns from `video.deleted.v1`) |
| AC-events-04 | the comment insert fails | POST a comment | no outbox row (one transaction) |
| AC-events-05 | a like / unlike / comment like | PUT / DELETE | exactly one `*.liked.v1` / `*.unliked.v1` row for `lake`; a repeat writes none; an unknown target writes nothing; comment likes count separately from video likes |
| AC-events-06 | any produced event | build the payload | contains `eventId` (new for every event), `occurredAt`, `schemaVersion` = 1, `traceId` (null when absent) and the contract fields |
| AC-events-07 | `lake` has no address (lesson 3) | likes and comments produce events | `lake` rows wait in the outbox (`sent_at is null`); the dispatcher claims only rows of subscribers with an address |
| AC-graphql-01 | request without a token | POST /graphql | 401 before any service is called |
| AC-graphql-02 | a feed of cards | `feed { … owner … likes … comments … }` | one downstream call per service (DataLoaders) |
| AC-graphql-03 | the same user appears many times | friend stats | deduplicated per user |
| AC-graphql-04 | signed-in user | `likeVideo` | the likes service is called, fresh counters are returned |
| AC-graphql-05 | signed-in user | `addComment`, `deleteComment`, `likeComment` | requests reach comments and likes |
| AC-graphql-06 | signed-in user | `follow` | routed to friends |
| AC-graphql-07 | a downstream 404 | any query | `NOT_FOUND` error code |
| AC-graphql-08 | downstream 400 / failure | any query | `VALIDATION` / `UNAVAILABLE` error codes, partial data allowed |
| AC-graphql-09 | page size above the limit | any paged query | rejected without calling services |
| AC-graphql-10 | a query deeper than 8 levels | any query | rejected before execution |
| AC-graphql-11 | default configuration | introspection query | disabled |
| AC-graphql-12 | the path `/graphql` | request | served by the gateway, not proxied to a service |
| AC-graphql-13 | `GraphQL:AllowIntrospection=true` | introspection query | works |
| AC-gateway-02 (changed) | valid token | like routes, `/api/likes/**`, `POST /graphql` | likes routes go to `likes` (the comment-like route wins over the comments catch-all); `/graphql` is handled by the gateway |

Unchanged from earlier lessons: `AC-comments-01..09` (still green with the outbox) and the lesson-1/2 criteria.

## NFR (changes)
- Likes: p95 `PUT/DELETE like`, counters and likers < 300 ms; counters are O(1) reads.
- GraphQL: a feed page with owners, likes and comments makes one downstream call per service; p95 < 500 ms locally; rate limit 300 requests per user per minute.
- Events: a like or comment event is written in the same transaction as the state change; delivery to `likes` within ~30 s (dispatcher poll), `lake` rows wait without loss.
- Resources: likes ≤ 192 MB / 0.5 CPU, gateway ≤ 256 MB (see [lesson-03 SLO](../slo/lesson-03.md)).

## Traceability
| AC | Test | Status |
| --- | --- | --- |
| AC-likes-01..12 | `LikesApiTests.AC_likes_01_*` … `AC_likes_12_*` (`services/likes/tests`) | green |
| AC-events-01 | `CommentsAdapterTests.Adding_a_comment_writes_one_outbox_row_per_subscriber_without_the_text`, `CommentsApiTests.Added_and_deleted_comments_emit_events_without_the_text` | green |
| AC-events-02 | `CommentsAdapterTests.Deleting_a_comment_writes_one_outbox_row_per_subscriber_and_a_repeat_writes_none` | green |
| AC-events-03 | `CommentsAdapterTests.Forgetting_a_video_emits_no_per_comment_events` | green |
| AC-events-04 | `CommentsAdapterTests.A_failing_insert_writes_no_outbox_row` | green |
| AC-events-05 | `LikesAdapterTests.Like_is_idempotent_keeps_the_counter_consistent_and_emits_one_outbox_row_for_lake`, `Unlike_emits_unliked_once_and_decrements`, `Like_of_an_unknown_target_writes_nothing`, `Comment_like_emits_comment_events_and_counts_separately_from_video` | green |
| AC-events-06 | `LikeEventsTests` (`Video_event_has_contract_fields`, `Comment_event_has_contract_fields_and_null_trace_when_absent`, `Every_event_gets_a_new_event_id`) | green |
| AC-events-07 | `make demo` (step "Events for the data lake": rows with `subscriber = 'lake'` and `sent_at is null`, no comment text in any payload) | demo |
| AC-graphql-01..13 | `GraphQLTests.AC_graphql_01_*` … `AC_graphql_13_*` (`services/gateway/tests/Maksov.VideoService.Gateway.GraphQL.Tests`), demo (query, mutation, 401) | green |
| AC-gateway-02 | `GatewayTests.AC_gateway_02_routes_to_the_owning_service` | green |
