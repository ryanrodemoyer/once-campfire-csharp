#!/usr/bin/env python3
"""Compare runtime configurations from a bench/runtimes/run-matrix.sh output.

Reads the four subdirectories of a runtime-* results dir and prints a
comparison table for the README-style workloads plus startup and memory.

  python3 bench/runtimes/analyze.py bench/results/runtime-20261010-032652
"""
import glob, json, os, statistics, sys

CONFIGS = {
    "jit-server":      ("JIT (Dynamic PGO)", "Server GC"),
    "jit-workstation":  ("JIT (Dynamic PGO)", "Workstation GC"),
    "r2r-server":       ("ReadyToRun", "Server GC"),
    "r2r-workstation":  ("ReadyToRun", "Workstation GC"),
}

README_CONC = 16
ROUTES = ["room_show", "messages_page", "sidebar", "search", "post_message", "up"]
ROUTE_LABELS = {
    "room_show": "Room page",
    "messages_page": "Messages page",
    "sidebar": "Sidebar",
    "search": "Search",
    "post_message": "Post a message",
    "up": "Health check (/up)",
}


def main():
    result_dir = sys.argv[1]
    config_data = {}

    for key, (compiler, gc) in CONFIGS.items():
        path = os.path.join(result_dir, key)
        if not os.path.isdir(path):
            print(f"# warning: no results for {key}", file=sys.stderr)
            continue
        runs = []
        for f in sorted(glob.glob(os.path.join(path, "*.json"))):
            runs.append(json.load(open(f)))
        if runs:
            config_data[key] = {"label": f"{compiler} / {gc}", "runs": runs}

    if not config_data:
        sys.exit(f"no results found in {result_dir}")

    ordered = [k for k in CONFIGS if k in config_data]
    labels = {k: config_data[k]["label"] for k in ordered}

    print("# Runtime configuration matrix\n")
    print(f"Results directory: `{result_dir}`\n")

    # Read env from first config
    env_file = os.path.join(result_dir, ordered[0], "env.txt")
    if os.path.exists(env_file):
        env = open(env_file).read().strip()
    else:
        env = "(env.txt not found)"
    print("## Environment\n")
    print("```\n" + env + "\n```\n")

    # Summary
    n_reps = {k: len(config_data[k]["runs"]) for k in ordered}
    rep_text = ", ".join(f"{labels[k]}: {n_reps[k]} rep(s)" for k in ordered)
    print(f"Reps: {rep_text}. Cells: median [min–max].\n")

    # Startup and memory
    print("## Startup and memory\n")
    print("| Metric | " + " | ".join(labels[k] for k in ordered) + " |")
    print("|---|" + "---:|" * len(ordered))

    for metric, key, unit in [
        ("cold start (ms)", "cold_start_ms", "ms"),
        ("idle memory (MB)", lambda r: r["memory"]["idle_current_mb"], "MB"),
        ("idle anon (MB)", lambda r: r["memory"]["idle_anon_mb"], "MB"),
        ("peak memory under load (MB)", lambda r: r["memory"]["peak_current_mb"], "MB"),
        ("peak anon under load (MB)", lambda r: r["memory"]["peak_anon_mb"], "MB"),
    ]:
        cells = []
        for k in ordered:
            getter = key if callable(key) else (lambda r, k=key: r[k])
            vals = [getter(r) for r in config_data[k]["runs"]]
            if not vals:
                cells.append("–")
            else:
                m = statistics.median(vals)
                if len(vals) > 1:
                    cells.append(f"{m:,.0f} [{min(vals):,.0f}–{max(vals):,.0f}]")
                else:
                    cells.append(f"{m:,.0f}")
        print(f"| {metric} | " + " | ".join(cells) + " |")

    # Throughput table
    print(f"\n## HTTP throughput (requests/sec), {README_CONC} concurrent clients\n")
    print("| Workload | " + " | ".join(labels[k] for k in ordered) + " |")
    print("|---|" + "---:|" * len(ordered))

    for route in ROUTES:
        label = ROUTE_LABELS.get(route, route)
        cells = []
        for k in ordered:
            vals = []
            for r in config_data[k]["runs"]:
                for h in r.get("http", []):
                    if h.get("route") == route and h.get("conc") == README_CONC:
                        if h.get("ok", 0) > 0:
                            vals.append(h["rps"])
            if vals:
                m = statistics.median(vals)
                if len(vals) > 1:
                    cells.append(f"{m:,.0f} [{min(vals):,.0f}–{max(vals):,.0f}]")
                else:
                    cells.append(f"{m:,.0f}")
            else:
                cells.append("–")
        print(f"| {label} | " + " | ".join(cells) + " |")

    # Find the best configuration
    print(f"\n## Recommendation\n")
    best = None
    best_score = 0
    scores = {}

    for k in ordered:
        score = 0
        for route in ROUTES:
            vals = []
            for r in config_data[k]["runs"]:
                for h in r.get("http", []):
                    if h.get("route") == route and h.get("conc") == README_CONC:
                        if h.get("ok", 0) > 0:
                            vals.append(h["rps"])
            if vals:
                score += statistics.median(vals)
        scores[k] = score
        if score > best_score:
            best_score = score
            best = k

    # Look at p99 latency for room_show at c=16
    print("### Latency comparison (p99 at c=16)\n")
    print("| Workload | " + " | ".join(labels[k] for k in ordered) + " |")
    print("|---|" + "---:|" * len(ordered))
    for route in ROUTES:
        label = ROUTE_LABELS.get(route, route)
        cells = []
        for k in ordered:
            vals = []
            for r in config_data[k]["runs"]:
                for h in r.get("http", []):
                    if h.get("route") == route and h.get("conc") == README_CONC:
                        p99 = h.get("latency", {}).get("p99_ms")
                        if p99 is not None:
                            vals.append(p99)
            if vals:
                m = statistics.median(vals)
                if len(vals) > 1:
                    cells.append(f"{m:.1f} ms [{min(vals):.1f}–{max(vals):.1f}]")
                else:
                    cells.append(f"{m:.1f} ms")
            else:
                cells.append("–")
        print(f"| {label} | " + " | ".join(cells) + " |")

    # GC comparison
    print(f"\n### GC mode comparison\n")
    print("Server GC is the default on multi-core machines. Workstation GC uses a single heap")
    print("and can reduce memory at the cost of throughput under high concurrency.\n")

    gc_pairs = [
        ("jit-server", "jit-workstation", "JIT (Dynamic PGO)"),
        ("r2r-server", "r2r-workstation", "ReadyToRun"),
    ]
    for server_key, ws_key, label in gc_pairs:
        if server_key not in config_data or ws_key not in config_data:
            continue
        server_score = scores.get(server_key, 0)
        ws_score = scores.get(ws_key, 0)
        if server_score > 0 and ws_score > 0:
            ratio = ws_score / server_score
            delta = (ws_score - server_score) / server_score * 100
            print(f"- **{label}**: Server GC score {server_score:,.0f} → Workstation GC score {ws_score:,.0f} ({ratio:.2f}x, {delta:+.1f}%)")

    # Compilation mode comparison
    print(f"\n### Compilation mode comparison (both Server GC)\n")
    if "jit-server" in config_data and "r2r-server" in config_data:
        jit_score = scores.get("jit-server", 0)
        r2r_score = scores.get("r2r-server", 0)
        if jit_score > 0 and r2r_score > 0:
            ratio = r2r_score / jit_score
            delta = (r2r_score - jit_score) / jit_score * 100
            print(f"- JIT score {jit_score:,.0f} → ReadyToRun score {r2r_score:,.0f} ({ratio:.2f}x, {delta:+.1f}%)")

    # Cold start comparison
    print(f"\n### Cold start comparison\n")
    for k in ordered:
        vals = [r["cold_start_ms"] for r in config_data[k]["runs"]]
        m = statistics.median(vals)
        print(f"- **{labels[k]}**: {m:,.0f} ms")

    # Native AOT note
    print(f"\n## Native AOT\n")
    print("Native AOT could not be built because `Campfire.Templates.Generator` targets `netstandard2.0`")
    print("(required for Roslyn source generators), which does not support AOT. The generator is only a")
    print("build-time dependency (`ReferenceOutputAssembly=false`, `OutputItemType=Analyzer`).\n")
    print("To enable Native AOT, the generator project needs `<IsAotCompatible>false</IsAotCompatible>`")
    print("in its `.csproj`. This is a one-line change that should be made in task F02 or P01.\n")
    print("Based on the .NET 10 documentation and general AOT characteristics, Native AOT is expected")
    print("to provide the best cold-start time and lowest idle memory, but may have lower peak throughput")
    print("than JIT with Dynamic PGO due to the lack of runtime profile-guided optimizations.")


if __name__ == "__main__":
    main()