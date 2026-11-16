-- Likes service. likes: one row per (target, user); like_counts: counter maintained in the same transaction as the like row.
-- known_videos / known_comments are replicas fed by events (404 for unknown targets without calling other services);
-- deleted rows stay as tombstones so a late event cannot resurrect a deleted video or comment.
create table known_videos (
    video_id   uuid        primary key,
    owner_id   text        not null,
    deleted_at timestamptz,
    created_at timestamptz not null default now()
);

create table known_comments (
    comment_id uuid        primary key,
    video_id   uuid        not null,
    author_id  text        not null,
    deleted_at timestamptz,
    created_at timestamptz not null default now()
);

create index known_comments_video_idx on known_comments (video_id);

create table likes (
    target_type text        not null check (target_type in ('video', 'comment')),
    target_id   uuid        not null,
    user_id     text        not null,
    video_id    uuid        not null,
    created_at  timestamptz not null default now(),
    primary key (target_type, target_id, user_id)
);

create index likes_target_created_idx on likes (target_type, target_id, created_at desc, user_id desc);
create index likes_video_idx on likes (video_id);

create table like_counts (
    target_type text not null check (target_type in ('video', 'comment')),
    target_id   uuid not null,
    count       int  not null check (count >= 0),
    primary key (target_type, target_id)
);

-- Outbox (shared shape, see OutboxSql.CreateTable in BuildingBlocks).
create table outbox (
    id              uuid        not null,
    subscriber      text        not null,
    topic           text        not null,
    payload         jsonb       not null,
    attempts        int         not null default 0,
    next_attempt_at timestamptz not null default now(),
    locked_until    timestamptz,
    sent_at         timestamptz,
    last_error      text,
    created_at      timestamptz not null default now(),
    primary key (id, subscriber)
);

create index ix_outbox_pending on outbox (subscriber, next_attempt_at) where sent_at is null;
