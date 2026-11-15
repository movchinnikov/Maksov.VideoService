-- Friends service: the social graph and user embeddings for recommendations.
-- The extension is normally created by the database init script (superuser); this keeps tests and fresh databases working.
create extension if not exists vector;

create table follows (
    follower_id text        not null,
    followee_id text        not null,
    created_at  timestamptz not null default now(),
    primary key (follower_id, followee_id),
    check (follower_id <> followee_id)
);

-- A person's followers (count, list).
create index ix_follows_followee on follows (followee_id);

-- Graph embeddings (see GraphEmbedding, model_version graph-hash-v1), 32 dimensions.
create table user_embeddings (
    user_id       text        primary key,
    embedding     vector(32)  not null,
    model_version text        not null,
    updated_at    timestamptz not null default now()
);

create index ix_user_embeddings_hnsw on user_embeddings using hnsw (embedding vector_cosine_ops);
