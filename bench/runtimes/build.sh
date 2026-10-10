#!/usr/bin/env bash
# Build the three runtime-variant Docker images.
#
#   bench/runtimes/build.sh [aot|r2r|jit|all]
#
# Images: campfire-csharp:jit (default, same as campfire-csharp:app),
#         campfire-csharp:r2r (ReadyToRun),
#         campfire-csharp:aot (Native AOT).
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
RUNTIMES=$ROOT/bench/runtimes
TARGET=${1:-all}

log() { echo "[$(date +%T)] $*" >&2; }

build_jit() {
  log "building campfire-csharp:jit (default JIT / Dynamic PGO)"
  docker build -t campfire-csharp:jit "$ROOT" 2>&1 | tail -5
  log "campfire-csharp:jit built"
}

build_r2r() {
  log "building campfire-csharp:r2r (ReadyToRun)"
  docker build -t campfire-csharp:r2r -f "$RUNTIMES/Dockerfile.r2r" "$ROOT" 2>&1 | tail -5
  log "campfire-csharp:r2r built"
}

build_aot() {
  log "building campfire-csharp:aot (Native AOT)"
  docker build -t campfire-csharp:aot -f "$RUNTIMES/Dockerfile.aot" "$ROOT" 2>&1 | tail -5
  log "campfire-csharp:aot built"
}

cd "$ROOT"

case "$TARGET" in
  all)
    build_jit
    build_r2r
    build_aot
    ;;
  jit)
    build_jit
    ;;
  r2r)
    build_r2r
    ;;
  aot)
    build_aot
    ;;
  *)
    echo "unknown target: $TARGET (use all, jit, r2r, or aot)" >&2
    exit 2
    ;;
esac

log "done"