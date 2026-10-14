# Every port on one machine

Rails, C# and six other Campfire ports, measured in one run of the shared harness
([basecamp/once-campfire-verification](https://github.com/basecamp/once-campfire-verification)
`ec02deb`, through [`verification/run`](verification/run)) on one machine. Each app gets the same
seed, the same four CPUs and the same route contracts. The harness runs one app at a time and
alternates the order between rounds.

Machine: Intel i9-11900K (8 cores, 16 threads), 128 GB, Linux 7.0. Each app runs on CPUs 8–11 and
the load generator on 12–15. There are 16 keep-alive clients with gzip, and three rounds of eight
seconds per route. Each cell is the median of the three rounds, in requests per second.

Every measured response was checked against the harness's route contracts: 78,270,252 in the
normal run and 25,978,859 in the mixed run, with 0 errors and 0 invalid responses. Raw samples,
provenance and load are in [`results/ports-20261014-normal`](results/ports-20261014-normal) and
[`results/ports-20261014-mixed`](results/ports-20261014-mixed).

## Normal profile

Each route is measured on its own. Requests per second (higher is better):

| Workload | Rails | C# | Rust | Go | Express | Django | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Room page | 2,808 | 33,542 | 45,956 | 28,940 | 21,289 | 404 | 2,851 | 77,872 |
| Messages page | 2,556 | 50,982 | 45,077 | 29,693 | 33,052 | 409 | 2,884 | 81,568 |
| Sidebar | 2,906 | 54,976 | 51,916 | 31,416 | 41,430 | 499 | 3,139 | 83,450 |
| Search | 3,054 | 54,214 | 51,768 | 31,744 | 36,682 | 498 | 3,091 | 86,301 |
| Post a message | 257 | 1,336 | 3,952 | 1,630 | 1,580 | 58 | 485 | 288 |

p99 latency in ms (lower is better):

| Workload | Rails | C# | Rust | Go | Express | Django | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Room page | 14.18 | 2.14 | 0.66 | 2.34 | 2.66 | 48.09 | 12.48 | 0.55 |
| Messages page | 19.17 | 0.85 | 0.68 | 2.28 | 1.31 | 46.98 | 12.33 | 0.50 |
| Sidebar | 16.94 | 0.75 | 0.63 | 2.22 | 0.99 | 38.91 | 11.04 | 0.40 |
| Search | 14.05 | 0.69 | 0.63 | 2.17 | 1.15 | 37.76 | 11.81 | 0.44 |
| Post a message | 171.01 | 43.30 | 36.16 | 41.44 | 53.34 | 3,067.90 | 84.22 | 129.47 |

## Mixed-write profile

The four pages are read by 16 clients while one writer posts at most 10 messages a second to
another room, which keeps emptying the response caches. Requests per second:

| Workload | Rails | C# | Rust | Go | Express | Django | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Room page | 1,024 | 20,677 | 43,190 | 26,547 | 18,010 | 46 | 1,334 | 68,266 |
| Messages page | 1,271 | 46,163 | 42,473 | 27,634 | 30,037 | 49 | 1,436 | 74,694 |
| Sidebar | 2,436 | 53,223 | 51,308 | 33,614 | 39,577 | 189 | 2,990 | 82,827 |
| Search | 2,041 | 51,350 | 49,602 | 32,367 | 33,772 | 91 | 2,392 | 83,302 |

p99 latency in ms:

| Workload | Rails | C# | Rust | Go | Express | Django | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Room page | 216.70 | 4.84 | 0.73 | 3.00 | 5.88 | 418.81 | 72.06 | 0.52 |
| Messages page | 101.38 | 1.99 | 0.75 | 2.81 | 3.46 | 400.64 | 66.62 | 0.54 |
| Sidebar | 29.60 | 1.02 | 0.66 | 2.03 | 1.14 | 117.25 | 13.86 | 0.42 |
| Search | 51.23 | 0.97 | 0.70 | 2.25 | 2.21 | 216.96 | 29.86 | 0.47 |

## What was measured

| App | Commit | How it serves HTTP |
|---|---|---|
| Rails | `edbc779` (this repo's `reference/`) | Thruster in front of Puma: 3 workers, 5 threads |
| C# | `3fd196b` (branch `perf/request-hotspots`) | Kestrel alone (`CAMPFIRE_THRUSTER=0`), 5 readers |
| Rust | `6dae2fd` | in-process front, 5 readers |
| Go | `c4ab53d` | in-process front |
| Express | `bb70a7d` | cluster, workers from the cpuset |
| Django | `ac50dac` | Uvicorn, 1 worker |
| Laravel | `4093bf2` | FrankenPHP/Octane |
| C | `873a51f` | in-process, up to 4 event loops |

The ports are each upstream repository's latest commit as of 2026-10-14, pinned in
[`verification/ports.tsv`](verification/ports.tsv) and built by `verification/run ports`.

- **C#** is the image built from `perf/request-hotspots` at `3fd196b`, which isn't on `main` yet.
  The ports serve HTTP from their own process, so C# was measured without Thruster as well. As
  shipped, the C# image runs Thruster, which caps these routes far lower (see
  [`RESULTS.md`](RESULTS.md)).
- **Django** got one Uvicorn worker. The harness gives Django more workers only when `REDIS_URL`
  is set, and this seed doesn't set it.
- **C** was built in a Fedora container, the way its `bench/run-c` builds it on a Fedora host,
  without Fil-C.
- **Elixir** isn't included. Its `Dockerfile.dev` pins `libvips-dev=8.16.1-1+deb13u1`, which
  Debian has since replaced with `deb13u2`, so the image no longer builds.
- The seed is the harness's, built by Rails `90b3300` and migrated to `edbc779`, so C# can boot
  it. Every app booted that same file.

## Reproducing

```sh
bench/verification/run prepare
bench/verification/run ports
docker build -t campfire-csharp:perf8 .        # from the C# commit being measured
git worktree add --detach tmp/csharp-3fd196b 3fd196b
export APPS=rails,csharp,rust,go,express,django,laravel,c \
  CSHARP_IMAGE=campfire-csharp:perf8 CSHARP_SOURCE=$PWD/tmp/csharp-3fd196b \
  CSHARP_BENCH_ENV='{"CAMPFIRE_THRUSTER":"0"}'
bench/verification/run bench normal "$PWD/bench/results/ports-20261014-normal"
bench/verification/run bench mixed "$PWD/bench/results/ports-20261014-mixed"
python3 bench/verification/summarize.py bench/results/ports-20261014-normal
```

The normal run took about 34 minutes and the mixed run about 18.
