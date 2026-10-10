```
date: 2026-10-10T03:35:45-04:00
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
csharp image: campfire-csharp:app sha256:e90bb97bbc7ed93c60f7c4300fd0ef5bdfc125c74306f3bac0e44ae2666f0350 2026-10-10T02:45:34.539498687-04:00
csharp HEAD: baf3bda (dirty: 0 files)
```

Reps: csharp 2. Cells: median [min–max].

### HTTP workload (requests/sec), 16 concurrent clients

| HTTP workload (requests/sec) | C# |
|---|---:|
| Room page | 217 |
| Messages page | 242 |
| Sidebar | 2,857 |
| Search | 566 |
| Post a message | 1,318 |
| Health check (/up) | 10,678 |

### Startup and memory

| Metric | C# |
|---|---|
| cold start: docker run → /up answers (ms) | 215 [213–217] |
| idle memory.current (MB) | 78.0 [78.0–78.0] |
| idle anon (MB) | 65.0 [65.0–65.0] |
| peak memory.current under load (MB) | 208 [208–208] |
| peak anon under load (MB) | 168 [165–170] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

| Metric | C# |
|---|---|
| room_show c=1 req/s | 70.5 [67.6–73.4] |
| room_show c=1 p50 ms | 11.4 [11.1–11.8] |
| room_show c=1 p99 ms | 30.2 [29.3–31.1] |
| room_show c=16 req/s | 217 [217–218] |
| room_show c=16 p50 ms | 72.9 [72.8–73.0] |
| room_show c=16 p99 ms | 99.7 [98.1–101.4] |
| room_show c=64 req/s | 220 [220–220] |
| room_show c=64 p50 ms | 289 [289–290] |
| room_show c=64 p99 ms | 321 [317–325] |
| messages_page c=1 req/s | 102 [101–103] |
| messages_page c=1 p50 ms | 9.47 [9.42–9.51] |
| messages_page c=1 p99 ms | 14.2 [14.1–14.3] |
| messages_page c=16 req/s | 242 [230–255] |
| messages_page c=16 p50 ms | 65.0 [62.4–67.6] |
| messages_page c=16 p99 ms | 89.6 [79.7–99.6] |
| messages_page c=64 req/s | 252 [252–252] |
| messages_page c=64 p50 ms | 250 [249–252] |
| messages_page c=64 p99 ms | 381 [297–465] |
| sidebar c=1 req/s | 1,053 [1,052–1,055] |
| sidebar c=1 p50 ms | 0.90 [0.90–0.91] |
| sidebar c=1 p99 ms | 1.45 [1.43–1.47] |
| sidebar c=16 req/s | 2,857 [2,842–2,872] |
| sidebar c=16 p50 ms | 5.44 [5.43–5.45] |
| sidebar c=16 p99 ms | 10.4 [10.1–10.6] |
| sidebar c=64 req/s | 2,612 [2,409–2,814] |
| sidebar c=64 p50 ms | 24.5 [22.8–26.2] |
| sidebar c=64 p99 ms | 35.6 [35.0–36.2] |
| search c=1 req/s | 241 [240–242] |
| search c=1 p50 ms | 4.06 [4.06–4.07] |
| search c=1 p99 ms | 6.92 [6.79–7.05] |
| search c=16 req/s | 566 [561–571] |
| search c=16 p50 ms | 27.1 [26.2–28.0] |
| search c=16 p99 ms | 45.3 [42.9–47.7] |
| search c=64 req/s | 561 [559–563] |
| search c=64 p50 ms | 113 [112–114] |
| search c=64 p99 ms | 161 [130–192] |
| up c=1 req/s | 4,707 [4,689–4,725] |
| up c=1 p50 ms | 0.20 [0.20–0.20] |
| up c=1 p99 ms | 0.69 [0.66–0.72] |
| up c=16 req/s | 10,678 [10,664–10,692] |
| up c=16 p50 ms | 1.40 [1.40–1.40] |
| up c=16 p99 ms | 3.38 [3.38–3.39] |
| up c=64 req/s | 11,149 [11,105–11,194] |
| up c=64 p50 ms | 5.63 [5.60–5.66] |
| up c=64 p99 ms | 12.2 [12.2–12.2] |
| post_message c=1 req/s | 767 [762–773] |
| post_message c=1 p50 ms | 0.92 [0.92–0.92] |
| post_message c=1 p99 ms | 20.7 [20.6–20.7] |
| post_message c=16 req/s | 1,318 [1,303–1,333] |
| post_message c=16 p50 ms | 7.00 [6.91–7.09] |
| post_message c=16 p99 ms | 36.2 [35.3–37.1] |
| post_message c=64 req/s | 1,237 [1,230–1,245] |
| post_message c=64 p50 ms | 51.9 [51.7–52.1] |
| post_message c=64 p99 ms | 78.3 [76.0–80.6] |

### HTTP errors / non-2xx-3xx (first rep, per app)

- C#: none
