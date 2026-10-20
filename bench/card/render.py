#!/usr/bin/env python3
"""Render the port comparison table in bench/PORTS.md as a shareable PNG.

The numbers come from the results directories through summarize.py, so the image can't drift from
the committed samples. Usage: bench/card/render.py [NORMAL_DIR MIXED_DIR [OUT.png]]
"""

import html
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageChops

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "bench" / "verification"))
from summarize import APP_LABELS, LABELS, READS, load_summary, raw_totals, route_cell  # noqa: E402

FEATURED = "csharp"
ORDER = ["csharp", "rails", "rust", "go", "express", "c", "laravel"]
REPO = "github.com/ryanrodemoyer/once-campfire-csharp"
COUNTS = {6: "six", 7: "seven", 8: "eight", 9: "nine"}
WIDTH = 1400


def measure(directory, routes):
    _, meta, key = load_summary(directory)
    apps = [app for app in ORDER if app in meta["source_revisions"]]
    cells = {route: {app: route_cell(directory, app, route, key)[0] for app in apps} for route in routes}
    _, _, errors, invalid = raw_totals(directory)
    if errors or invalid:
        raise SystemExit(f"{directory}: raw samples include errors or invalid responses")
    return meta, apps, cells


def row_html(group, group_rows, label, values, apps):
    best = max(values.values())
    cells = []
    for app in apps:
        value = values[app]
        classes = []
        if app == FEATURED:
            classes.append("featured")
        if value == best:
            classes.append("best")
        cells.append(f'<td class="{" ".join(classes)}">{value:,.0f}</td>')
    head = f'<th class="group" rowspan="{group_rows}">{html.escape(group)}</th>' if group else ""
    return f'<tr>{head}<th class="workload">{html.escape(label)}</th><td class="unit">req/s</td>{"".join(cells)}</tr>'


def page(normal, mixed):
    meta, apps, normal_cells = normal
    _, mixed_apps, mixed_cells = mixed
    if mixed_apps != apps:
        raise SystemExit(f"normal and mixed runs measured different apps: {apps} vs {mixed_apps}")
    revisions = meta["source_revisions"]
    head = "".join(
        f'<th class="app{" featured top" if app == FEATURED else ""}">{APP_LABELS[app]}'
        f'<span class="sha">{revisions[app]["head"][:7]}</span></th>'
        for app in apps
    )
    rows = []
    normal_routes = READS + ["post_message"]
    for index, route in enumerate(normal_routes):
        group = "Each route alone" if index == 0 else ""
        rows.append(row_html(group, len(normal_routes), LABELS[route], normal_cells[route], apps))
    for index, route in enumerate(READS):
        group = "Reads while a writer posts 10/s" if index == 0 else ""
        rows.append(row_html(group, len(READS), LABELS[route], mixed_cells[route], apps))
    rows[-1] = rows[-1].replace('class="featured', 'class="featured bottom', 1)
    brand = f"<span>{REPO}</span>" * 3
    return f"""<!doctype html>
<html><head><meta charset="utf-8"><style>
  :root {{ --ink: #1f2328; --muted: #5f6368; --rule: #1f2328; --soft: #dadce0;
           --blue: #1a73e8; --blue-fill: #a8d1ff; --gray-fill: #e3e6ea; --bg: #f8f9fc; }}
  * {{ box-sizing: border-box; }}
  body {{ margin: 0; background: var(--bg); color: var(--ink);
         font-family: "IBM Plex Sans", "DejaVu Sans", sans-serif; }}
  main {{ width: {WIDTH}px; padding: 40px 40px 28px; }}
  h1 {{ font-size: 22px; font-weight: 600; margin: 0 0 4px 160px; }}
  .lede {{ font-size: 14px; color: var(--muted); margin: 0 0 22px 160px; }}
  table {{ width: 100%; border-collapse: collapse; font-size: 16px; }}
  th, td {{ padding: 0 14px; height: 44px; border-bottom: 1px solid var(--soft); }}
  thead th {{ height: 56px; font-weight: 500; border-bottom: 1px solid var(--rule); text-align: center; }}
  thead th.lead {{ text-align: left; }}
  .sha {{ display: block; font: 11px "IBM Plex Mono", monospace; color: var(--muted); margin-top: 2px; }}
  th.group {{ width: 160px; text-align: left; vertical-align: middle; font-size: 12px; font-weight: 400;
             color: var(--muted); border-bottom: 1px solid var(--rule); }}
  tr:nth-child(5) th, tr:nth-child(5) td, tr:last-child th, tr:last-child td {{ border-bottom-color: var(--rule); }}
  th.workload {{ text-align: left; font-weight: 500; white-space: nowrap; }}
  td.unit {{ width: 60px; font-size: 11px; color: var(--muted); text-align: right; padding-left: 0; }}
  td {{ text-align: center; font-variant-numeric: tabular-nums; }}
  td.best {{ background: var(--gray-fill); font-weight: 600; }}
  td.featured.best {{ background: var(--blue-fill); color: #0b2a55; }}
  .featured {{ border-left: 2px solid var(--blue); border-right: 2px solid var(--blue); }}
  th.featured.top {{ border-top: 2px solid var(--blue); font-weight: 600; }}
  td.featured.bottom {{ border-bottom: 2px solid var(--blue); }}
  .grid {{ position: relative; }}
  .brand {{ position: absolute; inset: 0; overflow: hidden; pointer-events: none; }}
  .brand div {{ position: absolute; left: 50%; top: 50%; width: 2600px; margin-left: -1300px;
               transform: translateY(-50%) rotate(-12deg); display: flex; flex-direction: column; gap: 210px;
               align-items: center; font: 600 38px "IBM Plex Mono", monospace; letter-spacing: 1px;
               color: rgba(26, 115, 232, 0.075); white-space: nowrap; }}
  .mark {{ margin-top: 14px; text-align: right; font: 13px "IBM Plex Mono", monospace; color: var(--muted); }}
</style></head><body><main>
  <h1>Campfire, {COUNTS.get(len(apps), len(apps))} ways</h1>
  <p class="lede">Requests per second, 4 cores per app on one Google Cloud c3 VM. Higher is better; the fastest in each row is shaded.</p>
  <div class="grid">
  <table>
    <thead><tr><th></th><th class="lead">Workload</th><th></th>{head}</tr></thead>
    <tbody>{"".join(rows)}</tbody>
  </table>
  <div class="brand"><div>{brand}</div></div>
  </div>
  <div class="mark">{REPO}</div>
</main></body></html>
"""


def screenshot(document, out):
    chrome = shutil.which("google-chrome") or shutil.which("chromium")
    if not chrome:
        raise SystemExit("needs google-chrome or chromium on PATH")
    with tempfile.TemporaryDirectory() as tmp:
        source = Path(tmp) / "card.html"
        source.write_text(document)
        shot = Path(tmp) / "card.png"
        subprocess.run(
            [chrome, "--headless", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=2",
             f"--window-size={WIDTH},1400", f"--screenshot={shot}", source.as_uri()],
            check=True, capture_output=True,
        )
        image = Image.open(shot).convert("RGB")
    # Trim the empty page below the watermark.
    background = Image.new("RGB", image.size, image.getpixel((0, image.height - 1)))
    box = ImageChops.difference(image, background).getbbox()
    image.crop((0, 0, image.width, min(image.height, box[3] + 56))).save(out)


def main(argv):
    results = ROOT / "bench" / "results"
    normal_dir = Path(argv[1]) if len(argv) > 2 else results / "ports-20261014-normal"
    mixed_dir = Path(argv[2]) if len(argv) > 2 else results / "ports-20261014-mixed"
    out = Path(argv[3]) if len(argv) > 3 else ROOT / "bench" / "card" / "ports.png"
    normal = measure(normal_dir, READS + ["post_message"])
    mixed = measure(mixed_dir, READS)
    screenshot(page(normal, mixed), out)
    print(out)


if __name__ == "__main__":
    main(sys.argv)
