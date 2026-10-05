#!/usr/bin/env bash
# Regenerates crates/richtext/tests/corpus/expected.json from inputs.yml by running the real
# Rails pipeline inside the campfire-reference image (docker build -t campfire-reference reference).
#
# RICHTEXT_FUZZ_CASES=5000 RICHTEXT_OUTPUT=big.json reference-tools/richtext/run.sh writes a larger
# corpus to crates/richtext/tests/corpus/big.json instead; point RICHTEXT_CORPUS at it to test.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"

docker run --rm \
  --env-file "$root/parity/.env.reference" \
  -e RAILS_LOG_LEVEL=error \
  -e DATABASE_URL=sqlite3:/tmp/richtext.sqlite3 \
  -e RICHTEXT_FUZZ_CASES="${RICHTEXT_FUZZ_CASES:-400}" \
  -e RICHTEXT_MUTATION_CASES="${RICHTEXT_MUTATION_CASES:-400}" \
  -e RICHTEXT_OUTPUT="/corpus/${RICHTEXT_OUTPUT:-expected.json}" \
  -v "$root/reference-tools/richtext:/tools:ro" \
  -v "$root/crates/richtext/tests/corpus:/corpus" \
  --user "$(id -u):$(id -g)" \
  campfire-reference \
  bash -c "bin/rails db:schema:load >/dev/null && bin/rails runner /tools/generate.rb"
