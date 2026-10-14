#!/usr/bin/env python3
"""Print median requests/sec and p99 from one verification-harness results directory."""

import json
import sys
from pathlib import Path

READS = ["room_show", "messages_page", "sidebar", "search"]
LABELS = {
    "room_show": "Room page",
    "messages_page": "Messages page",
    "sidebar": "Sidebar",
    "search": "Search",
    "post_message": "Post a message",
}
APP_LABELS = {
    "rails": "Rails",
    "csharp": "C#",
    "rust": "Rust",
    "go": "Go",
    "express": "Express",
    "elixir": "Elixir",
    "django": "Django",
    "laravel": "Laravel",
    "c": "C",
}


def median(values):
    values = sorted(values)
    count = len(values)
    if count == 0:
        raise SystemExit("no samples")
    if count % 2:
        return values[count // 2]
    return (values[count // 2 - 1] + values[count // 2]) / 2


def load_summary(directory):
    mixed = (directory / "mixed-summary.json").exists()
    name = "mixed-summary.json" if mixed else "summary.json"
    path = directory / name
    if not path.exists():
        raise SystemExit(f"missing {path}")
    summary = json.loads(path.read_text())
    meta = summary["metadata"]
    if not meta.get("complete"):
        raise SystemExit(f"{directory}: metadata.complete is not true")
    return mixed, meta, "mixed_http" if mixed else "http"


def samples(directory, app, route, key):
    found = []
    for path in sorted(directory.glob(f"{app}-*.json")):
        row = json.loads(path.read_text())
        hits = [item for item in row[key] if item["route"] == route and item["conc"] == 16]
        if len(hits) != 1:
            raise SystemExit(f"{path.name} {route}: expected one 16-client sample, found {len(hits)}")
        item = hits[0]
        if item["errors"] or item["invalid_responses"] or item["validation"] != "route-contract-v1":
            raise SystemExit(f"{path.name} {route}: response was not validated")
        if item["statuses"] != {"200": item["ok"]} or item["ok"] <= 0:
            raise SystemExit(f"{path.name} {route}: statuses were not all 200")
        found.append(item)
    if len(found) < 2:
        raise SystemExit(f"{directory} {app} {route}: expected at least two rounds")
    return found


def raw_totals(directory):
    files = list((directory / "raw").glob("http-*.json"))
    if not files:
        raise SystemExit(f"{directory}: no raw samples")
    ok = errors = invalid = 0
    for path in files:
        item = json.loads(path.read_text())
        if item["validation"] != "route-contract-v1":
            raise SystemExit(f"{path}: validation is {item['validation']}")
        ok += item["ok"]
        errors += item["errors"]
        invalid += item["invalid_responses"]
        writer = item.get("writer")
        if writer:
            if writer["validation"] != "route-contract-v1":
                raise SystemExit(f"{path}: writer validation is {writer['validation']}")
            ok += writer["ok"]
            errors += writer["errors"]
            invalid += writer["invalid_responses"]
    return len(files), ok, errors, invalid


def main(argv):
    if len(argv) != 2:
        raise SystemExit(f"usage: {argv[0]} RESULTS_DIR")
    directory = Path(argv[1])
    mixed, meta, key = load_summary(directory)
    routes = READS if mixed else READS + ["post_message"]
    apps = list(meta["source_revisions"].keys())
    if "rails" not in apps or "csharp" not in apps:
        raise SystemExit(f"expected rails and csharp in one session, got {sorted(apps)}")
    print(f"profile: {meta['profile']}")
    print(f"seed: {meta['seed_sha256']}")
    print(f"verification: {meta['verification_revision']}")
    print(f"rounds: {meta['rounds']} duration: {meta['duration']}s cpus: {meta['server_cpus']} client: {meta['client_cpus']}")
    files, ok, errors, invalid = raw_totals(directory)
    print(f"raw_samples: {files} validated_responses: {ok} errors: {errors} invalid: {invalid}")
    if errors or invalid:
        raise SystemExit("raw samples include errors or invalid responses")
    cells = {
        route: {app: route_cell(directory, app, route, key) for app in apps}
        for route in routes
    }
    if set(apps) == {"rails", "csharp"}:
        print_pair(routes, cells)
    else:
        print_ports(routes, apps, cells, meta["source_revisions"])


def route_cell(directory, app, route, key):
    rows = samples(directory, app, route, key)
    rps = [row["rps"] for row in rows]
    p99 = [row["latency"]["p99_ms"] for row in rows]
    return median(rps), median(p99), min(rps), max(rps), rps


def print_pair(routes, cells):
    print("| Workload | Rails req/s | Rails p99 ms | C# req/s | C# p99 ms | C#/Rails |")
    print("|---|---:|---:|---:|---:|---:|")
    for route in routes:
        rails, csharp = cells[route]["rails"], cells[route]["csharp"]
        print(
            f"| {LABELS[route]} | {rails[0]:.1f} | {rails[1]:.3f} "
            f"| {csharp[0]:.1f} | {csharp[1]:.3f} | {csharp[0] / rails[0]:.3f} |"
        )
    print_spreads(routes, ["rails", "csharp"], cells)


# One column per app in harness order: median req/s, then p99, then each app's multiple of Rails.
def print_ports(routes, apps, cells, revisions):
    names = [APP_LABELS.get(app, app) for app in apps]
    for title, value in (("req/s", lambda cell: f"{cell[0]:,.0f}"), ("p99 ms", lambda cell: f"{cell[1]:.2f}")):
        print(f"\n{title}\n")
        print("| Workload | " + " | ".join(names) + " |")
        print("|---|" + "---:|" * len(apps))
        for route in routes:
            print(f"| {LABELS[route]} | " + " | ".join(value(cells[route][app]) for app in apps) + " |")
    print("\n× Rails req/s\n")
    print("| Workload | " + " | ".join(names) + " |")
    print("|---|" + "---:|" * len(apps))
    for route in routes:
        rails = cells[route]["rails"][0]
        print(f"| {LABELS[route]} | " + " | ".join(f"{cells[route][app][0] / rails:.2f}" for app in apps) + " |")
    print()
    for app in apps:
        print(f"  {app} {revisions[app]['head'][:12]}{' dirty' if revisions[app]['dirty'] else ''}")
    print_spreads(routes, apps, cells)


def print_spreads(routes, apps, cells):
    for route in routes:
        for app in apps:
            median_rps, _, low, high, rps = cells[route][app]
            spread = (high - low) / median_rps if median_rps else 0
            print(f"  {app} {route} runs={rps} spread={(spread * 100):.2f}%")


if __name__ == "__main__":
    main(sys.argv)
