#!/usr/bin/env bash
# Creates or updates the local .env from .env.example.
#  - keys missing from .env are appended with the value from .env.example;
#  - secrets (PASSWORD / KEY / SECRET in the name) that are empty in .env.example, or still carry an old
#    "change-me" placeholder in .env, get a random value generated on this machine.
# .env is git-ignored: secrets never leave the local machine and are never committed.
set -euo pipefail
cd "$(dirname "$0")/.."

example=.env.example
target=.env
touch "$target"
chmod 600 "$target"

random() { python3 -c 'import secrets,string,sys; a=string.ascii_letters+string.digits; print("".join(secrets.choice(a) for _ in range(int(sys.argv[1]))))' "${1:-24}"; }

is_secret() {
  case "$1" in
    GOOGLE_CLIENT_*|MICROSOFT_CLIENT_*) return 1 ;;  # external IdP credentials are supplied by the developer
    *PASSWORD*|*_KEY|*SECRET*) return 0 ;;
    *) return 1 ;;
  esac
}

set_value() {  # replace KEY=... in .env (portable: no sed -i differences)
  local key="$1" value="$2" tmp
  tmp=$(mktemp)
  awk -v k="$key" -v v="$value" 'BEGIN{FS=OFS="="} $1==k {print k "=" v; next} {print}' "$target" >"$tmp"
  cat "$tmp" >"$target"
  rm -f "$tmp"
}

while IFS= read -r line || [[ -n "$line" ]]; do
  [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || continue
  key=${line%%=*}
  default=${line#*=}
  current=$(grep -E "^${key}=" "$target" | head -n1 | cut -d= -f2- || true)

  if ! grep -qE "^${key}=" "$target"; then
    if is_secret "$key" && [[ -z "$default" || "$default" == change-me* ]]; then
      default=$(random 24)
    fi
    printf '%s=%s\n' "$key" "$default" >>"$target"
    echo "added to .env: $key"
  elif is_secret "$key" && [[ "$current" == change-me* || "$current" == "demo-demo" ]]; then
    set_value "$key" "$(random 24)"
    echo "regenerated weak value in .env: $key"
  fi
done <"$example"

