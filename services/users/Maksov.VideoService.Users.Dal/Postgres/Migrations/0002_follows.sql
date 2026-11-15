-- Follows ("who I follow") and people search.
create table follows (
    follower_id text        not null,
    followee_id text        not null,
    created_at  timestamptz not null default now(),
    primary key (follower_id, followee_id),
    check (follower_id <> followee_id)
);

-- A person's followers (count, list).
create index ix_follows_followee on follows (followee_id);

-- Case-insensitive search by login/name prefix.
create index ix_profiles_username_prefix on profiles (lower(username) text_pattern_ops);
create index ix_profiles_display_name_prefix on profiles (lower(display_name) text_pattern_ops);
