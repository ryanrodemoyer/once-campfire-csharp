```
date: 2026-10-10T00:07:53-04:00
host: 7.0.0-38-generic, 11th Gen Intel(R) Core(TM) i9-11900K @ 3.50GHz, 16 threads, 125GB
server cpus: 8-11 (nproc 4); loadgen cpus: 12-15; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
csharp extra env: 
rust extra env: 
user agent: (none)
routes: up; suites: http; http: 8s at c=1 16 64; reps: 1
oracle: /home/rodey/Code/campfire-wt/B06/bench/results/harness-20261010/validation
seed: 6de159a86513e116 (production.sqlite3), 089726b88abf5f9b (labels.json)
loadgen: ae2139e98e4d3585, reference-rust ccece30
reference image: campfire-reference:app sha256:371ce7f6411746e911c69e8eadd838ee6623e35f150a0c06cf36450b0480919b 2026-10-09T09:33:37.111447744-04:00
csharp image: campfire-csharp:app sha256:81810a8e23be513ee10dc1e3b3c608722025207acfcd6c4c9e2c152b5823ebde 2026-10-10T00:00:17.928958703-04:00
csharp HEAD: d3b2628 (dirty: 0 files)
```

Reps: reference 1, csharp 1. Cells: median [min–max].

### HTTP workload (requests/sec), 16 concurrent clients

| HTTP workload (requests/sec) | Rails | C# |
|---|---:|---:|
| Health check (/up) | 4,122 | 13,340 |

### Startup and memory

| Metric | Rails | C# | C# adv. |
|---|---|---|---|
| cold start: docker run → /up answers (ms) | 477 | 214 | 2.2× |
| idle memory.current (MB) | 288 | 77.0 | 3.7× |
| idle anon (MB) | 268 | 64.0 | 4.2× |
| peak memory.current under load (MB) | 466 | 147 | 3.2× |
| peak anon under load (MB) | 444 | 123 | 3.6× |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

| Metric | Rails | C# | C# adv. |
|---|---|---|---|
| up c=1 req/s | 1,924 | 5,393 | 2.8× |
| up c=1 p50 ms | 0.48 | 0.17 | 2.9× |
| up c=1 p99 ms | 1.22 | 0.41 | 2.9× |
| up c=16 req/s | 4,122 | 13,340 | 3.2× |
| up c=16 p50 ms | 3.75 | 1.11 | 3.4× |
| up c=16 p99 ms | 7.54 | 2.85 | 2.6× |
| up c=64 req/s | 4,039 | 13,832 | 3.4× |
| up c=64 p50 ms | 15.4 | 4.38 | 3.5× |
| up c=64 p99 ms | 26.1 | 11.0 | 2.4× |

### HTTP errors / non-2xx-3xx (first rep, per app)

- Rails: none
- C#: none
