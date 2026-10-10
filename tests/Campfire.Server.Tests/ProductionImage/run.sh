#!/usr/bin/env bash
# Builds the production image (the root Dockerfile), runs it on a fresh copy of the Rails fixtures
# with the reference's environment (parity/.env.reference), uploads an image and a video through
# it, and checks their thumbnail and poster are the reference's bytes (ProductionImageMediaTests).
#
#   tests/Campfire.Server.Tests/ProductionImage/run.sh [IMAGE] [-- extra dotnet test arguments]
#
# The test skips without CAMPFIRE_PRODUCTION_URL, which this script sets. The container's storage
# is a scratch copy, so the database is never written to the repository.
set -euo pipefail

cd "$(dirname "$0")/../../.."

image="campfire-csharp:production-media"
if [ $# -gt 0 ] && [ "$1" != "--" ]; then
  image="$1"
  shift
fi
if [ "${1:-}" = "--" ]; then
  shift
fi

[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

name="campfire-production-media-$$"
work=$(mktemp -d "${TMPDIR:-/tmp}/campfire-production-media.XXXXXX")

cleanup() {
  status=$?
  if [ "$status" -ne 0 ] && docker inspect "$name" >/dev/null 2>&1; then
    echo "== Container log" >&2
    docker logs "$name" >&2 2>&1 || true
  fi
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

echo "== Building the production image: $image"
docker build -t "$image" .

echo "== Starting it on a fresh copy of the Rails fixtures"
mkdir -p "$work/storage/db"
cp tests/Campfire.Data.Tests/Oracle/rails-fixtures.sqlite3 "$work/storage/db/production.sqlite3"
# The container runs as uid 1000; the scratch copy must be writable by it.
chmod -R a+rwX "$work"
docker run -d --name "$name" --env-file parity/.env.reference \
  -v "$work/storage:/rails/storage" -p 127.0.0.1::3000 "$image" >/dev/null
port=$(docker port "$name" 3000 | head -n 1 | sed 's/.*://')

for _ in $(seq 1 120); do
  curl -fsS "http://127.0.0.1:$port/up" >/dev/null 2>&1 && break
  sleep 1
done
curl -fsS "http://127.0.0.1:$port/up" >/dev/null

echo "== Uploading an image and a video through http://127.0.0.1:$port"
CAMPFIRE_PRODUCTION_URL="http://127.0.0.1:$port" dotnet test tests/Campfire.Server.Tests \
  -c Release --nologo --filter "FullyQualifiedName~Campfire.Server.Tests.ProductionImage" "$@"
