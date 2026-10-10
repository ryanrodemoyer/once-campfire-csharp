# Runtime configuration matrix

Results directory: `/home/rodey/Code/campfire-wt/B04/bench/results/runtime-20261010-032652`

## Environment

```
date: 2026-10-10T03:26:53-04:00
host: 7.0.0-38-generic, 11th Gen Intel(R) Core(TM) i9-11900K @ 3.50GHz, 16 threads, 125GB
server cpus: 8-11 (nproc 4); loadgen cpus: 12-15; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
csharp extra env: 
rust extra env: 
user agent: (none)
routes: room_show messages_page sidebar search up post_message; suites: http; http: 8s at c=1 16 64; reps: 2
oracle: /home/rodey/Code/campfire-wt/B04/bench/results/first-20261009/validation
seed: 3e8a0cf7b52f01bb (production.sqlite3), 089726b88abf5f9b (labels.json)
loadgen: ae2139e98e4d3585, reference-rust ccece30
csharp image: campfire-csharp:app sha256:e90bb97bbc7ed93c60f7c4300fd0ef5bdfc125c74306f3bac0e44ae2666f0350 2026-10-10T02:45:34.539498687-04:00
csharp HEAD: baf3bda (dirty: 0 files)
```

Reps: JIT (Dynamic PGO) / Server GC: 2 rep(s), JIT (Dynamic PGO) / Workstation GC: 2 rep(s), ReadyToRun / Server GC: 2 rep(s), ReadyToRun / Workstation GC: 2 rep(s). Cells: median [min–max].

## Startup and memory

| Metric | JIT (Dynamic PGO) / Server GC | JIT (Dynamic PGO) / Workstation GC | ReadyToRun / Server GC | ReadyToRun / Workstation GC |
|---|---:|---:|---:|---:|
| cold start (ms) | 202 [189–215] | 215 [213–217] | 202 [188–217] | 192 [191–193] |
| idle memory (MB) | 60 [59–60] | 78 [78–78] | 43 [43–43] | 42 [42–42] |
| idle anon (MB) | 46 [45–46] | 65 [65–65] | 31 [31–31] | 30 [30–30] |
| peak memory under load (MB) | 230 [227–234] | 208 [208–208] | 216 [208–224] | 170 [168–171] |
| peak anon under load (MB) | 202 [198–205] | 168 [165–170] | 192 [185–198] | 133 [127–139] |

## HTTP throughput (requests/sec), 16 concurrent clients

| Workload | JIT (Dynamic PGO) / Server GC | JIT (Dynamic PGO) / Workstation GC | ReadyToRun / Server GC | ReadyToRun / Workstation GC |
|---|---:|---:|---:|---:|
| Room page | 257 [253–260] | 217 [217–218] | 247 [246–248] | 209 [208–210] |
| Messages page | 309 [308–311] | 242 [230–255] | 313 [313–313] | 253 [253–253] |
| Sidebar | 3,136 [3,105–3,168] | 2,857 [2,842–2,872] | 3,161 [3,142–3,180] | 2,842 [2,831–2,854] |
| Search | 660 [657–662] | 566 [561–571] | 672 [670–674] | 576 [572–580] |
| Post a message | 1,436 [1,430–1,441] | 1,318 [1,303–1,333] | 1,399 [1,387–1,412] | 1,322 [1,311–1,333] |
| Health check (/up) | 11,085 [11,085–11,086] | 10,678 [10,664–10,692] | 10,959 [10,952–10,966] | 10,653 [10,616–10,691] |

## Recommendation

### Latency comparison (p99 at c=16)

| Workload | JIT (Dynamic PGO) / Server GC | JIT (Dynamic PGO) / Workstation GC | ReadyToRun / Server GC | ReadyToRun / Workstation GC |
|---|---:|---:|---:|---:|
| Room page | 89.3 ms [83.3–95.4] | 99.7 ms [98.1–101.4] | 95.9 ms [95.9–96.0] | 105.2 ms [105.0–105.3] |
| Messages page | 73.6 ms [72.7–74.6] | 89.6 ms [79.7–99.6] | 71.4 ms [71.0–71.7] | 92.8 ms [79.5–106.0] |
| Sidebar | 10.1 ms [9.7–10.4] | 10.4 ms [10.1–10.6] | 9.6 ms [9.4–9.8] | 10.5 ms [10.4–10.5] |
| Search | 43.7 ms [42.7–44.8] | 45.3 ms [42.9–47.7] | 41.2 ms [40.9–41.4] | 41.2 ms [39.8–42.5] |
| Post a message | 34.4 ms [33.8–35.0] | 36.2 ms [35.3–37.1] | 35.1 ms [34.7–35.4] | 36.3 ms [35.9–36.6] |
| Health check (/up) | 3.3 ms [3.2–3.3] | 3.4 ms [3.4–3.4] | 3.5 ms [3.4–3.5] | 3.4 ms [3.4–3.5] |

### GC mode comparison

Server GC is the default on multi-core machines. Workstation GC uses a single heap
and can reduce memory at the cost of throughput under high concurrency.

- **JIT (Dynamic PGO)**: Server GC score 16,883 → Workstation GC score 15,879 (0.94x, -5.9%)
- **ReadyToRun**: Server GC score 16,752 → Workstation GC score 15,855 (0.95x, -5.4%)

### Compilation mode comparison (both Server GC)

- JIT score 16,883 → ReadyToRun score 16,752 (0.99x, -0.8%)

### Cold start comparison

- **JIT (Dynamic PGO) / Server GC**: 202 ms
- **JIT (Dynamic PGO) / Workstation GC**: 215 ms
- **ReadyToRun / Server GC**: 202 ms
- **ReadyToRun / Workstation GC**: 192 ms

## Native AOT

Native AOT could not be built because `Campfire.Templates.Generator` targets `netstandard2.0`
(required for Roslyn source generators), which does not support AOT. The generator is only a
build-time dependency (`ReferenceOutputAssembly=false`, `OutputItemType=Analyzer`).

To enable Native AOT, the generator project needs `<IsAotCompatible>false</IsAotCompatible>`
in its `.csproj`. This is a one-line change that should be made in task F02 or P01.

Based on the .NET 10 documentation and general AOT characteristics, Native AOT is expected
to provide the best cold-start time and lowest idle memory, but may have lower peak throughput
than JIT with Dynamic PGO due to the lack of runtime profile-guided optimizations.
