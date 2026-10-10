```
date: 2026-10-09T22:27:30-04:00
host: 7.0.0-38-generic, 11th Gen Intel(R) Core(TM) i9-11900K @ 3.50GHz, 16 threads, 125GB
server cpus: 8-11 (nproc 4); loadgen cpus: 12-15; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
csharp extra env: 
rust extra env: 
user agent: (none)
routes: room_show messages_page sidebar search post_message; suites: http; http: 8s at c=1 16 64; reps: 3
oracle: /home/rodey/Code/campfire-wt/B02/bench/results/first-20261009/validation
seed: 78173a5e814b6454 (production.sqlite3), 089726b88abf5f9b (labels.json)
loadgen: ae2139e98e4d3585, reference-rust ccece30
reference image: campfire-reference:app sha256:371ce7f6411746e911c69e8eadd838ee6623e35f150a0c06cf36450b0480919b 2026-10-09T09:33:37.111447744-04:00
csharp image: campfire-csharp:app sha256:532b5acf9fa20489c788fbc3a8479622138002947b047effc6c234f4039721ef 2026-10-09T22:12:00.304556378-04:00
csharp HEAD: 09090c8 (dirty: 1 files)
```

Reps: reference 3, csharp 3. Cells: median [min–max].

> **Post a message is not valid.** The C# image was built with `server-composition.patch`, which
> gives `campfire server` a `WebApp` but no-op broadcaster, job queue and connection revoker. A
> C# post skips the Turbo broadcast and job enqueue Rails does, so its numbers are not comparable.
> P05 (#173) wires the real server; B03 re-measures posting after it. The four GET workloads stand.

### HTTP workload (requests/sec), 16 concurrent clients

| HTTP workload (requests/sec) | Rails | C# |
|---|---:|---:|
| Room page | 195 | 217 |
| Messages page | 327 | 266 |
| Sidebar | 392 | 3,143 |
| Search | 319 | 602 |
| Post a message | 171 | 1,503 (not valid) |

### Startup and memory

| Metric | Rails | C# | C# adv. |
|---|---|---|---|
| cold start: docker run → /up answers (ms) | 462 [461–463] | 220 [194–220] | 2.1× |
| idle memory.current (MB) | 290 [288–295] | 43.0 [43.0–43.0] | 6.7× |
| idle anon (MB) | 270 [268–275] | 31.0 [31.0–31.0] | 8.7× |
| peak memory.current under load (MB) | 827 [813–849] | 184 [184–188] | 4.5× |
| peak anon under load (MB) | 759 [755–826] | 146 [144–158] | 5.2× |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

| Metric | Rails | C# | C# adv. |
|---|---|---|---|
| room_show c=1 req/s | 76.1 [72.7–77.9] | 64.6 [62.8–65.1] | 0.8× |
| room_show c=1 p50 ms | 12.8 [12.4–12.9] | 13.1 [12.9–13.2] | 1.0× |
| room_show c=1 p99 ms | 16.9 [16.5–24.2] | 34.6 [32.0–34.9] | 0.5× |
| room_show c=16 req/s | 195 [187–196] | 217 [209–218] | 1.1× |
| room_show c=16 p50 ms | 81.7 [79.1–87.3] | 72.9 [72.2–75.4] | 1.1× |
| room_show c=16 p99 ms | 171 [140–190] | 103 [101–114] | 1.7× |
| room_show c=64 req/s | 175 [172–187] | 217 [217–218] | 1.2× |
| room_show c=64 p50 ms | 364 [342–369] | 293 [290–294] | 1.2× |
| room_show c=64 p99 ms | 455 [431–473] | 331 [329–338] | 1.4× |
| messages_page c=1 req/s | 130 [126–132] | 97.5 [96.7–97.7] | 0.8× |
| messages_page c=1 p50 ms | 7.50 [7.32–7.82] | 9.86 [9.84–9.86] | 0.8× |
| messages_page c=1 p99 ms | 10.6 [10.5–11.2] | 16.6 [16.5–16.8] | 0.6× |
| messages_page c=16 req/s | 327 [315–333] | 266 [261–267] | 0.8× |
| messages_page c=16 p50 ms | 47.3 [40.3–53.3] | 59.2 [58.8–59.7] | 0.8× |
| messages_page c=16 p99 ms | 119 [103–139] | 87.5 [85.4–90.0] | 1.4× |
| messages_page c=64 req/s | 309 [298–310] | 264 [246–265] | 0.9× |
| messages_page c=64 p50 ms | 211 [205–214] | 241 [241–245] | 0.9× |
| messages_page c=64 p99 ms | 279 [261–280] | 269 [268–358] | 1.0× |
| sidebar c=1 req/s | 155 [153–162] | 1,050 [1,035–1,055] | 6.8× |
| sidebar c=1 p50 ms | 6.02 [5.92–6.32] | 0.92 [0.92–0.93] | 6.5× |
| sidebar c=1 p99 ms | 9.83 [9.20–14.70] | 1.31 [1.30–1.39] | 7.5× |
| sidebar c=16 req/s | 392 [380–395] | 3,143 [3,121–3,148] | 8.0× |
| sidebar c=16 p50 ms | 39.5 [37.7–39.6] | 4.91 [4.90–4.96] | 8.0× |
| sidebar c=16 p99 ms | 80.6 [65.9–86.1] | 9.93 [9.89–10.00] | 8.1× |
| sidebar c=64 req/s | 369 [348–395] | 3,131 [3,128–3,136] | 8.5× |
| sidebar c=64 p50 ms | 172 [162–178] | 20.0 [20.0–20.1] | 8.6× |
| sidebar c=64 p99 ms | 240 [215–260] | 30.1 [29.4–30.7] | 8.0× |
| search c=1 req/s | 129 [128–132] | 228 [228–229] | 1.8× |
| search c=1 p50 ms | 7.43 [7.32–7.61] | 4.15 [4.14–4.16] | 1.8× |
| search c=1 p99 ms | 11.4 [11.3–11.7] | 10.5 [10.5–10.5] | 1.1× |
| search c=16 req/s | 319 [318–327] | 602 [598–603] | 1.9× |
| search c=16 p50 ms | 48.6 [47.9–49.9] | 25.4 [25.4–25.6] | 1.9× |
| search c=16 p99 ms | 94.8 [93.0–105.0] | 45.4 [44.9–46.1] | 2.1× |
| search c=64 req/s | 303 [292–322] | 594 [588–597] | 2.0× |
| search c=64 p50 ms | 201 [201–215] | 107 [106–107] | 1.9× |
| search c=64 p99 ms | 291 [254–297] | 132 [130–151] | 2.2× |
| post_message c=1 req/s | 98.3 [95.8–98.9] | 777 [764–786] | 7.9× |
| post_message c=1 p50 ms | 9.39 [9.36–9.64] | 0.93 [0.93–0.93] | 10.1× |
| post_message c=1 p99 ms | 34.5 [32.8–39.2] | 20.6 [20.5–20.6] | 1.7× |
| post_message c=16 req/s | 171 [165–173] | 1,503 [1,492–1,537] | 8.8× |
| post_message c=16 p50 ms | 86.7 [80.9–88.5] | 5.42 [5.39–5.49] | 16.0× |
| post_message c=16 p99 ms | 254 [233–267] | 33.0 [32.1–36.4] | 7.7× |
| post_message c=64 req/s | 170 [167–172] | 1,591 [1,573–1,592] | 9.4× |
| post_message c=64 p50 ms | 363 [358–369] | 40.4 [40.3–40.9] | 9.0× |
| post_message c=64 p99 ms | 548 [534–603] | 65.6 [65.0–67.2] | 8.4× |

### HTTP errors / non-2xx-3xx (first rep, per app)

- Rails: none
- C#: none
