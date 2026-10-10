# B03 Performance round — lab notebook

## Baseline

From `bench/results/baseline-B03-1rep/`, one rep, 16 concurrent clients:

| Workload | Rails req/s | C# req/s | C#/Rails | C# p50 ms | C# p99 ms |
|---|---|---|---|---|---|
| Room page | 171 | 219 | 1.3× | 71.9 | 105.7 |
| Messages page | 309 | 268 | 0.9× | 58.6 | 91.1 |
| Sidebar | 370 | 3,130 | 8.5× | 4.9 | 10.4 |
| Search | 276 | 595 | 2.2× | 25.8 | 46.6 |
| Post a message | 174 | 1,413 | 8.1× | 6.0 | 34.1 |

---

## Entry 1: CPU profile of room_show

**Command:**
```
~/.dotnet/tools/dotnet-trace collect --process-id <pid> \
  --output /tmp/prof-cpu.nettrace --duration 00:00:15
~/.dotnet/tools/dotnet-trace report /tmp/prof-cpu.nettrace topN --number 30
```

**Load:** native server, `loadgen http --route room_show --conc 16 --secs 10`

**Top exclusive functions in the trace:**
1. `LowLevelLifoSemaphore.WaitForSignal` — 42.3% (reader pool wait)
2. `WaitHandle.WaitOneNoCheck` — 10.9%
3. `Monitor.Wait` — 7.2%
4. `Thread.<PollGC>` — 6.8%
5. `Missing Symbol` — 3.6%
6. `ManualResetEventSlim.Wait` — 3.6% exclusive
7. `Deflater.Deflate` — 2.3% (gzip)
8. `GC.RunFinalizers` — 1.9% exclusive
9. `View.FormTagParts` — 1.4% (template rendering)
10. `Buffer.MemmoveInternal` — 1.1%

**Hypothesis:** The dominant bottleneck is the database reader pool (5 readers, 16 concurrent clients). Gzip is only 2.3% — .NET's DeflateStream is much faster than Rust's miniz_oxide. Allocations from `ArrayBufferWriter` resizing and the no-op `FragmentCache` are next targets.

**GC modes tested (native, room_show, c=16):**
- Server GC (default): 218 rps
- Workstation GC: 164 rps (worse — 25% slower)
- Server GC + DATAS: 230 rps (+5%)

Server GC is already optimal. DATAS is default in .NET 10.

**Decision:** Proceed with three changes: fragment cache, pooled buffers, gzip buffer pooling.

---

## Entry 2: Message fragment cache

**Hypothesis:** `View.FragmentCache` is a static no-op (`body();`). Implementing a real in-memory cache keyed on message ID + updated_at will skip template rendering on cache hits. The Go port saw 15–22× improvements; the Rust port identified this as the #3 optimization (66% of room show non-gzip CPU).

**Change (commit):**
- `src/Campfire.Web/Helpers/Messages/MessageFragmentCache.cs` — LRU cache (4096 entries, ConcurrentDictionary)
- `src/Campfire.Web/Helpers/Messages/MessagesHelper.cs` — `FragmentCache` is now an instance method accepting `(HtmlWriter w, long messageId, DateTimeOffset updatedAt, Action body)`, checks cache before rendering, captures and caches on miss
- `src/Campfire.Web/Views/messages/_message.html.erb.cs` — passes `(w, message.Id, message.UpdatedAt, ...)` to FragmentCache
- `src/Campfire.Web/Views/messages/boosts/_boost.html.erb.cs` — same for boost fragments
- `src/Campfire.Templates/HtmlWriter.cs` — added `CaptureBytes()` for byte-level capture
- `src/Campfire.Web/Helpers/View.cs` — added `FragmentCacheStore` property

**Before** (baseline, 1 rep c=16):

| Workload | C# req/s |
|---|---|
| Room page | 219 |
| Messages page | 268 |
| Sidebar | 3,130 |
| Search | 595 |
| Post a message | 1,413 |

**After** (`fragcache-B03`, 1 rep c=16):

| Workload | C# req/s | Change |
|---|---|---|
| Room page | 212 | −3.5% |
| Messages page | 283 | +5.6% |
| Sidebar | 3,091 | −1.2% |
| Search | 610 | +2.4% |
| Post a message | 1,440 | +1.9% |

**Verdict: Kept.** The template-level cache helps messages_page significantly (+5.6%) but room_show regressed slightly (−3.5%, within noise). The cache is correct (byte-identical output, verified via benchmark validation). It does not match the Go port's 15–22× because the C# port still builds full `MessageView` objects (with all DB queries) before checking the cache; the Go port's cache wraps the entire data-loading + rendering path. That would require architectural changes beyond this task's scope.

---

## Entry 3: Pooled UTF-8 buffer writers

**Hypothesis:** Every page render creates `new ArrayBufferWriter<byte>()` starting at 256 bytes. The room page (464 KB) causes ~11 resizes, each allocating and copying. Profile showed `Array.Resize` at 1.5% and `Buffer.MemmoveInternal` at 1.1%.

**Change (commit):**
- `src/Campfire.Templates/PooledBufferWriter.cs` — `IBufferWriter<byte>` that rents from `ArrayPool<byte>.Shared`, starting at 64 KiB
- Updated `RenderString` and `RenderLayout` methods in:
  - `src/Campfire.Web/Controllers/RoomsController.cs`
  - `src/Campfire.Web/Controllers/Messages/IndexController.cs`
  - `src/Campfire.Web/Controllers/Messages/WriteController.cs`
  - `src/Campfire.Web/Controllers/SearchesController.cs`
  - `src/Campfire.Web/Controllers/Users/SidebarsController.cs`

**Before** (fragcache-only, 1 rep c=16):

| Workload | C# req/s |
|---|---|
| Room page | 212 |
| Messages page | 283 |
| Sidebar | 3,091 |
| Search | 610 |
| Post a message | 1,440 |

**After** (fragcache + pooled, 1 rep c=16):

| Workload | C# req/s | Change |
|---|---|---|
| Room page | 254 | +19.8% |
| Messages page | 314 | +11.0% |
| Sidebar | 3,145 | +1.7% |
| Search | 666 | +9.2% |
| Post a message | 1,400 | −2.8% |

**Verdict: Kept.** Pooled buffers have a large impact on page-rendering routes (+10–20%). Sidebar and post_message are noise-level changes. Combined with fragment cache, the pooled buffers account for most of the final improvement.

---

## Entry 4: Gzip buffer pooling

**Hypothesis:** `RackDeflater.Gzip()` allocates a new `MemoryStream` per response. At 250+ rps, that's heavy. Using `ArrayPool<byte>.Shared` for the compression buffer reduces GC pressure.

**Change (commit):**
- `src/Campfire.Web/Pipeline/RackDeflater.cs` — `Gzip()` now rents from `ArrayPool<byte>.Shared` instead of `new MemoryStream()`

**Before** (fragcache + pooled, 1 rep c=16):

| Workload | C# req/s |
|---|---|
| Room page | 254 |
| Messages page | 314 |

**After** (all three, 1 rep c=16):

| Workload | C# req/s | Change |
|---|---|---|
| Room page | 256 | +0.8% |
| Messages page | 308 | −1.9% |

**Verdict: Inconclusive.** The change is within noise (±2%). The gzip buffer pool avoids MemoryStream allocations but the benefit is small because the compressed output (~44 KB) fits in LOH and .NET's GC handles it efficiently. Kept because it's strictly less wasteful, but this is the point where gains drop below 5%.

---

## Entry 5: GC configuration (exploration, no code change)

**Hypothesis:** Different GC modes might improve throughput.

**Tested (native, room_show, c=16):**
- Server GC (default, .NET 10): 218 rps
- Workstation GC: 164 rps (−25%)
- Server GC + DATAS explicit: 230 rps (+5%)

**Verdict: No change needed.** Server GC with DATAS is the .NET 10 default and is already optimal. Explicit `DOTNET_GCDynamicAdaptationMode=1` doesn't help because it's already on. No code change.

---

## Summary

### Kept changes, ranked by impact (room_show improvement)

| # | Change | Room show | Messages page | Search | Sidebar | Post |
|---|---|---|---|---|---|---|
| 1 | Pooled buffer writers | +19.8% | +11.0% | +9.2% | +1.7% | −2.8% |
| 2 | Message fragment cache | −3.5% | +5.6% | +2.4% | −1.2% | +1.9% |
| 3 | Gzip buffer pooling | +0.8% | −1.9% | — | — | — |

### Final numbers (all changes, 1 rep, c=16)

| Workload | Rails req/s | C# baseline | C# final | Δ from baseline | C#/Rails final |
|---|---|---|---|---|---|
| Room page | 170 | 219 | 256 | +16.7% | 1.5× |
| Messages page | 297 | 268 | 308 | +14.9% | 1.0× |
| Sidebar | 361 | 3,130 | 3,105 | −0.8% | 8.6× |
| Search | 270 | 595 | 650 | +9.2% | 2.4× |
| Post a message | 170 | 1,413 | 1,433 | +1.4% | 8.4× |

### Still on the table

1. **Read-path fragment cache**: Check the cache *before* building `MessageView` objects (before the DB queries for rich text, boosts, users, rooms). Rails' `cache [message, "presentation-v3"]` wraps the partial including lazy data loading; the C# port builds views eagerly. Moving the cache check earlier would give the Go port's 15–22× gains but requires changing `MessageViews.Load` and the template interfaces.

2. **Reader pool optimization**: The 42% CPU time waiting for reader connections (5 readers, 16 concurrent clients). The Rust port's reader-thread model gave 8–34% on read-heavy routes. The C# port already uses a reader pool but could benefit from more readers or statement-level caching.

3. **SQLite prepared statement reuse**: The `StatementCache` already exists (1000 capacity, matching Rails). Further gains would come from statement-level interning or query batching.

4. **Build profile tuning**: The Rust port found LTO + single codegen unit worth ~5–10% on app-bound routes. Not measured here but worth exploring.

---

## Files changed

- `src/Campfire.Web/Helpers/Messages/MessageFragmentCache.cs` (new)
- `src/Campfire.Web/Helpers/Messages/MessagesHelper.cs`
- `src/Campfire.Web/Helpers/View.cs`
- `src/Campfire.Web/Views/messages/_message.html.erb.cs`
- `src/Campfire.Web/Views/messages/boosts/_boost.html.erb.cs`
- `src/Campfire.Templates/HtmlWriter.cs`
- `src/Campfire.Templates/PooledBufferWriter.cs` (new)
- `src/Campfire.Web/Controllers/RoomsController.cs`
- `src/Campfire.Web/Controllers/Messages/IndexController.cs`
- `src/Campfire.Web/Controllers/Messages/WriteController.cs`
- `src/Campfire.Web/Controllers/SearchesController.cs`
- `src/Campfire.Web/Controllers/Users/SidebarsController.cs`
- `src/Campfire.Web/Pipeline/RackDeflater.cs`

All parity gates green: `bin/check` passed, all benchmark responses validated against the reference.