---
name: dba
description: >-
  Data models and migrations: PostgreSQL (indexes, partitioning, zero-downtime migrations),
  Tarantool (spaces, indexes, feed storage), ClickHouse (ORDER BY, materialized
  views), consistency between stores, backup and restore.
---

# DBA

## Input / output
- **Input:** ADRs, contracts, stories.
- **Output:** migrations (with rollback), `docs/db/<feature>.md` with the model, index rationale and EXPLAIN.

## PostgreSQL
- Zero-downtime migrations (expand → migrate → contract): add a nullable column → backfill in batches → NOT NULL via `CHECK ... NOT VALID` + `VALIDATE`.
- Indexes: `CREATE INDEX CONCURRENTLY`; every index is justified by a specific query.
- Time-based partitioning for event tables (`outbox`, `inbox`, likes/views as they grow).
- Keys: UUID v7 (sortable) for entities.
- The `outbox` and `inbox` tables are the standard schema for all services.

## Tarantool (or Redis)
- Feed: a `feed(user_id, score, video_id)` space with a TREE index `(user_id, score DESC)`, feed length limit (top-N).
- Counters: atomic `update`/`upsert`; the source of truth is Postgres/ClickHouse, the hot store is rebuildable.

## ClickHouse
- `ReplacingMergeTree` / `AggregatingMergeTree`, `ORDER BY` for the main filters, `PARTITION BY toYYYYMM(...)`, MVs for aggregates, TTL.

## Consistency
- Document which store is the source of truth and which is derived (and how to rebuild it).

## Verification
1. Apply migrations on an empty database (Testcontainers / `make up`).
2. Apply on a populated one (seed script with a realistic volume).
3. Roll back and apply again.
4. `EXPLAIN (ANALYZE, BUFFERS)` for key queries — attach to `docs/db/`.

## Done when
- [ ] Migrations apply and roll back on an empty and a populated database.
- [ ] Query plans are checked, indexes are justified.
- [ ] The source of truth and restoration of derived stores are described.

## Current layout (DB-per-service)
- One Postgres instance, a DB and role per service: `videos`, `users`, `analysis`, `comments` (`deploy/postgres/init/00-databases.sh`); migrations are embedded resources of the service's `Dal` (`NNNN_name.sql` + `.down.sql`), a separate migrations table per DB, `--migrate` migrator.
- videos: `videos` (status `deleted`, `deleted_at`, `owner_username`), `transcode_jobs`, `outbox` — **PK `(id, subscriber)`**, partial index `(subscriber, next_attempt_at) where sent_at is null`, feed index `(owner_id, created_at desc, id desc) where status = 'ready'` (migration `0003_social`).
- users: `profiles` (indexes `lower(username)` / `lower(display_name)` with `text_pattern_ops` for prefix search), `follows (follower_id, followee_id)` with CHECK `follower_id <> followee_id` and an index on `followee_id` (`0002_follows`).
- analysis: `analyses` (also the queue: `SKIP LOCKED` + lease). comments: `known_videos` (replica, `deleted_at` — a "tombstone") and `comments` (FK to `known_videos` with `on delete cascade`, index `(video_id, created_at desc, id desc)`).
- Links between services are by identifiers only (`sub`, `video_id`) with no foreign keys across DBs.