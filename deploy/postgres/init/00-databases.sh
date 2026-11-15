#!/bin/bash
# A single Postgres instance; each service has its own database and role (service data is visible only to it).
# Runs on first volume initialization. Passwords come from the container environment variables.
set -euo pipefail

create_service_db() {
  local name="$1" password="$2"
  psql -v ON_ERROR_STOP=1 -v name="$name" -v pw="$password" --username "$POSTGRES_USER" --dbname postgres <<-'SQL'
    create role :"name" login password :'pw';
    create database :"name" owner :"name";
SQL
}

create_service_db videos   "${VIDEOS_DB_PASSWORD:?}"
create_service_db users    "${USERS_DB_PASSWORD:?}"
create_service_db analysis "${ANALYSIS_DB_PASSWORD:?}"
create_service_db comments "${COMMENTS_DB_PASSWORD:?}"
