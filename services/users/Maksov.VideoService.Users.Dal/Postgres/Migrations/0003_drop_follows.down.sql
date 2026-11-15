create table follows (
    follower_id text        not null,
    followee_id text        not null,
    created_at  timestamptz not null default now(),
    primary key (follower_id, followee_id),
    check (follower_id <> followee_id)
);

create index ix_follows_followee on follows (followee_id);
