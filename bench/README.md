# Benchmarks

The C# port is measured with the Rust port's harness, so its numbers line up with the published
Rails, Go and Rust columns: the same seed (`parity/.seed/default`), the same load generator
(`reference-rust/bench/loadgen`), CPU pinning, alternating sequential runs and the same suites.
`bench/bin/run` is `reference-rust/bench/run` with a `csharp` app added and with every measured
response first validated against Rails.

**No numbers are published before B05** (AGENTS.md). Runs before then are internal.

## Running

On a Linux host with Docker (cgroup v2), Rust and at least 16 hardware threads:

```sh
git submodule update --init --recursive
parity/bin/seed build default && parity/bin/seed verify      # the recorded seed
reference-rust/parity/bin/reference build                       # campfire-reference:app
docker build -t campfire-csharp:app .                           # campfire-csharp:app
bench/bin/run --apps reference,csharp                           # everything, 3 reps
bench/bin/run --apps reference,csharp --up-only --reps 1        # the smallest whole run
bench/bin/report bench/results/<stamp>                          # re-print the tables
```

Add `rust` to `--apps` (with `RUST_IMAGE`, from `reference-rust/parity/bin/candidate build`) for a
third column measured on the same box.

Each rep runs each app in turn, never two at once, alternating the order between reps: a fresh
copy of the seed, a fresh container pinned to `SERVER_CPUS` (default 8-11, four threads per app),
then cold start, idle memory, the HTTP suite (1, 16 and 64 keep-alive connections), the Action
Cable fan-out suite (100, 500 and 1,000 clients) and the upload suite. The load generator runs on
`LOADGEN_CPUS` (default 12-15). Every knob is documented at the top of `bench/bin/run`.

Options:

- `--routes up,room_show,...` picks HTTP routes: `room_show messages_page sidebar search avatar
  static_css up post_message`.
- `--suites "http cable upload"` picks suites.
- `--up-only` is `--routes up --suites http` and skips sign-in.
- `--oracle DIR` validates against an earlier run's reference responses (in `DIR/validation/`) when
  the reference isn't first in `--apps`.

## Validation

Nothing is measured before it is shown to match the reference (`bench/lib/validate.py`):

| What | Check |
|---|---|
| GET routes | Fetched once before the warm-up. Status, content type and body must equal the reference's, with only the per-request CSRF tokens blanked. |
| Every measured response | Must have the validated status. No transport errors are allowed. |
| Posts | Every acknowledged post must be saved in the room and in the search index. |
| Cable | Every client must subscribe, and every message must reach every client in both phases. |
| Upload | Every upload's thumbnail must be served. |

The reference's responses are the oracle: the first reference rep captures them into
`validation/reference-<route>.json` (and `.body`). The first failure stops the run and writes the
reason to `failed.txt`. The app's rep that failed records no numbers. The checks for posts, cable
and uploads follow the Go port's harness (`once-campfire-go/bench/application`).

Body equality here is stricter than the Go harness's message-id contracts, but the replay gate (Q01)
is the full proof. B02 runs only once Q01 has passed for exactly the measured responses.

## Results

`bench/results/<stamp>/` holds:

- `env.txt`: host, CPUs, process model, image ids, seed, load generator and source hashes.
- `<app>-<rep>.json`: raw samples, with statuses, latency percentiles, cable delivery counts,
  upload timings and memory.
- `validation/`: every validated response.
- `uptime.log`: load around each run.
- `report.md`: from `bench/bin/report`. Its first table has the README's format (requests/sec at
  16 clients); the tables after it give median [min–max] across reps and the C# advantage over
  Rails.

Directories are named by task: `harness-*` (B06), `first-*` (B02), `runtime-*` (B04) and
`final-*` (B05).

## Tests

```sh
python3 bench/lib/test_bench.py
```
