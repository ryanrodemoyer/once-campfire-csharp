# Published benchmark

The headline is the C#/Rails ratio measured in the same session on this machine.
It is not a comparison with numbers published from any other machine.

On the normal profile (16 clients, three alternating rounds, eight-second samples),
C# serves **2.27× to 3.45×** as many requests per second as Rails on the four HTML
reads, and **0.20×** as many when posting a message. Avatar and static CSS match
Rails (0.99× to 1.03×). Health checks are about **2.8×**. With a writer capped at
10 messages per second in another room, the read ratios are **3.76× to 5.81×**,
because Rails' cached reads slow down more than C#'s.

Session 2 was a second pair of runs from scratch (fresh containers and fresh copies
of the same seed). Every summary-table ratio agreed with session 1 within the
run-to-run band defined below. Raw samples, provenance and load are in
[`results/verify-session-1-normal`](results/verify-session-1-normal),
[`results/verify-session-1-mixed`](results/verify-session-1-mixed),
[`results/verify-session-2-normal`](results/verify-session-2-normal) and
[`results/verify-session-2-mixed`](results/verify-session-2-mixed).

Request rates and p99 latencies are the median of the three rounds. The ratio is
the C# median divided by the Rails median. p99 is `latency.p99_ms` from the harness.

## Normal profile

`read-and-post-v1`. Routes, in harness order: room page, messages page, sidebar,
search, avatar, static CSS, `/up`, post a message. Both sessions: 9,025,381 and
8,974,182 validated responses, 96 raw samples each (warmup and timed), 0 errors,
0 invalid responses.

### Session 1 (2026-10-13 05:52:41Z)

| Workload | Rails req/s | Rails p99 ms | C# req/s | C# p99 ms | C#/Rails |
|---|---:|---:|---:|---:|---:|
| Room page | 2762.4 | 14.935 | 6279.5 | 6.579 | 2.273 |
| Messages page | 2572.7 | 19.311 | 8569.0 | 4.339 | 3.331 |
| Sidebar | 2877.9 | 16.975 | 9228.9 | 3.991 | 3.207 |
| Search | 3055.9 | 13.711 | 8837.3 | 4.103 | 2.892 |
| Avatar | 53040.3 | 1.345 | 52864.5 | 1.361 | 0.997 |
| Static CSS | 75126.5 | 0.873 | 75130.3 | 0.894 | 1.000 |
| Health (`/up`) | 4210.4 | 9.559 | 11630.7 | 3.063 | 2.762 |
| Post a message | 249.1 | 181.375 | 50.0 | 332.287 | 0.201 |

### Session 2 (2026-10-13 06:09:06Z)

| Workload | Rails req/s | Rails p99 ms | C# req/s | C# p99 ms | C#/Rails |
|---|---:|---:|---:|---:|---:|
| Room page | 2741.2 | 14.631 | 6213.5 | 6.543 | 2.267 |
| Messages page | 2497.8 | 20.879 | 8621.9 | 4.335 | 3.452 |
| Sidebar | 2843.3 | 17.087 | 9191.6 | 4.005 | 3.233 |
| Search | 2923.3 | 14.583 | 8898.3 | 4.031 | 3.044 |
| Avatar | 52705.7 | 1.364 | 51945.4 | 1.419 | 0.986 |
| Static CSS | 73831.6 | 0.899 | 75795.9 | 0.877 | 1.027 |
| Health (`/up`) | 4163.4 | 9.823 | 11711.4 | 2.991 | 2.813 |
| Post a message | 248.5 | 183.295 | 50.4 | 330.495 | 0.203 |

## Mixed-write profile

`mixed-read-write-v1`. Sixteen read clients on the four HTML routes, plus one
writer posting at most 10 messages per second to a different room, with no
catch-up. The harness keeps the writer out of this table. Both sessions: 1,080,311
and 1,083,634 validated responses (reads, warmups and writer), 48 raw samples each,
0 errors, 0 invalid responses.

### Session 1 (2026-10-13 06:02:53Z)

| Workload | Rails req/s | Rails p99 ms | C# req/s | C# p99 ms | C#/Rails |
|---|---:|---:|---:|---:|---:|
| Room page | 957.8 | 255.231 | 4753.5 | 14.607 | 4.963 |
| Messages page | 1356.2 | 99.199 | 7801.5 | 5.967 | 5.752 |
| Sidebar | 2377.7 | 30.895 | 8937.4 | 4.191 | 3.759 |
| Search | 1943.8 | 52.767 | 8590.6 | 4.431 | 4.419 |

### Session 2 (2026-10-13 06:19:18Z)

| Workload | Rails req/s | Rails p99 ms | C# req/s | C# p99 ms | C#/Rails |
|---|---:|---:|---:|---:|---:|
| Room page | 1037.1 | 205.311 | 4743.3 | 13.519 | 4.574 |
| Messages page | 1354.9 | 101.951 | 7867.1 | 5.667 | 5.806 |
| Sidebar | 2307.7 | 32.895 | 9071.4 | 4.179 | 3.931 |
| Search | 1957.4 | 52.159 | 8625.9 | 4.487 | 4.407 |

## Run-to-run agreement

The harness publishes each round and the min–max of those rounds
(`docs/performance-review.md` in the pinned checkout). It does not publish a
single allowed percentage. For this run, a workload's within-run spread is
`(max − min) / median` of its three request rates. A ratio moves when either app
moves, so the band for one session is the sum of the two apps' spreads. The
sessions agree when

`|ratio₂ − ratio₁| / max(ratio₁, ratio₂)`

is no larger than the greater of the two sessions' bands. Per-round rates are in
each directory's `rails-N.json` and `csharp-N.json`. `bench/verification/summarize.py`
prints the medians and the per-app spreads.

| Workload | Profile | Session ratios | Relative gap | Larger single-app spread | Ratio band | Agrees |
|---|---|---:|---:|---:|---:|---|
| Room page | normal | 2.273, 2.267 | 0.29% | 4.08% | 6.39% | yes |
| Messages page | normal | 3.331, 3.452 | 3.51% | 10.89% | 12.05% | yes |
| Sidebar | normal | 3.207, 3.233 | 0.80% | 8.53% | 10.15% | yes |
| Search | normal | 2.892, 3.044 | 4.99% | 6.36% | 6.93% | yes |
| Avatar | normal | 0.997, 0.986 | 1.11% | 4.11% | 5.87% | yes |
| Static CSS | normal | 1.000, 1.027 | 2.59% | 2.08% | 3.67% | yes |
| Health (`/up`) | normal | 2.762, 2.813 | 1.80% | 1.56% | 2.37% | yes |
| Post a message | normal | 0.201, 0.203 | 1.03% | 2.40% | 3.08% | yes |
| Room page | mixed | 4.963, 4.574 | 7.84% | 13.96% | 26.21% | yes |
| Messages page | mixed | 5.752, 5.806 | 0.93% | 12.96% | 14.36% | yes |
| Sidebar | mixed | 3.759, 3.931 | 4.38% | 6.87% | 8.98% | yes |
| Search | mixed | 4.419, 4.407 | 0.29% | 9.72% | 14.17% | yes |

Static CSS and `/up` sit just outside the larger *single-app* spread (2.59% against
2.08%, and 1.80% against 1.56%). Their rates are flat across rounds, so that
stricter band is about the size of the noise. Both pass the ratio band above.
The nine workloads in the harness summary tables also pass the stricter single-app test.

## Hardware

Recorded in each directory's `provenance.txt` and `loadavg.log`.

- Host `thoru`, Ubuntu 24.04, Linux 7.0.0-38-generic, x86_64.
- Intel Core i9-11900K (8 cores, 16 threads), max 5300 MHz, min 800 MHz. The
  scaling governor was `powersave` on every logical CPU. The governor file is
  owned by root and was left as found. Just before the first session the current
  frequency on CPU 0 was 5100 MHz.
- 131,705,748 kB RAM (~125.6 GiB). About 117 GiB was available when session 1's
  provenance was written. Swap is 8 GiB.
- App containers: `--cpuset-cpus 8-11`, `--network host`. Load generator:
  `taskset -c 12-15`. The Ruby orchestrator ran in `campfire-b07-runner` on
  CPUs `0-7,12-15`, so it was off the app's logical CPUs.
- Thread siblings: CPU 8 shares a core with 0, 9 with 1, 10 with 2, 11 with 3,
  and 12–15 share cores with 4–7. The orchestrator is allowed onto 0–3, the
  siblings of the app CPUs. Both apps had that placement.
- One-minute load at the start of session 1 was 0.80. Later sessions waited until
  it fell below 2.0 (90 seconds in each case; the bench itself keeps the 5-minute
  load near 5). Peak one-minute load during a session was 9.43, 7.37, 8.00 and
  7.78, in session order.

## Pins

| | |
|---|---|
| Verification checkout | `ec02debb3e44f40b8778a6a42db3c4515b2f9eaf` ([basecamp/once-campfire-verification](https://github.com/basecamp/once-campfire-verification)) |
| C# patch | `bench/verification/csharp.patch`, sha256 `4b88655e8584cc323fe646434fe80f708ea71dc6d8c6316040d9f3b70588a9b5`. Applied to a disposable worktree. The checkout is unmodified. |
| Rails source | `reference/` at `edbc779f4dfc9b26c36310881711ddc976a9dfc8`, clean |
| Rails image | `campfire-reference:app-edbc779`, `sha256:5fde961b71d4fa6da3757ae28972b7047a80c7bfffc07aef4cba8993695f02ee`, `APP_VERSION=parity`, `GIT_REVISION=edbc779f4dfc9b26c36310881711ddc976a9dfc8` |
| C# source during the runs | `df6039cc01ff53a6152433c63fb9e27481968a0f`, clean. `src/`, `tests/` and `Dockerfile` match `31e411275c5f1fa29883791a6f17fa8481248ad6` (the Q11 commit the image was built from). The commits in between only add this harness. |
| C# image | `campfire-csharp:b07`, `sha256:d3ec4d7203b512a50d221d7bb137b2676efb2b6e8e6898fa7bfb0b1701da94f1`, `GIT_REVISION=31e411275c5f1fa29883791a6f17fa8481248ad6`, `APP_VERSION=parity` |
| C# binary | `/app/campfire` sha256 `8ea5248282079734382061e05ed45323ce391b85761142a3fb50122c5959cd89` |
| Load generator | `bench/verification/checkout/loadgen/target/release/loadgen`, sha256 `6d46faf6bd409a28705678193422e0a3d69afb1c14fdafce03b6584cb8b17be1`, built with rustc 1.98.1 and `cargo build --release --locked` |
| Seed database | sha256 `86a0a5ab3520ac7b1a4845a579863a1e90be90a18d6313700ed2dfcb877ce950`. Built by the harness `bin/seed` from public Rails `90b330024dec3e757c79b6a7e6568f93da8e3148` (image `once-campfire-verification:reference-90b3300`), then `bin/rails db:prepare` in the edbc779 image. Both measured apps booted that migrated file. |
| Toolchain | .NET SDK 10.0.112, Docker server 29.8.2, runner Ruby 3.4.11, runner SQLite 3.46.1, runner Git 2.47.3, host ffprobe 6.1.1 |
| Orchestrator image | `campfire-b07-runner` `sha256:b814657cbe4cddcb37acea6d95693c2dea1d7a240c7c9629f21b0de0485f9e61` |

Topology the harness recorded for every session: Rails, 3 HTTP workers and 5 threads, Redis/Resque, Action Cable, 64 MB response cache. C#, one process, 5 SQLite readers, 3 in-process job workers, Kestrel WebSockets, 64 MB response cache. Both images start through Thruster (`bin/boot`). The harness sets `WEB_CONCURRENCY=3`, `JOB_CONCURRENCY=3`, `RAILS_MAX_THREADS=5` and `CAMPFIRE_RESPONSE_CACHE_MB` defaults to 64 for both.

## How to reproduce

From this repo, on a Linux host with Docker, Rust 1.98.1, a C# SDK and at least 16 hardware threads. Nothing else should be using the machine. The host has no Ruby or `sqlite3` CLI; `bench/verification/run` supplies them in a container that is kept off CPUs 8–11.

```sh
git submodule update --init --recursive
docker build -t campfire-csharp:b07 --build-arg APP_VERSION=parity --build-arg GIT_REVISION="$(git rev-parse HEAD)" .
# campfire-reference:app-edbc779 must already exist and report GIT_REVISION=edbc779f4dfc9b26c36310881711ddc976a9dfc8
bench/verification/run prepare
bench/verification/run sessions
python3 bench/verification/summarize.py bench/results/verify-session-1-normal
```

`prepare` builds `campfire-b07-runner`, builds the load generator, builds the
90b3300 reference image on the host (BuildKit), runs `bin/seed` into `tmp/b07-seed`,
and migrates that database with the edbc779 image. Credentials stay in
`tmp/b07-seed/reference.env` and are not committed. `sessions` runs normal, then
mixed, then both again, and refuses to overwrite an existing output directory.
Details are in [`verification/README.md`](verification/README.md).

## Limitations

- These are HTTP throughput ratios on one desktop, with 16 keep-alive clients.
  They are not concurrent-user counts and they do not measure WebSocket fan-out.
- The absolute request rates are specific to this i9-11900K and to the
  `powersave` governor. The published verification tables were measured on
  different hardware. Use the ratios in this file.
- The mixed profile measures cached reads while another room is being written,
  at a cap of 10 posts per second. It is not a chat-capacity test. The harness
  says the same thing.
- Rails and C# do not use the same process layout. Rails runs 3 Puma workers.
  Its Resque pool size comes from `reference/config/resque-pool.yml`
  (`(Concurrent.processor_count * 0.5).ceil`) and ignores `JOB_CONCURRENCY`.
  C# is one Kestrel process; `JOB_CONCURRENCY=3` is in-process
  (`src/Campfire.Server/Cli/ServerSettings.cs`).
- The only listed behavioral difference from Rails is the writer WAL checkpoint,
  described in the [Known differences](../README.md#known-differences) section of
  the README. Rails checkpoints from a background contender. C# also runs a
  PASSIVE checkpoint after a commit once the WAL passes 4 MiB. That shows up
  under sustained posts. The gate is
  `WAL_size_stays_bounded_under_a_sustained_posting_run_and_stop_leaves_the_database_consistent`
  in `tests/Campfire.Data.Tests/Checkpoint/SqliteWalCheckpointTests.cs`
  ([`plans/status/U08.md`](../plans/status/U08.md)). The post ratio above (0.20×)
  includes that difference and the process-layout difference. This run did not
  separate them.
- The seed was generated by Rails `90b3300`, which is what `bin/seed` pins, and
  then migrated with `edbc779` so the file contains
  `20261004225000`, `20261005022000` and `20261007190000`. C# refuses to boot a
  database with pending migrations. Rails would apply those migrations itself on
  `db:prepare`. Both apps were measured on the already-migrated file.
- Route contracts (`route-contract-v1`) check status, headers, the decoded body
  against the app's own preflight, message ids against the fixture SQL, and
  persisted posts. They are not a byte-for-byte comparison of C# HTML with Rails
  HTML. Parity against Rails is the Q11 gate, not this harness.
- The orchestrator is a container, and logical CPUs 0–3 are the sibling threads
  of the app CPUs. A host Ruby process pinned away from those siblings could
  measure a slightly different absolute rate. The ratio is from one placement,
  shared by both apps, in each session.
