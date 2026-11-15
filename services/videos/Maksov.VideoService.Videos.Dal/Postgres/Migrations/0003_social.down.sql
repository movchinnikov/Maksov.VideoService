drop index if exists ix_outbox_pending;
delete from outbox where subscriber <> 'analysis';
alter table outbox drop constraint outbox_pkey;
alter table outbox add primary key (id);
alter table outbox drop column subscriber;
create index ix_outbox_pending on outbox (next_attempt_at) where sent_at is null;

drop index if exists ix_videos_ready_by_owner;
delete from videos where status = 'deleted';
alter table videos drop constraint videos_status_check;
alter table videos add constraint videos_status_check
    check (status in ('awaiting_upload', 'uploaded', 'processing', 'ready', 'failed'));
alter table videos drop column deleted_at;
alter table videos drop column owner_username;
