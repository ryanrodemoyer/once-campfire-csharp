#!/usr/bin/env python3
"""Response validation for bench/bin/run: nothing is measured until it matches the reference.

  validate.py capture --base URL --path PATH [--cookie C] --out FILE.json
      GET PATH once (Accept-Encoding: identity), save the body next to FILE.json and write its
      status, content type, size and hashes.
  validate.py compare ORACLE.json CANDIDATE.json
      Exit 1, saying why, unless the candidate has the oracle's status and content type and the
      same body once per-request values are blanked (see normalize).
  validate.py statuses EXPECTED.json SAMPLE.json
      Exit 1 unless every response in a load generator sample (bench/loadgen `http` output) has
      the status the validated response had, with no transport errors.
  validate.py mark DB
      Print the newest message id, the mark `writes` counts from.
  validate.py writes DB ROOM MARK OK
      Exit 1 unless the room gained exactly OK messages after MARK and every one of them reached
      the search index, as the Go port's harness checks.
  validate.py cable SAMPLE.json      every client subscribed and every message reached every client
  validate.py upload SAMPLE.json     every upload's thumbnail was served

The reference is the oracle: its responses are captured first and each other app's are compared
with them. The comparison is byte equality after normalization, which is stricter than the Go
harness's message-id contracts and looser than Q01's replay gate only in the values blanked here.
"""
import hashlib, json, re, sqlite3, sys, urllib.error, urllib.request
from pathlib import Path

# Values Rails generates afresh for every request. Everything else must match byte for byte.
PER_REQUEST = [
    # <meta name="csrf-token" content="..."> (csrf_meta_tags): a masked token, random per request.
    (re.compile(rb'(<meta name="csrf-token" content=")[^"]*(")'), rb"\1\2"),
    # <input type="hidden" name="authenticity_token" value="..."> in every form.
    (re.compile(rb'(name="authenticity_token" value=")[^"]*(")'), rb"\1\2"),
]


def normalize(body):
    for pattern, repl in PER_REQUEST:
        body = pattern.sub(repl, body)
    return body


def sha256(data):
    return hashlib.sha256(data).hexdigest()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def capture(base, path, cookie, out):
    headers = {"Accept-Encoding": "identity"}
    if cookie:
        headers["Cookie"] = cookie
    opener = urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(urllib.request.Request(base + path, headers=headers), timeout=30) as r:
            status, ctype, body = r.status, r.headers.get("Content-Type"), r.read()
    except urllib.error.HTTPError as e:
        status, ctype, body = e.code, e.headers.get("Content-Type"), e.read()
    out = Path(out)
    out.with_suffix(".body").write_bytes(body)
    record = {
        "path": path, "status": status, "content_type": ctype, "bytes": len(body),
        "sha256": sha256(body), "normalized_sha256": sha256(normalize(body)),
    }
    out.write_text(json.dumps(record, indent=1) + "\n")
    return record


def compare(oracle, candidate):
    """The reasons the candidate's response differs from the oracle's (empty when equal)."""
    problems = []
    if not 200 <= oracle["status"] < 400:
        problems.append(f"the reference answered {oracle['status']}: nothing to measure")
    for key in ("status", "content_type"):
        if oracle[key] != candidate[key]:
            problems.append(f"{key} {candidate[key]!r}, reference {oracle[key]!r}")
    if oracle["normalized_sha256"] != candidate["normalized_sha256"]:
        problems.append(f"body differs ({candidate['bytes']} bytes, reference {oracle['bytes']})")
    return problems


def status_problems(expected, sample):
    want = str(expected["status"])
    problems = []
    if sample.get("errors"):
        problems.append(f"{sample['errors']} transport errors")
    other = {k: v for k, v in sample.get("statuses", {}).items() if k != want}
    if other:
        problems.append(f"statuses {sample['statuses']}, validated {want}")
    if not sample.get("ok"):
        problems.append("no successful responses")
    return problems


def last_message_id(db):
    with sqlite3.connect(f"file:{db}?mode=ro", uri=True) as c:
        return c.execute("SELECT coalesce(max(id), 0) FROM messages").fetchone()[0]


def write_problems(db, room, mark, ok):
    """Every acknowledged post saved in the room and indexed (searchable.rb's create_in_index)."""
    with sqlite3.connect(f"file:{db}?mode=ro", uri=True) as c:
        persisted = c.execute("SELECT count(*) FROM messages WHERE room_id = ? AND id > ?", (room, mark)).fetchone()[0]
        indexed = c.execute(
            "SELECT count(*) FROM messages JOIN message_search_index idx ON messages.id = idx.rowid "
            "WHERE messages.room_id = ? AND messages.id > ?", (room, mark)).fetchone()[0]
    problems = []
    if persisted != ok:
        problems.append(f"{ok} acknowledged posts, {persisted} saved")
    if indexed != persisted:
        problems.append(f"{persisted} saved posts, {indexed} in the search index")
    return problems


def cable_problems(sample):
    problems = []
    if sample.get("ready") != sample.get("clients"):
        problems.append(f"{sample.get('ready')} of {sample.get('clients')} clients subscribed")
    t, l = sample.get("throughput", {}), sample.get("latency", {})
    if t.get("complete") != t.get("posted"):
        problems.append(f"saturated phase: {t.get('complete')} of {t.get('posted')} messages reached every client")
    if "complete" in l and "messages" in l and l["complete"] != l["messages"]:
        problems.append(f"paced phase: {l['complete']} of {l['messages']} messages reached every client")
    return problems


def upload_problems(sample):
    runs = sample.get("runs") or []
    if not runs:
        return ["no upload runs"]
    missing = sum(1 for r in runs if r.get("thumb_ms") is None)
    return [f"{missing} of {len(runs)} thumbnails never served"] if missing else []


def load(path):
    return json.loads(Path(path).read_text())


def report(problems, what):
    for p in problems:
        print(f"validate: {what}: {p}", file=sys.stderr)
    return 1 if problems else 0


def main(argv):
    cmd, args = argv[0], argv[1:]
    if cmd == "capture":
        opts = dict(zip(args[::2], args[1::2]))
        capture(opts["--base"], opts["--path"], opts.get("--cookie", ""), opts["--out"])
        return 0
    if cmd == "compare":
        return report(compare(load(args[0]), load(args[1])), Path(args[1]).stem)
    if cmd == "statuses":
        return report(status_problems(load(args[0]), json.loads(args[1])), "measured responses")
    if cmd == "mark":
        print(last_message_id(args[0]))
        return 0
    if cmd == "writes":
        return report(write_problems(args[0], int(args[1]), int(args[2]), int(args[3])), "posts")
    if cmd == "cable":
        return report(cable_problems(json.loads(args[0])), "cable")
    if cmd == "upload":
        return report(upload_problems(json.loads(args[0])), "upload")
    print(__doc__, file=sys.stderr)
    return 64


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
