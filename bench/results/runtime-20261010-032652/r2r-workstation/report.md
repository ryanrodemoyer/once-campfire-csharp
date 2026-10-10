```
date: 2026-10-10T03:54:28-04:00
host: 7.0.0-38-generic, 11th Gen Intel(R) Core(TM) i9-11900K @ 3.50GHz, 16 threads, 125GB
server cpus: 8-11 (nproc 4); loadgen cpus: 12-15; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
csharp extra env: DOTNET_gcServer=0
rust extra env: 
user agent: (none)
routes: room_show messages_page sidebar search up post_message; suites: http; http: 8s at c=1 16 64; reps: 2
oracle: /home/rodey/Code/campfire-wt/B04/bench/results/first-20261009/validation
seed: 3e8a0cf7b52f01bb (production.sqlite3), 089726b88abf5f9b (labels.json)
loadgen: ae2139e98e4d3585, reference-rust ccece30
csharp image: campfire-csharp:r2r sha256:529deae00cfc6d113c3de6ca3bc19e865403959e23019de09cd6b5643cb77179 2026-10-10T03:04:19.114651738-04:00
csharp HEAD: baf3bda (dirty: 0 files)
```

Reps: csharp 2. Cells: median [min–max].

### HTTP workload (requests/sec), 16 concurrent clients

| HTTP workload (requests/sec) | C# |
|---|---:|
| Room page | 209 |
| Messages page | 253 |
| Sidebar | 2,842 |
| Search | 576 |
| Post a message | 1,322 |
| Health check (/up) | 10,653 |

### Startup and memory

| Metric | C# |
|---|---|
| cold start: docker run → /up answers (ms) | 192 [191–193] |
| idle memory.current (MB) | 42.0 [42.0–42.0] |
| idle anon (MB) | 30.0 [30.0–30.0] |
| peak memory.current under load (MB) | 170 [168–171] |
| peak anon under load (MB) | 133 [127–139] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

| Metric | C# |
|---|---|
| room_show c=1 req/s | 55.0 [52.9–57.1] |
| room_show c=1 p50 ms | 20.8 [20.4–21.1] |
| room_show c=1 p99 ms | 29.5 [26.1–32.9] |
| room_show c=16 req/s | 209 [208–210] |
| room_show c=16 p50 ms | 75.4 [74.9–75.8] |
| room_show c=16 p99 ms | 105 [105–105] |
| room_show c=64 req/s | 218 [218–218] |
| room_show c=64 p50 ms | 293 [293–293] |
| room_show c=64 p99 ms | 322 [319–325] |
| messages_page c=1 req/s | 101 [101–101] |
| messages_page c=1 p50 ms | 9.45 [9.44–9.46] |
| messages_page c=1 p99 ms | 14.8 [14.7–14.9] |
| messages_page c=16 req/s | 253 [253–253] |
| messages_page c=16 p50 ms | 61.7 [60.5–63.0] |
| messages_page c=16 p99 ms | 92.8 [79.5–106.0] |
| messages_page c=64 req/s | 248 [243–253] |
| messages_page c=64 p50 ms | 252 [251–253] |
| messages_page c=64 p99 ms | 318 [292–344] |
| sidebar c=1 req/s | 1,051 [1,050–1,053] |
| sidebar c=1 p50 ms | 0.91 [0.90–0.91] |
| sidebar c=1 p99 ms | 1.47 [1.47–1.47] |
| sidebar c=16 req/s | 2,842 [2,831–2,854] |
| sidebar c=16 p50 ms | 5.46 [5.44–5.47] |
| sidebar c=16 p99 ms | 10.5 [10.4–10.5] |
| sidebar c=64 req/s | 2,420 [2,417–2,422] |
| sidebar c=64 p50 ms | 26.1 [26.0–26.1] |
| sidebar c=64 p99 ms | 35.2 [34.6–35.9] |
| search c=1 req/s | 243 [241–245] |
| search c=1 p50 ms | 4.05 [4.03–4.08] |
| search c=1 p99 ms | 6.87 [6.72–7.01] |
| search c=16 req/s | 576 [572–580] |
| search c=16 p50 ms | 27.3 [27.2–27.5] |
| search c=16 p99 ms | 41.2 [39.8–42.5] |
| search c=64 req/s | 560 [556–564] |
| search c=64 p50 ms | 114 [113–114] |
| search c=64 p99 ms | 131 [129–133] |
| up c=1 req/s | 4,717 [4,677–4,757] |
| up c=1 p50 ms | 0.20 [0.20–0.20] |
| up c=1 p99 ms | 0.60 [0.58–0.63] |
| up c=16 req/s | 10,653 [10,616–10,691] |
| up c=16 p50 ms | 1.41 [1.40–1.41] |
| up c=16 p99 ms | 3.44 [3.41–3.48] |
| up c=64 req/s | 11,141 [11,069–11,212] |
| up c=64 p50 ms | 5.63 [5.60–5.67] |
| up c=64 p99 ms | 12.4 [12.2–12.5] |
| post_message c=1 req/s | 765 [762–768] |
| post_message c=1 p50 ms | 0.92 [0.92–0.93] |
| post_message c=1 p99 ms | 20.7 [20.6–20.7] |
| post_message c=16 req/s | 1,322 [1,311–1,333] |
| post_message c=16 p50 ms | 6.96 [6.87–7.05] |
| post_message c=16 p99 ms | 36.3 [35.9–36.6] |
| post_message c=64 req/s | 1,239 [1,238–1,240] |
| post_message c=64 p50 ms | 51.6 [51.5–51.8] |
| post_message c=64 p99 ms | 77.1 [76.0–78.3] |

### HTTP errors / non-2xx-3xx (first rep, per app)

- C#: none
