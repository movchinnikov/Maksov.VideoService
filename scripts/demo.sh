#!/usr/bin/env bash
# End-to-end scenario on a running stack (make up):
# Keycloak token → create upload → PUT to MinIO → complete → wait for Ready →
# check DASH/HLS and segments → video description (separate background job) → profile → "my videos" →
# complete idempotency → second user: follow, feed (ids → cards), comments, recommended friends, unfollow → delete a video and the
# event cascade (description, comments) → anonymous isolation and closed internal endpoints.
# The test clip is vertical 9:16 with no SAR (like a phone), three shots and audio with a pause.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; source .env; set +a

API=${API:-http://localhost:8081}
KC=${KC:-http://localhost:8080}
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✗ %s\033[0m\n' "$*"; exit 1; }
json() { python3 -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1], {}, {'d': d}))" "$1"; }

step "Token for user ${DEMO_USER} (dev-only password grant)"
TOKEN=$(curl -fsS "$KC/realms/videoservice/protocol/openid-connect/token" \
  -d grant_type=password -d client_id=videoservice-cli \
  -d username="$DEMO_USER" -d password="$DEMO_PASSWORD" | json "d['access_token']")
AUTH=(-H "Authorization: Bearer $TOKEN")
ok "received"

step "Test clip: 6 s, 720×1280 (vertical), no SAR, 3 shots, 3 s of audio + 3 s of silence"
SAMPLE="$WORK/sample.mp4"
FFMPEG_ARGS=(-hide_banner -loglevel error -y
  -f lavfi -i color=c=red:s=720x1280:r=30:d=2 -f lavfi -i color=c=blue:s=720x1280:r=30:d=2 -f lavfi -i color=c=green:s=720x1280:r=30:d=2
  -f lavfi -i sine=frequency=440:duration=3 -f lavfi -i "anullsrc=r=44100:cl=mono,atrim=0:3"
  -filter_complex "[0][1][2]concat=n=3:v=1:a=0,setsar=0[v];[3][4]concat=n=2:v=0:a=1[a]"
  -map "[v]" -map "[a]" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest)
if command -v ffmpeg >/dev/null; then
  ffmpeg "${FFMPEG_ARGS[@]}" "$SAMPLE"
else
  docker run --rm -v "$WORK":/out --entrypoint ffmpeg videoservice/media-worker:local "${FFMPEG_ARGS[@]}" /out/sample.mp4
fi
SIZE=$(wc -c <"$SAMPLE" | tr -d ' ')
ok "$SIZE bytes"

step "Create upload (through the gateway → videos)"
CREATE=$(curl -fsS "${AUTH[@]}" -H 'Content-Type: application/json' "$API/api/videos" \
  -d "{\"title\":\"Demo $(date +%H:%M:%S)\",\"fileName\":\"sample.mp4\",\"contentType\":\"video/mp4\",\"sizeBytes\":$SIZE}")
VIDEO_ID=$(echo "$CREATE" | json "d['videoId']")
UPLOAD_URL=$(echo "$CREATE" | json "d['uploadUrl']")
ok "video $VIDEO_ID"

step "PUT the file straight to MinIO via the presigned URL"
curl -fsS -X PUT -H 'Content-Type: video/mp4' --data-binary @"$SAMPLE" "$UPLOAD_URL" >/dev/null
ok "uploaded"

step "Complete upload → transcode job"
STATUS=$(curl -fsS -X POST "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/complete" | json "d['status']")
[[ "$STATUS" == "uploaded" || "$STATUS" == "processing" || "$STATUS" == "ready" ]] || fail "status $STATUS"
ok "status $STATUS"

step "Repeated complete is idempotent"
curl -fsS -o /dev/null -w '%{http_code}' -X POST "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/complete" | grep -q 202 && ok "202, no second job"

step "Wait for Ready (up to 120 s)"
for i in $(seq 1 120); do
  VIDEO=$(curl -fsS "${AUTH[@]}" "$API/api/videos/$VIDEO_ID")
  STATUS=$(echo "$VIDEO" | json "d['status']")
  [[ "$STATUS" == "ready" ]] && break
  [[ "$STATUS" == "failed" ]] && fail "processing failed: $(echo "$VIDEO" | json "d['failureReason']")"
  sleep 1
done
[[ "$STATUS" == "ready" ]] || fail "did not reach Ready (status $STATUS)"
ok "ready in ~${i} s: $(echo "$VIDEO" | json "', '.join(r['name'] for r in d['renditions'])")"
[[ "$(echo "$VIDEO" | json "d['width'] < d['height']")" == "True" ]] && ok "vertical: $(echo "$VIDEO" | json "str(d['width'])+'×'+str(d['height'])")"

step "DASH manifest"
MPD=$(curl -fsS "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/stream/manifest.mpd")
grep -q '<MPD' <<<"$MPD" || fail "not an MPD"
REPS=$(grep -o '<Representation ' <<<"$MPD" | wc -l | tr -d ' ')
ok "Representation: $REPS"

step "HLS master playlist and segment"
MASTER=$(curl -fsS "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/stream/master.m3u8")
VARIANTS=$(grep -c '#EXT-X-STREAM-INF' <<<"$MASTER")
MEDIA=$(grep -m1 -v '^#' <<<"$MASTER" | tr -d '\r')
ok "variants: $VARIANTS"
INIT=$(curl -fsS "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/stream/$MEDIA" | grep -o 'URI="[^"]*"' | head -1 | cut -d'"' -f2)
curl -fsS "${AUTH[@]}" -o "$WORK/init.m4s" "$API/api/videos/$VIDEO_ID/stream/$INIT"
ok "init segment $INIT: $(wc -c <"$WORK/init.m4s" | tr -d ' ') bytes (shared by DASH and HLS)"

step "Poster"
curl -fsS "${AUTH[@]}" -o "$WORK/poster.jpg" -w '  %{content_type}, %{size_download} bytes\n' "$API/api/videos/$VIDEO_ID/poster"

step "Video description — a separate background job of the analysis service (up to 90 s)"
DESC_STATUS=""
for i in $(seq 1 90); do
  CODE=$(curl -s -o "$WORK/desc.json" -w '%{http_code}' "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/description")
  if [[ "$CODE" == "200" ]]; then
    DESC_STATUS=$(json "d['status']" <"$WORK/desc.json")
    [[ "$DESC_STATUS" == "done" || "$DESC_STATUS" == "failed" ]] && break
  fi
  sleep 1
done
[[ "$DESC_STATUS" == "done" ]] || fail "description was not built (status '$DESC_STATUS')"
ok "ready in ~${i} s"
printf '    %s\n' "$(json "d['text']" <"$WORK/desc.json")"
printf '    tags: %s\n' "$(json "' · '.join(d['tags'])" <"$WORK/desc.json")"
json "d['text']" <"$WORK/desc.json" | grep -q 'Vertical' && ok "clip recognised as vertical"
json "d['text']" <"$WORK/desc.json" | grep -q '2 scene cuts' && ok "found 2 scene cuts (red → blue → green)"
json "d['text']" <"$WORK/desc.json" | grep -q 'pauses' && ok "found silence in the second half of the audio"

step "User profile (users service)"
ME=$(curl -fsS "${AUTH[@]}" "$API/api/users/me")
ok "me: $(echo "$ME" | json "d['username']+' / '+d['displayName']")"
curl -fsS -X PUT "${AUTH[@]}" -H 'Content-Type: application/json' "$API/api/users/me" \
  -d '{"displayName":"Demo director","bio":"I shoot vertical"}' | json "d['displayName']" | grep -q 'Demo director' && ok "name updated"

step ""My videos" contain the clip"
curl -fsS "${AUTH[@]}" "$API/api/videos?limit=50" | json "[i['id'] for i in d['items']]" | grep -q "$VIDEO_ID" && ok "listed"

step "Second user ${FRIEND_USER:-demo-friend} (created through the Keycloak admin API if missing)"
FRIEND_USER=${FRIEND_USER:-demo-friend}
ADMIN_TOKEN=$(curl -fsS "$KC/realms/master/protocol/openid-connect/token" \
  -d grant_type=password -d client_id=admin-cli \
  -d username="$KC_BOOTSTRAP_ADMIN_USERNAME" -d password="$KC_BOOTSTRAP_ADMIN_PASSWORD" | json "d['access_token']")
ensure_user() {  # ensure_user <username> <phone>: creates the user if missing (409 is fine)
  curl -s -o /dev/null -w '' -X POST -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
    "$KC/admin/realms/videoservice/users" \
    -d "{\"username\":\"$1\",\"email\":\"$1@example.local\",\"enabled\":true,\"emailVerified\":true,\"attributes\":{\"phoneNumber\":[\"$2\"]},\"credentials\":[{\"type\":\"password\",\"value\":\"$DEMO_PASSWORD\",\"temporary\":false}]}" || true
}
token_of() {  # token_of <username>
  curl -fsS "$KC/realms/videoservice/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=videoservice-cli \
    -d username="$1" -d password="$DEMO_PASSWORD" | json "d['access_token']"
}
ensure_user "$FRIEND_USER" "+79990000002"
BUDDY_USER=${BUDDY_USER:-demo-buddy}
ensure_user "$BUDDY_USER" "+79990000003"
TOKEN2=$(token_of "$FRIEND_USER")
AUTH2=(-H "Authorization: Bearer $TOKEN2")
ME_ID=$(echo "$ME" | json "d['id']")
FRIEND_ID=$(curl -fsS "${AUTH2[@]}" "$API/api/users/me" | json "d['id']")
ok "token received, id $FRIEND_ID"

feed_has() { curl -fsS "${AUTH2[@]}" "$API/api/feed?limit=30" | json "'$1' in d['ids']"; }

step "Feed: without a follow another user's clip is absent; the author's profile shows their grid"
[[ "$(feed_has "$VIDEO_ID")" == "False" ]] && ok "feed is empty before following"
curl -fsS "${AUTH2[@]}" "$API/api/users/$ME_ID/videos" | json "'$VIDEO_ID' in [i['id'] for i in d['items']]" | grep -q True && ok "the author's profile grid contains the clip"

step "Follow → clip appears in the friend's feed, stream and description are visible"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X PUT "${AUTH2[@]}" "$API/api/users/$ME_ID/follow") == 204 ]] && ok "followed (204)"
curl -fsS "${AUTH2[@]}" "$API/api/friends/$ME_ID/stats" | json "d['isFollowing'] and d['followersCount'] >= 1" | grep -q True && ok "stats (friends service): isFollowing, followers >= 1"
curl -fsS "${AUTH2[@]}" "$API/api/friends/status?ids=$ME_ID,nobody" | json "d['following'] == ['$ME_ID']" | grep -q True && ok "status lists exactly the followed id"
# The follow cache in videos lives for 20 s — wait for the clip to show up in the feed.
for i in $(seq 1 30); do [[ "$(feed_has "$VIDEO_ID")" == "True" ]] && break; sleep 1; done
[[ "$(feed_has "$VIDEO_ID")" == "True" ]] || fail "the clip did not appear in the follower's feed"
ok "clip is in the follower's feed (feed service returns ids only)"
curl -fsS "${AUTH2[@]}" "$API/api/videos/cards?ids=$VIDEO_ID,00000000-0000-0000-0000-000000000000" | json "[i['id'] for i in d['items']] == ['$VIDEO_ID']" | grep -q True && ok "cards (videos service) resolve the id; unknown ids are skipped"
[[ $(curl -s -o /dev/null -w '%{http_code}' "${AUTH2[@]}" "$API/api/videos/$VIDEO_ID/stream/manifest.mpd") == 200 ]] && ok "follower can watch the stream"
[[ $(curl -s -o /dev/null -w '%{http_code}' "${AUTH2[@]}" "$API/api/videos/$VIDEO_ID/description") == 200 ]] && ok "follower sees the finished description"

step "Comments: a separate comments service (it learns about the video from an event, up to 30 s)"
CODE=000
for i in $(seq 1 30); do
  CODE=$(curl -s -o "$WORK/c1.json" -w '%{http_code}' -X POST "${AUTH2[@]}" -H 'Content-Type: application/json' \
    "$API/api/videos/$VIDEO_ID/comments" -d '{"text":"Nicely shot!"}')
  [[ "$CODE" == "201" ]] && break
  sleep 1
done
[[ "$CODE" == "201" ]] || fail "comment was not created (HTTP $CODE)"
C1=$(json "d['id']" <"$WORK/c1.json")
ok "comment $C1 created (~${i} s)"
curl -fsS "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/comments" | json "d['total'] == 1 and d['items'][0]['canDelete'] and d['items'][0]['authorUsername'] == '$FRIEND_USER'" | grep -q True && ok "the owner sees the comment and can delete it"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X POST "${AUTH[@]}" -H 'Content-Type: application/json' "$API/api/videos/$VIDEO_ID/comments" -d '{"text":""}') == 400 ]] && ok "empty comment — 400"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X DELETE "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/comments/$C1") == 204 ]] && ok "the video owner deleted someone else's comment (204)"
curl -fsS "${AUTH2[@]}" "$API/api/videos/$VIDEO_ID/comments" | json "d['total'] == 0" | grep -q True && ok "list is empty"

step "Recommended friends: friends of friends (pgvector similarity)"
TOKEN3=$(token_of "$BUDDY_USER")
AUTH3=(-H "Authorization: Bearer $TOKEN3")
BUDDY_ID=$(curl -fsS "${AUTH3[@]}" "$API/api/users/me" | json "d['id']")
[[ $(curl -s -o /dev/null -w '%{http_code}' -X PUT "${AUTH2[@]}" "$API/api/users/$BUDDY_ID/follow") == 204 ]] && ok "$FRIEND_USER follows $BUDDY_USER"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X PUT "${AUTH[@]}" "$API/api/users/$FRIEND_ID/follow") == 204 ]] && ok "demo follows $FRIEND_USER"
curl -fsS "${AUTH[@]}" "$API/api/friends/$ME_ID/followers" | json "[(i['userId'], i['isFollowing'], i['followsYou'], i['isMutual']) for i in d['items']] == [('$FRIEND_ID', True, True, True)] and d['total'] == 1" | grep -q True && ok "followers list: $FRIEND_USER follows demo and demo follows back — mutual"
curl -fsS "${AUTH[@]}" "$API/api/friends/$ME_ID/following" | json "[(i['userId'], i['isMutual']) for i in d['items']] == [('$FRIEND_ID', True)]" | grep -q True && ok "following list shows the mutual status"
curl -fsS "${AUTH2[@]}" "$API/api/friends/$ME_ID/stats" | json "d['isFollowing'] and d['followsYou'] and d['isMutual']" | grep -q True && ok "stats: isMutual"
REC=$(curl -fsS "${AUTH[@]}" "$API/api/friends/recommendations?limit=10")
echo "$REC" | json "'$BUDDY_ID' in [i['userId'] for i in d['items']]" | grep -q True && ok "demo is offered $BUDDY_USER (friend of a friend)"
echo "$REC" | json "[i['mutualFriends'] for i in d['items'] if i['userId'] == '$BUDDY_ID'] == [1]" | grep -q True && ok "1 mutual friend"
curl -fsS "${AUTH[@]}" "$API/api/users?ids=$BUDDY_ID" | json "[i['username'] for i in d['items']] == ['$BUDDY_USER']" | grep -q True && ok "profile resolved by the users service (batch ids)"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X PUT "${AUTH[@]}" "$API/api/users/$BUDDY_ID/follow") == 204 ]] && ok "demo follows $BUDDY_USER"
curl -fsS "${AUTH[@]}" "$API/api/friends/recommendations?limit=10" | json "'$BUDDY_ID' not in [i['userId'] for i in d['items']]" | grep -q True && ok "followed users are no longer recommended"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X DELETE "${AUTH[@]}" "$API/api/users/$BUDDY_ID/follow") == 204 ]] && ok "unfollowed again"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X DELETE "${AUTH[@]}" "$API/api/users/$FRIEND_ID/follow") == 204 ]] && ok "demo unfollowed $FRIEND_USER"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X PUT "${AUTH[@]}" "$API/api/users/nobody-at-all/follow") == 404 ]] && ok "following an unknown profile — 404"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X PUT "${AUTH[@]}" "$API/api/users/$ME_ID/follow") == 400 ]] && ok "following yourself — 400"

step "Unfollow → clip disappears from the feed"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X DELETE "${AUTH2[@]}" "$API/api/users/$ME_ID/follow") == 204 ]] && ok "unfollowed (204)"
for i in $(seq 1 30); do [[ "$(feed_has "$VIDEO_ID")" == "False" ]] && break; sleep 1; done
[[ "$(feed_has "$VIDEO_ID")" == "False" ]] && ok "the clip is gone from the friend's feed"

step "Deletion: second clip → Ready → a stranger cannot delete it, the owner can (MinIO, description, comments)"
CREATE2=$(curl -fsS "${AUTH[@]}" -H 'Content-Type: application/json' "$API/api/videos" \
  -d "{\"title\":\"To delete\",\"fileName\":\"sample.mp4\",\"contentType\":\"video/mp4\",\"sizeBytes\":$SIZE}")
DEL_ID=$(echo "$CREATE2" | json "d['videoId']")
curl -fsS -X PUT -H 'Content-Type: video/mp4' --data-binary @"$SAMPLE" "$(echo "$CREATE2" | json "d['uploadUrl']")" >/dev/null
curl -fsS -X POST "${AUTH[@]}" "$API/api/videos/$DEL_ID/complete" >/dev/null
for i in $(seq 1 120); do
  [[ "$(curl -fsS "${AUTH[@]}" "$API/api/videos/$DEL_ID" | json "d['status']")" == "ready" ]] && break
  sleep 1
done
ok "clip $DEL_ID is ready"
for i in $(seq 1 30); do
  CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST "${AUTH2[@]}" -H 'Content-Type: application/json' "$API/api/videos/$DEL_ID/comments" -d '{"text":"about to vanish"}')
  [[ "$CODE" == "201" ]] && break
  sleep 1
done
[[ "$CODE" == "201" ]] && ok "comment on the clip to be deleted created"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X DELETE "${AUTH2[@]}" "$API/api/videos/$DEL_ID") == 404 ]] && ok "DELETE by a stranger — 404"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X DELETE "${AUTH[@]}" "$API/api/videos/$DEL_ID") == 204 ]] && ok "owner deleted it (204)"
[[ $(curl -s -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$API/api/videos/$DEL_ID") == 404 ]] && ok "GET → 404"
[[ $(curl -s -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$API/api/videos/$DEL_ID/stream/manifest.mpd") == 404 ]] && ok "manifest → 404 (files removed from MinIO)"
curl -fsS "${AUTH[@]}" "$API/api/videos?limit=50" | json "'$DEL_ID' in [i['id'] for i in d['items']]" | grep -q False && ok "not in \"my videos\""
for i in $(seq 1 30); do
  A=$(curl -s -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$API/api/videos/$DEL_ID/description")
  C=$(curl -s -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$API/api/videos/$DEL_ID/comments")
  [[ "$A" == "404" && "$C" == "404" ]] && break
  sleep 1
done
[[ "$A" == "404" ]] && ok "description removed by the video.deleted event (~${i} s)" || fail "description is still there (HTTP $A)"
[[ "$C" == "404" ]] && ok "comments removed by the video.deleted event" || fail "comments are still there (HTTP $C)"

step "No token — 401, path traversal — 404, internal endpoints are not published"
[[ $(curl -s -o /dev/null -w '%{http_code}' "$API/api/videos/$VIDEO_ID") == 401 ]] && ok "401 without a token"
[[ $(curl -s -o /dev/null -w '%{http_code}' "$API/api/users/me") == 401 ]] && ok "401 on the profile without a token"
[[ $(curl -s -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$API/api/videos/$VIDEO_ID/stream/..%2Fsource") == 404 ]] && ok "404 on path traversal"
[[ $(curl -s -o /dev/null -w '%{http_code}' -X POST "${AUTH[@]}" -H 'Content-Type: application/json' -d '{}' "$API/internal/v1/events/video-processed") == 404 ]] && ok "404: /internal is not published through the gateway"

printf '\n\033[32mDemo passed.\033[0m Open http://localhost:3000/videos/%s\n' "$VIDEO_ID"
