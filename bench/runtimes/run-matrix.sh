#!/usr/bin/env bash
# Run the runtime configuration matrix: JIT vs ReadyToRun, Server GC vs Workstation GC.
# Uses the B02 oracle for validation so only the C# variants need to run.
#
#   bench/runtimes/run-matrix.sh [--up-only] [--reps 3]
#
# Output: bench/results/runtime-<stamp>/
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
BENCH_RUN=$ROOT/bench/bin/run
ORACLE=$ROOT/bench/results/first-20261009
STAMP=$(date +%Y%m%d-%H%M%S)
OUT_BASE=$ROOT/bench/results/runtime-$STAMP

REPS=3
UP_ONLY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --up-only) UP_ONLY="--up-only"; shift ;;
    --reps) REPS=$2; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

log() { echo "[$(date +%T)] $*" >&2; }

# Configuration matrix.
# Format: "label|image|gc_mode"
# gc_mode: "server" (default) or "workstation" (DOTNET_gcServer=0)
CONFIGS=(
  "jit-server|campfire-csharp:app|server"
  "jit-workstation|campfire-csharp:app|workstation"
  "r2r-server|campfire-csharp:r2r|server"
  "r2r-workstation|campfire-csharp:r2r|workstation"
)

log "Runtime matrix starting at $STAMP"
log "Configurations: ${#CONFIGS[@]}"
log "Oracle: $ORACLE"

for cfg in "${CONFIGS[@]}"; do
  IFS='|' read -r label image gc <<< "$cfg"

  out="$OUT_BASE/$label"
  mkdir -p "$out"

  extra=""
  if [ "$gc" = "workstation" ]; then
    extra="DOTNET_gcServer=0"
  fi

  log "=== $label: image=$image gc=$gc ==="

  # Only routes with oracle validation in B02: room_show messages_page sidebar search up post_message
  routes="room_show,messages_page,sidebar,search,up,post_message"
  if [ -n "$UP_ONLY" ]; then
    routes="up"
  fi

  CSHARP_IMAGE="$image" \
  CSHARP_EXTRA_ENV="$extra" \
  "$BENCH_RUN" \
    --apps csharp \
    --oracle "$ORACLE" \
    --reps "$REPS" \
    --routes "$routes" \
    --suites http \
    --out "$out" \
    2>&1 | sed "s/^/[$label] /"

  log "$label done -> $out"
done

log "All configurations done. Results in $OUT_BASE"
echo "$OUT_BASE"