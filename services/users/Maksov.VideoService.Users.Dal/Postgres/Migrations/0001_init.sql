-- Users service: profiles. The identifier is the Keycloak sub.
create table profiles (
    user_id      text        primary key,
    username     text        not null,
    display_name text        not null check (char_length(display_name) between 1 and 60),
    bio          text        not null default '' check (char_length(bio) <= 300),
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now()
);
