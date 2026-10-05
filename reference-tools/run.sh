#!/usr/bin/env bash
# Runs a reference-tools Ruby script with `bin/rails runner` against the reference app in
# production mode, with the fixed SECRET_KEY_BASE from parity/.env.reference.
#
#   reference-tools/run.sh reference-tools/rails_compat_vectors.rb
#
# Uses the `campfire-reference` Docker image (the oracle). With REFERENCE_NATIVE=1, or when Docker
# isn't reachable, it falls back to the local Ruby with the reference bundle installed into
# target/reference-bundle (same Gemfile.lock; see vectors/README.md).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$(realpath "$1")"; shift
SCRIPT_REL="${SCRIPT#"$ROOT"/}"
SECRET_KEY_BASE="${SECRET_KEY_BASE:-$(sed -n 's/^SECRET_KEY_BASE=//p' "$ROOT/parity/.env.reference" | head -1)}"
mkdir -p "$ROOT/target"

if [ -z "${REFERENCE_NATIVE:-}" ] && docker image inspect campfire-reference >/dev/null 2>&1; then
  exec docker run --rm -i --entrypoint "" --user "$(id -u):$(id -g)" \
    -e RAILS_ENV=production -e RAILS_LOG_LEVEL=fatal -e SECRET_KEY_BASE="$SECRET_KEY_BASE" -e DISABLE_SSL=1 \
    -e SKIP_TELEMETRY=true -e DATABASE_URL=sqlite3:/tmp/reference-tools.sqlite3 -e VECTORS_DIR=/work/vectors \
    -v "$ROOT/reference-tools:/work/reference-tools:ro" -v "$ROOT/vectors:/work/vectors" -v "$ROOT/target:/work/target" \
    campfire-reference bin/rails runner "/work/$SCRIPT_REL" "$@"
else
  export PATH="$HOME/.local/share/gem/ruby/3.4.0/bin:$PATH"
  export BUNDLE_PATH="$ROOT/target/reference-bundle" BUNDLE_WITHOUT="development:test"
  export RAILS_ENV=production RAILS_LOG_LEVEL=fatal SECRET_KEY_BASE DISABLE_SSL=1 SKIP_TELEMETRY=true VECTORS_DIR="$ROOT/vectors"
  mkdir -p "$ROOT/target/reference-db"
  rm -f "$ROOT/target/reference-db/reference-tools.sqlite3"
  export DATABASE_URL="sqlite3:$ROOT/target/reference-db/reference-tools.sqlite3"
  cd "$ROOT/reference"
  exec bin/rails runner "$SCRIPT" "$@" 2> >(grep -v VIPS-WARNING >&2)
fi
