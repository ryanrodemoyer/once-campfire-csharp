```
date: 2026-10-10T03:45:37-04:00
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
csharp image: campfire-csharp:r2r sha256:529deae00cfc6d113c3de6ca3bc19e865403959e23019de09cd6b5643cb77179 2026-10-10T03:04:19.114651738-04:00
csharp HEAD: baf3bda (dirty: 0 files)
```

Reps: csharp 2. Cells: median [min–max].

### HTTP workload (requests/sec), 16 concurrent clients

| HTTP workload (requests/sec) | C# |
|---|---:|
| Room page | 247 |
| Messages page | 313 |
| Sidebar | 3,161 |
| Search | 672 |
| Post a message | 1,399 |
| Health check (/up) | 10,959 |

### Startup and memory

| Metric | C# |
|---|---|
| cold start: docker run → /up answers (ms) | 202 [188–217] |
| idle memory.current (MB) | 43.0 [43.0–43.0] |
| idle anon (MB) | 31.0 [31.0–31.0] |
| peak memory.current under load (MB) | 216 [208–224] |
| peak anon under load (MB) | 192 [185–198] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

| Metric | C# |
|---|---|
| room_show c=1 req/s | 48.1 [46.9–49.4] |
| room_show c=1 p50 ms | 22.4 [21.6–23.1] |
| room_show c=1 p99 ms | 32.7 [31.9–33.6] |
| room_show c=16 req/s | 247 [246–248] |
| room_show c=16 p50 ms | 63.8 [63.4–64.3] |
| room_show c=16 p99 ms | 95.9 [95.9–96.0] |
| room_show c=64 req/s | 261 [260–262] |
| room_show c=64 p50 ms | 243 [243–243] |
| room_show c=64 p99 ms | 276 [272–281] |
| messages_page c=1 req/s | 101 [101–101] |
| messages_page c=1 p50 ms | 9.43 [9.42–9.45] |
| messages_page c=1 p99 ms | 14.9 [14.8–15.0] |
| messages_page c=16 req/s | 313 [313–313] |
| messages_page c=16 p50 ms | 50.3 [50.3–50.3] |
| messages_page c=16 p99 ms | 71.4 [71.0–71.7] |
| messages_page c=64 req/s | 316 [315–318] |
| messages_page c=64 p50 ms | 201 [200–202] |
| messages_page c=64 p99 ms | 281 [228–333] |
| sidebar c=1 req/s | 1,054 [1,053–1,055] |
| sidebar c=1 p50 ms | 0.91 [0.91–0.92] |
| sidebar c=1 p99 ms | 1.32 [1.31–1.33] |
| sidebar c=16 req/s | 3,161 [3,142–3,180] |
| sidebar c=16 p50 ms | 4.90 [4.88–4.92] |
| sidebar c=16 p99 ms | 9.63 [9.43–9.83] |
| sidebar c=64 req/s | 3,252 [3,228–3,277] |
| sidebar c=64 p50 ms | 19.4 [19.3–19.6] |
| sidebar c=64 p99 ms | 29.9 [28.5–31.4] |
| search c=1 req/s | 241 [238–244] |
| search c=1 p50 ms | 4.00 [3.97–4.03] |
| search c=1 p99 ms | 8.81 [7.50–10.11] |
| search c=16 req/s | 672 [670–674] |
| search c=16 p50 ms | 23.1 [22.9–23.3] |
| search c=16 p99 ms | 41.2 [40.9–41.4] |
| search c=64 req/s | 671 [665–677] |
| search c=64 p50 ms | 94.5 [93.6–95.4] |
| search c=64 p99 ms | 117 [115–119] |
| up c=1 req/s | 4,707 [4,704–4,709] |
| up c=1 p50 ms | 0.20 [0.20–0.20] |
| up c=1 p99 ms | 0.42 [0.42–0.42] |
| up c=16 req/s | 10,959 [10,952–10,966] |
| up c=16 p50 ms | 1.36 [1.36–1.36] |
| up c=16 p99 ms | 3.45 [3.45–3.46] |
| up c=64 req/s | 11,809 [11,778–11,840] |
| up c=64 p50 ms | 5.17 [5.17–5.18] |
| up c=64 p99 ms | 12.6 [12.4–12.9] |
| post_message c=1 req/s | 784 [780–787] |
| post_message c=1 p50 ms | 0.93 [0.93–0.93] |
| post_message c=1 p99 ms | 20.6 [20.5–20.6] |
| post_message c=16 req/s | 1,399 [1,387–1,412] |
| post_message c=16 p50 ms | 6.10 [6.07–6.14] |
| post_message c=16 p99 ms | 35.1 [34.7–35.4] |
| post_message c=64 req/s | 1,459 [1,452–1,466] |
| post_message c=64 p50 ms | 44.0 [43.9–44.0] |
| post_message c=64 p99 ms | 72.0 [71.0–73.0] |

### HTTP errors / non-2xx-3xx (first rep, per app)

- C#: none
