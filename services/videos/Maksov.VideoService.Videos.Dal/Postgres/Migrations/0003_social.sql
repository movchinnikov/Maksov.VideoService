-- Social features: the author is visible in the feed, videos can be deleted, events are delivered to each subscriber separately.

-- Snapshot of the author's login at upload time (for the feed and profile without calls to the users service).
alter table videos add column owner_username text not null default '';
alter table videos add column deleted_at timestamptz;

alter table videos drop constraint videos_status_check;
alter table videos add constraint videos_status_check
    check (status in ('awaiting_upload', 'uploaded', 'processing', 'ready', 'failed', 'deleted'));

-- Feed and profile: the author's ready videos by time.
create index ix_videos_ready_by_owner on videos (owner_id, created_at desc, id desc) where status = 'ready';

-- Outbox: one row per (event, subscriber), each with its own delivery, retries and backoff.
alter table outbox add column subscriber text not null default 'analysis';
alter table outbox drop constraint outbox_pkey;
alter table outbox add primary key (id, subscriber);
drop index ix_outbox_pending;
create index ix_outbox_pending on outbox (subscriber, next_attempt_at) where sent_at is null;
