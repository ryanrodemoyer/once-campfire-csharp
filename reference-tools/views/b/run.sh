#!/usr/bin/env bash
# Regenerates crates/views/tests/golden/b by running setup.rb and goldens.rb inside the
# campfire-reference image (built by `parity/bin/reference build`). The goldens were cut with
# their own SECRET_KEY_BASE, so it overrides the one in parity/.env.reference.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
OUT=${OUT:-$ROOT/crates/views/tests/golden/b}
SECRET_KEY_BASE=${SECRET_KEY_BASE:-views-b-secret-key-base-0123456789abcdef0123456789abcdef}
mkdir -p "$OUT"
docker run --rm --entrypoint "" \
  --cpus "${PARITY_CPUS:-2}" \
  --user "$(id -u):$(id -g)" \
  --env-file "$ROOT/parity/.env.reference" \
  -e SECRET_KEY_BASE="$SECRET_KEY_BASE" \
  -e DISABLE_DATABASE_ENVIRONMENT_CHECK=1 -e RAILS_LOG_LEVEL=error -e OUT=/out \
  -v "$ROOT/reference-tools:/tools:ro" -v "$OUT:/out" \
  campfire-reference:latest \
  sh -c 'bin/rails db:prepare >/dev/null && bin/rails db:fixtures:load &&
         bin/rails runner /tools/views/b/setup.rb && bin/rails runner /tools/views/b/goldens.rb' \
  2> >(grep -v -e VIPS -e '^$' >&2)
