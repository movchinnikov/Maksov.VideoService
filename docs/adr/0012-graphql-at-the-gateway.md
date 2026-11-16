# ADR 0012: GraphQL at the gateway

- Status: accepted
- Date: 2026-11-17
- Lesson: 3
- Related: ADR 0006 (gateway), 0010, 0011

## Context

Screens (feed card, profile, video page) need data from many services: video card, owner, likes, comments, relation to the
viewer. With REST that is a waterfall of calls from the client. A GraphQL layer at the edge lets the client ask for exactly the
shape it needs in one request, while services stay small and REST-based.

## Decision

- Library: **HotChocolate** (open source, local). A new project `Maksov.VideoService.Gateway.GraphQL` in the gateway solution;
  the gateway host maps `POST /graphql` (and the schema endpoint in development) next to the YARP routes. REST `/api/**` is unchanged.
- Authentication: the JWT is validated at the gateway as before; resolvers call the services over HTTP with the caller's
  `Authorization` header (the services still validate it: zero trust). `/internal/**` is never used by GraphQL.
- Schema (queries and mutations, **no subscriptions**):
  - Query: `me`, `user(id)`, `users(query, ids, limit)`, `video(id)`, `feed(cursor, limit)`, `userVideos(userId, cursor, limit)`,
    `recommendations(limit)`, `followers(userId, cursor, limit)`, `following(userId, cursor, limit)`.
  - Types: `User { id username displayName bio stats videos }`, `Video { id title owner createdAt durationMs width height playback likes comments description }`,
    `Comment { id videoId author text createdAt canDelete likes }`, `LikeInfo { count likedByMe }`.
  - Mutation: `likeVideo`, `unlikeVideo`, `likeComment`, `unlikeComment`, `addComment`, `deleteComment`, `follow`, `unfollow`.
- Batching: DataLoaders turn N field resolutions into one downstream call per service
  (`users?ids=`, `videos/cards?ids=`, `likes/videos?ids=`, `likes/comments?ids=`).
- Protection: max execution depth, max page size (the REST limits), request size limit, rate limiting like the REST routes,
  introspection only when `GraphQL:AllowIntrospection=true` (set for the local stack).
- Errors from services map to GraphQL errors with codes (`NOT_FOUND`, `VALIDATION`, `UNAVAILABLE`); partial data is allowed.

## Consequences

- One round trip per screen; the web client may migrate screen by screen (REST keeps working).
- The gateway now has business-shaped code (a schema); it must stay a thin composition layer without its own storage.
- N+1 against services without batch endpoints (e.g. friends stats) is possible; batch endpoints are added when needed.
