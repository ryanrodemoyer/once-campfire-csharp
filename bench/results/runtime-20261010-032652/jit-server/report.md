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

Reps: csharp 2. Cells: median [min–max].

### HTTP workload (requests/sec), 16 concurrent clients

| HTTP workload (requests/sec) | C# |
|---|---:|
| Room page | 257 |
| Messages page | 309 |
| Sidebar | 3,136 |
| Search | 660 |
| Post a message | 1,436 |
| Health check (/up) | 11,085 |

### Startup and memory

| Metric | C# |
|---|---|
| cold start: docker run → /up answers (ms) | 202 [189–215] |
| idle memory.current (MB) | 59.5 [59.0–60.0] |
| idle anon (MB) | 45.5 [45.0–46.0] |
| peak memory.current under load (MB) | 230 [227–234] |
| peak anon under load (MB) | 202 [198–205] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

| Metric | C# |
|---|---|
| room_show c=1 req/s | 67.8 [67.6–68.1] |
| room_show c=1 p50 ms | 12.2 [12.2–12.2] |
| room_show c=1 p99 ms | 31.4 [30.7–32.0] |
| room_show c=16 req/s | 257 [253–260] |
| room_show c=16 p50 ms | 61.4 [60.8–62.0] |
| room_show c=16 p99 ms | 89.3 [83.3–95.4] |
| room_show c=64 req/s | 260 [260–261] |
| room_show c=64 p50 ms | 244 [243–244] |
| room_show c=64 p99 ms | 275 [273–277] |
| messages_page c=1 req/s | 98.7 [98.2–99.2] |
| messages_page c=1 p50 ms | 9.59 [9.57–9.62] |
| messages_page c=1 p99 ms | 15.3 [15.2–15.4] |
| messages_page c=16 req/s | 309 [308–311] |
| messages_page c=16 p50 ms | 50.7 [50.4–51.1] |
| messages_page c=16 p99 ms | 73.6 [72.7–74.6] |
| messages_page c=64 req/s | 309 [309–309] |
| messages_page c=64 p50 ms | 206 [205–206] |
| messages_page c=64 p99 ms | 242 [238–245] |
| sidebar c=1 req/s | 1,056 [1,054–1,059] |
| sidebar c=1 p50 ms | 0.92 [0.92–0.92] |
| sidebar c=1 p99 ms | 1.27 [1.26–1.27] |
| sidebar c=16 req/s | 3,136 [3,105–3,168] |
| sidebar c=16 p50 ms | 4.91 [4.90–4.92] |
| sidebar c=16 p99 ms | 10.1 [9.7–10.4] |
| sidebar c=64 req/s | 3,216 [3,183–3,249] |
| sidebar c=64 p50 ms | 19.7 [19.4–20.0] |
| sidebar c=64 p99 ms | 30.4 [28.6–32.3] |
| search c=1 req/s | 237 [236–237] |
| search c=1 p50 ms | 4.10 [4.09–4.11] |
| search c=1 p99 ms | 6.76 [6.22–7.29] |
| search c=16 req/s | 660 [657–662] |
| search c=16 p50 ms | 23.3 [23.1–23.5] |
| search c=16 p99 ms | 43.7 [42.7–44.8] |
| search c=64 req/s | 660 [659–662] |
| search c=64 p50 ms | 95.7 [95.2–96.3] |
| search c=64 p99 ms | 140 [117–163] |
| up c=1 req/s | 4,683 [4,663–4,704] |
| up c=1 p50 ms | 0.20 [0.20–0.20] |
| up c=1 p99 ms | 0.46 [0.44–0.47] |
| up c=16 req/s | 11,085 [11,085–11,086] |
| up c=16 p50 ms | 1.35 [1.35–1.35] |
| up c=16 p99 ms | 3.27 [3.24–3.31] |
| up c=64 req/s | 11,780 [11,670–11,890] |
| up c=64 p50 ms | 5.17 [5.14–5.21] |
| up c=64 p99 ms | 12.6 [12.3–12.9] |
| post_message c=1 req/s | 773 [766–780] |
| post_message c=1 p50 ms | 0.94 [0.94–0.94] |
| post_message c=1 p99 ms | 20.6 [20.5–20.6] |
| post_message c=16 req/s | 1,436 [1,430–1,441] |
| post_message c=16 p50 ms | 6.00 [5.98–6.01] |
| post_message c=16 p99 ms | 34.4 [33.8–35.0] |
| post_message c=64 req/s | 1,456 [1,434–1,478] |
| post_message c=64 p50 ms | 44.2 [43.8–44.7] |
| post_message c=64 p99 ms | 70.8 [69.0–72.6] |

### HTTP errors / non-2xx-3xx (first rep, per app)

- C#: none
