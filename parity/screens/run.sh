#!/usr/bin/env bash
# Screen capture runtime for the C# port. Sourced by parity/screens/compare.
# Runs the Rust port's capture engine (reference-rust/parity/capture/) inside the pinned Playwright
# image, but also mounts the C# project root so the capture engine can read our seed data.
#
# PARITY_CAPTURE_RUNTIME:
#   docker   docker build + docker run (the canonical runtime)
#   host     the host's Node and Playwright browsers; for developing the harness only, NOT canonical
# Default: docker when the daemon is reachable, else host.
set -euo pipefail

CAMPFIRE_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
REFERENCE_RUST=$CAMPFIRE_ROOT/reference-rust
CAPTURE_PARITY=$REFERENCE_RUST/parity

die() { echo "screens: $*" >&2; exit 1; }

capture_runtime() {
  if [ -n "${PARITY_CAPTURE_RUNTIME:-}" ]; then echo "$PARITY_CAPTURE_RUNTIME"
  elif docker info >/dev/null 2>&1; then echo docker
  else echo host
  fi
}

docker_image() {
  local hash; hash=$(cat "$CAPTURE_PARITY/Dockerfile.playwright" "$CAPTURE_PARITY/package-lock.json" | sha256sum | cut -c1-12)
  local image=campfire-parity-playwright:$hash
  if ! docker image inspect "$image" >/dev/null 2>&1; then
    echo "screens: building $image" >&2
    docker build -q -f "$CAPTURE_PARITY/Dockerfile.playwright" -t "$image" "$CAPTURE_PARITY" >&2
  fi
  echo "$image"
}

# run_in_image ARGS... runs `node capture/cli.ts ARGS...` in the capture parity directory.
# Mounts both the C# project root (for seed data) and the reference-rust root (for the capture engine).
run_in_image() {
  local runtime; runtime=$(capture_runtime)
  local net_dir; net_dir=$(mktemp -d "$CAMPFIRE_ROOT/parity/out/.net.XXXXXX")
  NET_DIRS+=("$net_dir")
  local socket=$net_dir/upstream.sock
  case "$runtime" in
    docker)
      local image; image=$(docker_image)
      local name=parity-capture-$$-$RANDOM
      CAPTURE_CONTAINERS+=("$name" "$name-forward")
      docker run -d --rm --init --name "$name-forward" --network host -u "$(id -u):$(id -g)" \
        -v "$REFERENCE_RUST:$REFERENCE_RUST" -v "$CAMPFIRE_ROOT:$CAMPFIRE_ROOT" \
        --tmpfs "$CAPTURE_PARITY/node_modules" -w "$CAPTURE_PARITY" \
        "$image" node capture/forward.ts "$socket" >/dev/null
      wait_for_socket "$socket"
      docker run --rm --init --name "$name" --network none --ipc host \
        -u "$(id -u):$(id -g)" -e HOME=/tmp -e TZ=UTC -e CI="${CI:-}" -e PARITY_WORKERS="${PARITY_WORKERS:-}" \
        -e PARITY_UPSTREAM_SOCKET="$socket" \
        -v "$REFERENCE_RUST:$REFERENCE_RUST" -v "$CAMPFIRE_ROOT:$CAMPFIRE_ROOT" \
        --tmpfs "$CAPTURE_PARITY/node_modules" -w "$CAPTURE_PARITY" \
        "$image" node capture/cli.ts "$@" &
      local status=0
      wait $! || status=$?
      docker kill "$name-forward" >/dev/null 2>&1 || true
      rm -rf "$net_dir"
      return $status
      ;;
    host)
      echo "screens: WARNING running on the host; captures are not canonical" >&2
      (cd "$CAPTURE_PARITY" && node capture/cli.ts "$@")
      ;;
    *) die "unknown PARITY_CAPTURE_RUNTIME=$runtime" ;;
  esac
}

wait_for_socket() {
  for _ in $(seq 1 100); do [ -S "$1" ] && return 0; sleep 0.1; done
  die "the upstream forwarder did not start ($1)"
}

# Resetting servers for captures of `mutates: true` states.
start_reset_loop() {
  local reset_cmd=$1
  RESET_CTRL=$(mktemp -d "$CAMPFIRE_ROOT/parity/out/.control.XXXXXX")
  (
    serve() {
      local id=$1 port target url cmd status
      read -r port target url <"$RESET_CTRL/work.$id"
      cmd=${reset_cmd//\{port\}/$port}; cmd=${cmd//\{target\}/$target}; cmd=${cmd//\{url\}/$url}
      if sh -c "$cmd" >&2; then status=ok; else status=failed; fi
      echo "$status" >"$RESET_CTRL/.done.$id" && mv "$RESET_CTRL/.done.$id" "$RESET_CTRL/done.$id"
      rm -f "$RESET_CTRL/work.$id"
    }
    while [ -d "$RESET_CTRL" ]; do
      for req in "$RESET_CTRL"/req.*; do
        [ -e "$req" ] || continue
        id=${req##*/req.}
        mv "$req" "$RESET_CTRL/work.$id" 2>/dev/null || continue
        serve "$id" &
      done
      sleep 0.2
    done
    wait
  ) &
  RESET_LOOP_PID=$!
  RESET_ARG="$CAPTURE_PARITY/capture/sandbox/reset-request.sh $RESET_CTRL {port} {target} {url}"
}

CAPTURE_CONTAINERS=()
NET_DIRS=()

parity_cleanup() {
  stop_reset_loop
  local name
  for name in "${CAPTURE_CONTAINERS[@]+"${CAPTURE_CONTAINERS[@]}"}"; do docker kill "$name" >/dev/null 2>&1 || true; done
  local dir
  for dir in "${NET_DIRS[@]+"${NET_DIRS[@]}"}"; do rm -rf "$dir"; done
}

stop_reset_loop() {
  [ -n "${RESET_CTRL:-}" ] && rm -rf "$RESET_CTRL"
  [ -n "${RESET_LOOP_PID:-}" ] && wait "$RESET_LOOP_PID" 2>/dev/null
  RESET_CTRL="" RESET_LOOP_PID=""
}