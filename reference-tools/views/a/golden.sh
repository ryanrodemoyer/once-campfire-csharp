#!/bin/bash
# Regenerates crates/views/tests/golden/a by running render.rb inside the campfire-reference image.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
OUT=$ROOT/crates/views/tests/golden/a
mkdir -p "$OUT"
docker run --rm --entrypoint "" \
  --env-file "$ROOT/parity/.env.reference" \
  -e DISABLE_DATABASE_ENVIRONMENT_CHECK=1 \
  -v "$ROOT/reference-tools:/tools:ro" -v "$OUT:/out" \
  campfire-reference:latest \
  sh -c 'bin/rails db:prepare >/dev/null && bin/rails db:fixtures:load && bin/rails runner /tools/views/a/render.rb /out' \
  2> >(grep -v -e VIPS -e '^$' >&2)
