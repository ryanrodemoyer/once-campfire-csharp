#!/usr/bin/env bash
# Regenerates the campfire_storage golden vectors inside the reference image:
#
#   docker build -t campfire-reference reference
#   reference-tools/storage/run.sh            # → vectors/storage.json + vectors/storage/
#   reference-tools/storage/run.sh tables     # → crates/storage/src/tables.rs and
#                                             #   crates/rails_compat/src/content_disposition/approximations.rs
#
# The byte-identity vectors (variants, previews) depend on the image's libvips and ffmpeg; the
# versions are recorded in storage.json under "versions".
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
image="${IMAGE:-campfire-reference}"

if [[ "${1:-}" == "tables" ]]; then
  dump() {
    docker run --rm -v "$root/reference-tools/storage:/tools:ro" "$image" bundle exec ruby /tools/dump_tables.rb "$1"
  }
  dump marcel > "$root/crates/storage/src/tables.rs"
  dump approximations > "$root/crates/rails_compat/src/content_disposition/approximations.rs"
  exit
fi

rm -rf "$root/vectors/storage" "$root/vectors/storage.json"
mkdir -p "$root/vectors/storage"

docker run --rm --env-file "$root/parity/.env.reference" -e OUT=/out -e RAILS_LOG_LEVEL=error \
  -v "$root/reference-tools/storage:/tools:ro" -v "$root/vectors:/out" "$image" \
  bash -c "bin/rails db:prepare > /dev/null && bin/rails runner /tools/generate.rb"
