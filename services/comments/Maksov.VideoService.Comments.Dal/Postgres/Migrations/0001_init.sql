-- Comments service. Replica of known videos (the owner arrives via the video.processed.v1 event);
-- a deleted video stays as a "tombstone" so a late event cannot resurrect it.
create table known_videos (
    video_id   uuid        primary key,
    owner_id   text        not null,
    deleted_at timestamptz,
    created_at timestamptz not null default now()
);

create table comments (
    id              uuid        primary key,
    video_id        uuid        not null references known_videos (video_id) on delete cascade,
    author_id       text        not null,
    author_username text        not null,
    text            text        not null check (char_length(text) between 1 and 500),
    created_at      timestamptz not null default now()
);

create index comments_video_created_idx on comments (video_id, created_at desc, id desc);
