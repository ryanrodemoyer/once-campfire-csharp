# Profiling the server under load

How the per-request cost of the C# server is measured and what it showed, so that the next
optimization starts from numbers. The tool is the
[dotnet-diagnostics MCP server](https://github.com/pedrosakuma/dotnet-diagnostics): it attaches to the
running process with no change to the app, and answers runtime counters, allocation samples and CPU
samples. Everything here was run on an Intel i9-10900 (20 threads) under Docker Desktop's Linux VM, the
app pinned to four CPUs and the load generator to four others. These are internal numbers, not the
B07 harness's: compare a change with the one before it measured the same way, not with
[`RESULTS.md`](RESULTS.md).

## What to measure

A change is judged on two things.

- **Bytes allocated per request.** The runtime counters `alloc-rate` divided by `requests-per-second`,
  read over 15 s of `room_show` at 16 clients after a warm-up. It is deterministic enough to see a
  3% change, which throughput is not: runs of the same image differ by 3 to 8%.
- **The validated A/B.** `bench/bin/run --apps csharp --routes room_show,messages_page,sidebar,search
  --suites http` with `HTTP_CONCS=16` and `CSHARP_EXTRA_ENV=CAMPFIRE_THRUSTER=0`, the two images
  alternating (A B B A, 3 reps each, so six samples a cell), every response checked against Rails's
  first. Six samples can't show a 2% change; they show whether a change is worse.

Warm the app first. For its first 10 to 25 seconds under load it serves at about half its steady rate
while the JIT tiers up, and any number taken then belongs to the JIT.

## Attaching the profiler

The runtime writes a diagnostic socket under `/tmp` of the app's container, so the profiler runs
beside the app: in the same PID namespace, sharing `/tmp`, as the same user. The topology is the
"anchor" recipe of dotnet-diagnostics's `docs/local-docker-sidecar.md`: an idle container owns the
PID namespace, so that the profiler survives the app crashing.

```sh
docker volume create diag-tmp
docker run -d --name diag-anchor --entrypoint tail campfire-csharp:app -f /dev/null

# The app: joins the anchor's PID namespace, shares /tmp, Kestrel on HTTP_PORT without Thruster.
# DOTNET_PerfMapEnabled=3 writes /tmp/perf-<pid>.map, which Linux perf needs to name managed frames.
docker run -d --name diag-app --pid=container:diag-anchor --network host --cpuset-cpus 8-11 --user 0:0 \
  --env-file parity/.env.reference -e HTTP_PORT=4390 -e CAMPFIRE_THRUSTER=0 -e RAILS_LOG_LEVEL=warn -e DOTNET_PerfMapEnabled=3 \
  -e WEB_CONCURRENCY=3 -e JOB_CONCURRENCY=3 -e RAILS_MAX_THREADS=5 \
  -v "$SEED/db:/rails/storage/db" -v "$SEED/storage:/rails/storage/files" -v diag-tmp:/tmp campfire-csharp:app

# The profiler: SYS_PTRACE for the live readers, PERFMON for Linux perf. MCP on 127.0.0.1:8787/mcp.
docker run -d --name diag-mcp --pid=container:diag-anchor --user 0 --cap-add SYS_PTRACE --cap-add PERFMON \
  -v diag-tmp:/tmp -e MCP_BEARER_TOKEN="$TOKEN" -e MCP_ALLOW_INSECURE_HTTP=true -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
  -p 127.0.0.1:8787:8080 ghcr.io/pedrosakuma/dotnet-diagnostics:latest
```

`$SEED` is a copy of the harness seed, with its Web Push and webhook endpoints pointed at a closed
local port as `bench/bin/run` does. Sign in with the load generator and send load to `HTTP_PORT`; leave
the load running while a sample is taken. A client of the MCP endpoint needs the bearer token and
`protocolVersion` `2025-11-25` (its HTTP transport keeps a session); `claude mcp add --transport http`
works, and so does a few lines of Python that POST JSON-RPC.

## The calls

| Question | Call |
|---|---|
| Which process? | `inspect_process {"view":"list"}`, then use the pid of `campfire` |
| Allocation per request | `collect_events {"kind":"counters","processId":PID,"durationSeconds":15}`; `alloc-rate` / `requests-per-second` |
| Who allocates | `collect_sample {"kind":"allocation","processId":PID,"durationSeconds":20}`; its `topBySite`, then `query_snapshot {"handle":H,"view":"call-tree","rootMethodFilter":"HandleAsync"}` |
| Where the CPU goes, managed only | `collect_sample {"kind":"cpu","processId":PID,"durationSeconds":20}` (EventPipe) |
| Where the CPU goes, native too | `collect_sample {"kind":"cpu","cpuBackend":"Os","processId":PID,"durationSeconds":20}` (Linux perf) |
| Rank what was sampled | `query_snapshot {"handle":H,"view":"top-methods","rankBy":"exclusive","topN":5000}`, or `"inclusive"` |

The default EventPipe CPU sample sees managed frames only. Threads waiting for work are most of its
samples, and native code (the GC, OpenSSL, the kernel) is a few unresolved addresses. The `Os` backend
samples what ran on a CPU and names native modules, which is why it is the one to use for a share of
CPU.

## What the changes did

Each row is one change, measured against `3fd196b` (the tip of `perf/request-hotspots`) with the same
image, rig and load. The three first cycles are independent of each other; stacked they are additive.

| Change | PR | Allocated per request | Room page | Messages | Sidebar | Search |
|---|---|---|---:|---:|---:|---:|
| `3fd196b` | | 51.2 KB | 18,994 | 29,353 | 31,456 | 29,738 |
| Cookie text without intermediate strings | #257 | 40.7 KB | +4.8% | +2.2% | +2.0% | −0.7% |
| Response cache budget read once, its key in one buffer | #259 | 47.1 KB | +11.4% | +13.4% | +9.6% | +11.2% |
| Cookie envelope without `JsonNode` | #258 | 44.9 KB | | | | |
| **Those three together** | | **30.4 KB** | **+12.6%** | **+9.2%** | **+10.7%** | **+11.3%** |
| Together, plus the request's host, port and base URL once, and the snapshot keys in one buffer | #260 | 29.1 KB | +9.6% | +11.1% | +5.0% | +3.7% |

#258's throughput wasn't measured on its own; its row is the allocation alone. The single-cycle throughput
columns come from their own A/Bs and are noisy: the #259 run's baseline
ranged 12.0 to 21.2k req/s on `room_show`, which is why it reads higher than the same image does against a
quieter baseline (+2 to +7%). The "together" row is the better measurement: six samples a cell, and
the Sidebar ranges don't overlap. The last row's throughput is against the three together, not against
`3fd196b`, pooled over two runs in opposite orders (twelve samples a cell); the profile attributes about
1.5% of the CPU to it, so read its size as "a few percent" and its direction as certain: all eight cells
were up in both orders. Its allocation figure is absolute.

## Where the CPU goes now

Linux perf on `room_show` at 16 clients on the three first changes stacked: 20 s, about 7,800 samples at
about 24k requests a second, the app's four CPUs at 95 to 99%. Self time by module:

| Share of samples | Where |
|---:|---|
| 24% | `System.*`, the managed BCL (strings, spans, date parsing and formatting, JSON, collections) |
| 19% | `Campfire.*`, the app's own code (`Campfire.Web` 13%, `Campfire.RailsCompat` 6%) |
| 19% | `[unknown]`: the kernel (sockets, `epoll`) and addresses perf couldn't name |
| 12% | the .NET runtime itself (`libcoreclr`: allocation, GC, type checks, stubs) |
| 9% | OpenSSL (`libcrypto`: the HMAC and AES-GCM of the cookies, SHA-256) |
| 8% | ASP.NET Core and Kestrel |
| 6% | `libc` and the loader |
| 4% | JIT stubs |

The profile is flat. The app's own code is a fifth of it and no function in it is above about 2% on its own.
Inclusive shares of the work a request does, from the same sample:

| Share | Work |
|---:|---|
| 60% | the whole request handler (the rest is Kestrel, the kernel and the GC) |
| 16% | authentication: the session cookie's decrypt and validation (`RestoreAuthentication`), session lookup |
| 15% | `CacheReadResponse` (`CacheableRead` 6.6%, `ResponseCacheKey` 4.9% before the request keys change) |
| 8% | sending the response (`ToSetCookieHeaders` 2.3%, `CookieSession.Commit` 2.8%) |
| 4% | `RackDeflater.SendAsync` |
| 3.0% | `RouteTable.Recognize` (the regular-expression module is another 1.1 to 1.5% on its own, shared by the route matches, `RemoteIp` and, before the request keys change, `RequestUrl`) |
| 2.4% | `StaticFiles.TryServe`: a lookup for a file on every request, before routing |
| 2.3% | `RubyBase64.StrictDecode`, the strict Base64 of the session cookie's message |
| 1.4 to 1.8% | `RequestUrl.BaseUrl` (0.7% after the request keys change) |
| 1.5% + 0.8% | `DateTimeParse` for the envelope's `exp`, and `DateTimeFormat` for `Set-Cookie`'s `expires` |
| 0.6% each | `IpNetwork.TryParse` and `RemoteIp.IpsFrom`: the trusted proxies parsed for each request |
| 1.0% | `RackDeflater.SelectBestEncoding` |

What they suggest, in order of size and of how little they risk:

1. **Routing.** `RouteTable.Recognize` walks every GET route that shares a literal prefix and runs a
   regular expression on each, in Rails's order. A necessary-condition prefilter (the number of path
   segments, a literal suffix) in front of the same expression keeps the order and skips most matches.
2. **A file lookup before routing.** `StaticFiles.TryServe` costs 2.4% of the CPU for requests that
   are never files.
3. **The session cookie's Base64 and dates.** A canonical-form decode with `Base64.DecodeFromUtf8`,
   and a fixed-format read of the `exp` in place of `DateTimeOffset.TryParseExact`; a cached `expires`
   string for the second.
4. **Trusted proxies.** Parsing the CIDR list once for the process, not once for each request.
5. **The cookie crypto itself** is the remaining large item (OpenSSL 9%, and the session lookup around
   it). Rails writes the session cookie on every response, so the work is part of the contract; what is
   left is fewer passes over the same bytes.

## Pitfalls

- A stale container holding the app's port makes the harness start its own app next to it and
  measure the wrong one. Check `docker ps -a` before an A/B.
- A host path under the Linux VM's own disk (`/benchroot` in these runs) is emptied when Docker Desktop
  restarts. A script that needs a file there has to write it each time.
- A Windows checkout carries CRLF into anything copied from it: build images from `git archive`
  (`git -c core.autocrlf=false archive`), never from the working tree.
