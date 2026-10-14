#!/usr/bin/env bash
# Builds the production image (the root Dockerfile), runs it the way the image runs by default
# (bin/boot: Thruster in front of `campfire server`) on an empty storage directory, and drives it
# with smoke.mjs: /up, first run, a room page, a message over /cable and an uploaded image's
# thumbnail. Then it starts the image again with CAMPFIRE_THRUSTER=0 and checks that the app runs
# alone on HTTP_PORT. CI runs this in .github/workflows/image.yml.
#
#   tests/image-smoke/run.sh [IMAGE]
#
# The environment is the reference's test environment (parity/.env.reference), whose keys are
# test-only. The storage directory is a scratch copy, so nothing is written to the repository.
set -euo pipefail

cd "$(dirname "$0")/../.."

image="${1:-campfire-csharp:smoke}"
name="campfire-image-smoke-$$"
work=$(mktemp -d "${TMPDIR:-/tmp}/campfire-image-smoke.XXXXXX")

cleanup() {
  status=$?
  if [ "$status" -ne 0 ] && docker inspect "$name" >/dev/null 2>&1; then
    echo "== Container log" >&2
    docker logs "$name" >&2 2>&1 || true
  fi
  docker rm -f "$name" "$name-direct" >/dev/null 2>&1 || true
  # The container wrote its storage as uid 1000, which the host user may not be able to remove.
  docker run --rm --user 0 --entrypoint /bin/sh -v "$work:/scratch" "$image" \
    -c 'rm -rf /scratch/storage' >/dev/null 2>&1 || true
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

echo "== Building the production image: $image"
docker build -t "$image" .

echo "== Starting it on empty storage"
mkdir -p "$work/storage/db"
# The container runs as uid 1000; the scratch storage must be writable by it.
chmod -R a+rwX "$work"
docker run -d --name "$name" --env-file parity/.env.reference \
  -v "$work/storage:/rails/storage" -p 127.0.0.1::80 "$image" >/dev/null
port=$(docker port "$name" 80 | head -n 1 | sed 's/.*://')

for _ in $(seq 1 120); do
  curl -fsS "http://127.0.0.1:$port/up" >/dev/null 2>&1 && break
  sleep 1
done
curl -fsS "http://127.0.0.1:$port/up" >/dev/null

echo "== Checking bin/boot started Thruster and campfire server"
logs=$(docker logs "$name" 2>&1)
grep -qF '"msg":"Server started","http":":80"' <<<"$logs"
grep -qF 'Now listening on: http://0.0.0.0:3000' <<<"$logs"

echo "== Driving http://127.0.0.1:$port"
node tests/image-smoke/smoke.mjs "http://127.0.0.1:$port"

echo "== Checking CAMPFIRE_THRUSTER=0 runs campfire server alone on HTTP_PORT"
docker rm -f "$name" >/dev/null
docker run -d --name "$name-direct" --env-file parity/.env.reference -e CAMPFIRE_THRUSTER=0 \
  -v "$work/storage:/rails/storage" -p 127.0.0.1::80 "$image" >/dev/null
direct=$(docker port "$name-direct" 80 | head -n 1 | sed 's/.*://')
for _ in $(seq 1 120); do
  curl -fsS "http://127.0.0.1:$direct/up" >/dev/null 2>&1 && break
  sleep 1
done
curl -fsS "http://127.0.0.1:$direct/up" >/dev/null
logs=$(docker logs "$name-direct" 2>&1)
grep -qE 'Now listening on: http://0\.0\.0\.0:80$' <<<"$logs"
if grep -qF '"msg":"Server started"' <<<"$logs"; then
  echo "Thruster started although CAMPFIRE_THRUSTER=0" >&2
  exit 1
fi
