-- Description service: video analysis results; the table itself serves as the job queue.
create table analyses (
    video_id       uuid        primary key,
    owner_id       text        not null,
    source_key     text        not null,
    event_id       uuid        not null,
    duration_ms    integer     not null,
    width          integer     not null,
    height         integer     not null,
    status         text        not null check (status in ('queued', 'processing', 'done', 'failed')),
    attempts       integer     not null default 0,
    max_attempts   integer     not null default 3,
    run_after      timestamptz not null default now(),
    locked_until   timestamptz,
    locked_by      text,
    description    text,
    tags           text[]      not null default '{}',
    facts          jsonb,
    failure_reason text,
    last_error     text,
    created_at     timestamptz not null default now(),
    updated_at     timestamptz not null default now()
);

-- Selection of jobs ready to run (queued or with an expired lease); the partial index stays small.
create index ix_analyses_pending on analyses (run_after) where status in ('queued', 'processing');
