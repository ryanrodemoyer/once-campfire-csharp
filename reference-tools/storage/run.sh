#!/usr/bin/env bash
# Regenerates the storage golden vectors (storage.json and storage/) in the reference app:
#
#   reference-tools/storage/run.sh            # → tmp/vectors/storage*, checked against vectors/
#   reference-tools/storage/run.sh tables     # the Rust port's Marcel and Content-Disposition tables,
#                                             #   on stdout (dump_tables.rb marcel, then approximations)
#
# The byte-identity vectors (variants, previews) depend on the image's libvips and ffmpeg; the
# versions are recorded in storage.json under "versions".
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"

if [[ "${1:-}" == "tables" ]]; then
  script=$("$root/parity/bin/reference" path "$root/reference-tools/storage/dump_tables.rb")
  for table in marcel approximations; do
    "$root/parity/bin/reference" exec -- bundle exec ruby "$script" "$table"
  done
  exit
fi

exec "$root/reference-tools/regenerate" storage
