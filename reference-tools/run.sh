#!/usr/bin/env bash
# Runs a reference-tools Ruby script with `bin/rails runner` against the reference app in
# production mode, with the fixed environment in parity/.env.reference, through
# parity/bin/reference (the campfire-reference Docker image, or the native runtime without Docker).
#
#   reference-tools/run.sh reference-tools/rails_compat_vectors.rb
#
# The script writes into $VECTORS_DIR: vectors/ by default, or VECTORS_DIR=<dir inside the repo>.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REFERENCE="$ROOT/parity/bin/reference"
VECTORS_DIR="${VECTORS_DIR:-$ROOT/vectors}"

exec "$REFERENCE" runner -e RAILS_LOG_LEVEL=fatal -e "VECTORS_DIR=$("$REFERENCE" path "$VECTORS_DIR")" "$@"
