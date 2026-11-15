-- Transactional outbox: video events are written in the same transaction as the status change,
-- and delivered to subscribers by a separate dispatcher (lesson 4: the transport changes to Kafka, the table stays).
create table outbox (
    id              uuid        primary key,
    topic           text        not null,
    payload         jsonb       not null,
    trace_parent    text,
    attempts        integer     not null default 0,
    next_attempt_at timestamptz not null default now(),
    locked_until    timestamptz,
    sent_at         timestamptz,
    last_error      text,
    created_at      timestamptz not null default now()
);

-- Unsent events; the partial index stays small.
create index ix_outbox_pending on outbox (next_attempt_at) where sent_at is null;
