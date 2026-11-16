-- Transactional outbox: comment.added.v1 / comment.deleted.v1 rows (one per subscriber), written with the comment change.
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
