-- Lesson 1: videos and the transcode job queue on Postgres.
-- The queue is replaced by Kafka in lesson 4 (the IJobQueue port in media-worker does not change).

create table videos (
    id              uuid        primary key,
    owner_id        text        not null,
    title           text        not null check (char_length(title) between 1 and 200),
    status          text        not null check (status in ('awaiting_upload', 'uploaded', 'processing', 'ready', 'failed')),
    source_key      text        not null,
    content_type    text        not null,
    declared_size   bigint      not null,
    size_bytes      bigint,
    duration_ms     integer,
    width           integer,
    height          integer,
    renditions      jsonb,
    failure_reason  text,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now()
);

-- "My videos": keyset pagination by (created_at, id) within an owner.
create index ix_videos_owner_created on videos (owner_id, created_at desc, id desc);

create table transcode_jobs (
    id            uuid        primary key,
    video_id      uuid        not null unique references videos (id) on delete cascade,
    status        text        not null check (status in ('queued', 'running', 'succeeded', 'failed')),
    attempts      integer     not null default 0,
    max_attempts  integer     not null default 3,
    run_after     timestamptz not null default now(),
    locked_until  timestamptz,
    locked_by     text,
    last_error    text,
    trace_parent  text,
    created_at    timestamptz not null default now(),
    updated_at    timestamptz not null default now()
);

-- Selection of jobs ready to run (queued or with an expired lease); the partial index stays small.
create index ix_transcode_jobs_pending on transcode_jobs (run_after) where status in ('queued', 'running');

