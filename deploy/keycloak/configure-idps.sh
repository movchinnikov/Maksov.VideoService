#!/usr/bin/env bash
# Connects external SSO providers (Google, Microsoft Entra ID) to the videoservice realm via kcadm.
# Secrets are taken from .env and are NOT stored in the repository/realm export.
#
#   GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET          — https://console.cloud.google.com/apis/credentials
#   MICROSOFT_CLIENT_ID / MICROSOFT_CLIENT_SECRET    — Entra ID → App registrations
#   MICROSOFT_TENANT (default: common)
#
# Redirect URIs for the providers:
#   http://localhost:8080/realms/videoservice/broker/google/endpoint
#   http://localhost:8080/realms/videoservice/broker/microsoft/endpoint
#
# Run: make idps
set -euo pipefail

: "${KC_BOOTSTRAP_ADMIN_USERNAME:?}" "${KC_BOOTSTRAP_ADMIN_PASSWORD:?}"
KCADM=(docker compose --env-file .env -f deploy/compose/docker-compose.yml exec -T keycloak /opt/keycloak/bin/kcadm.sh)

"${KCADM[@]}" config credentials --server http://localhost:8080 --realm master \
  --user "$KC_BOOTSTRAP_ADMIN_USERNAME" --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null

upsert_idp() {
  local alias="$1"; shift
  if "${KCADM[@]}" get "identity-provider/instances/$alias" -r videoservice >/dev/null 2>&1; then
    "${KCADM[@]}" update "identity-provider/instances/$alias" -r videoservice "$@"
  else
    "${KCADM[@]}" create identity-provider/instances -r videoservice -s "alias=$alias" "$@"
  fi
  echo "IdP '$alias' configured"
}

# Link to an existing account by e-mail — only for verified addresses (trustEmail).
if [[ -n "${GOOGLE_CLIENT_ID:-}" && -n "${GOOGLE_CLIENT_SECRET:-}" ]]; then
  upsert_idp google -s providerId=google -s enabled=true -s trustEmail=true \
    -s 'config.clientId='"$GOOGLE_CLIENT_ID" -s 'config.clientSecret='"$GOOGLE_CLIENT_SECRET" \
    -s 'config.defaultScope=openid profile email' -s 'config.syncMode=IMPORT'
else
  echo "Google skipped: GOOGLE_CLIENT_ID/SECRET are not set"
fi

if [[ -n "${MICROSOFT_CLIENT_ID:-}" && -n "${MICROSOFT_CLIENT_SECRET:-}" ]]; then
  upsert_idp microsoft -s providerId=microsoft -s enabled=true -s trustEmail=false \
    -s 'config.clientId='"$MICROSOFT_CLIENT_ID" -s 'config.clientSecret='"$MICROSOFT_CLIENT_SECRET" \
    -s 'config.tenantId='"${MICROSOFT_TENANT:-common}" -s 'config.defaultScope=openid profile email' -s 'config.syncMode=IMPORT'
else
  echo "Microsoft skipped: MICROSOFT_CLIENT_ID/SECRET are not set"
fi

