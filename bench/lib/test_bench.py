#!/usr/bin/env python3
"""Tests for bench/lib/validate.py and bench/bin/report:  python3 bench/lib/test_bench.py"""
import json, sqlite3, subprocess, sys, tempfile, unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(HERE))
import validate  # noqa: E402

REPORT = HERE.parent / "bin" / "report"


def record(status=200, ctype="text/html; charset=utf-8", body=b"<p>hi</p>"):
    return {"status": status, "content_type": ctype, "bytes": len(body),
            "sha256": validate.sha256(body), "normalized_sha256": validate.sha256(validate.normalize(body))}


class NormalizeTest(unittest.TestCase):
    def test_blanks_csrf_meta_and_form_tokens(self):
        a = (b'<meta name="csrf-token" content="abc+/=" />'
             b'<input type="hidden" name="authenticity_token" value="tok1" autocomplete="off" />')
        b = (b'<meta name="csrf-token" content="xyz" />'
             b'<input type="hidden" name="authenticity_token" value="tok2" autocomplete="off" />')
        self.assertEqual(validate.normalize(a), validate.normalize(b))

    def test_keeps_everything_else(self):
        self.assertNotEqual(validate.normalize(b'<p data-message-id="1">'), validate.normalize(b'<p data-message-id="2">'))


class CompareTest(unittest.TestCase):
    def test_equal_responses(self):
        self.assertEqual(validate.compare(record(), record()), [])

    def test_status_content_type_and_body(self):
        self.assertIn("status 501, reference 200", validate.compare(record(), record(status=501))[0])
        self.assertTrue(any("content_type" in p for p in validate.compare(record(), record(ctype="text/plain"))))
        self.assertTrue(any("body differs" in p for p in validate.compare(record(), record(body=b"<p>ho</p>"))))

    def test_refuses_a_failing_reference(self):
        self.assertTrue(any("the reference answered 500" in p for p in validate.compare(record(status=500), record(status=500))))


class StatusesTest(unittest.TestCase):
    def test_all_validated_status(self):
        self.assertEqual(validate.status_problems({"status": 200}, {"statuses": {"200": 10}, "errors": 0, "ok": 10}), [])

    def test_other_statuses_errors_and_nothing(self):
        self.assertTrue(validate.status_problems({"status": 200}, {"statuses": {"200": 9, "500": 1}, "errors": 0, "ok": 9}))
        self.assertTrue(validate.status_problems({"status": 200}, {"statuses": {"200": 9}, "errors": 1, "ok": 9}))
        self.assertTrue(validate.status_problems({"status": 200}, {"statuses": {}, "errors": 0, "ok": 0}))


class WritesTest(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.db = Path(self.dir.name) / "db.sqlite3"
        with sqlite3.connect(self.db) as c:
            c.execute("CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id INTEGER)")
            c.execute("CREATE VIRTUAL TABLE message_search_index USING fts5(body, tokenize=porter)")
            c.execute("INSERT INTO messages (id, room_id) VALUES (933434569, 1)")

    def tearDown(self):
        self.dir.cleanup()

    def post(self, room, indexed=True):
        with sqlite3.connect(self.db) as c:
            mid = c.execute("INSERT INTO messages (room_id) VALUES (?)", (room,)).lastrowid
            if indexed:
                c.execute("INSERT INTO message_search_index (rowid, body) VALUES (?, 'bench write')", (mid,))

    def test_counts_saved_and_indexed_posts_after_the_mark(self):
        mark = validate.last_message_id(self.db)
        self.assertEqual(mark, 933434569)
        self.post(2); self.post(2)
        self.assertEqual(validate.write_problems(self.db, 2, mark, 2), [])
        self.assertEqual(validate.write_problems(self.db, 2, mark, 3), ["3 acknowledged posts, 2 saved"])
        self.post(2, indexed=False)
        self.assertEqual(validate.write_problems(self.db, 2, mark, 3), ["3 saved posts, 2 in the search index"])


class CableAndUploadTest(unittest.TestCase):
    def test_cable(self):
        ok = {"clients": 100, "ready": 100, "throughput": {"posted": 50, "complete": 50}, "latency": {"messages": 20, "complete": 20}}
        self.assertEqual(validate.cable_problems(ok), [])
        self.assertTrue(validate.cable_problems({**ok, "ready": 99}))
        self.assertTrue(validate.cable_problems({**ok, "throughput": {"posted": 50, "complete": 49}}))

    def test_upload(self):
        self.assertEqual(validate.upload_problems({"runs": [{"post_ms": 1, "thumb_ms": 2}]}), [])
        self.assertTrue(validate.upload_problems({"runs": [{"post_ms": 1}]}))
        self.assertTrue(validate.upload_problems({}))


def http_sample(route, conc, rps):
    return {"route": route, "conc": conc, "rps": rps, "ok": 100, "errors": 0, "statuses": {"200": 100},
            "latency": {"n": 100, "p50_ms": 1.0, "p99_ms": 2.0}}


def run_result(app, rep, rps):
    return {"app": app, "rep": rep, "cold_start_ms": 900, "validated": True,
            "memory": {"idle_current_mb": 100, "idle_anon_mb": 50, "peak_current_mb": 200, "peak_anon_mb": 80, "cgroup_peak_mb": 210},
            "http": [http_sample("up", c, rps * c) for c in (1, 16, 64)], "cable": [], "upload": {}}


class ReportTest(unittest.TestCase):
    def report(self, results, extra=None):
        with tempfile.TemporaryDirectory() as d:
            for r in results:
                (Path(d) / f"{r['app']}-{r['rep']}.json").write_text(json.dumps(r))
            for name, text in (extra or {}).items():
                (Path(d) / name).write_text(text)
            return subprocess.run([sys.executable, str(REPORT), d], capture_output=True, text=True, check=True).stdout

    def test_readme_table_and_advantage(self):
        out = self.report([run_result("reference", 1, 100), run_result("reference", 2, 300),
                           run_result("csharp", 1, 1000), run_result("csharp", 2, 1000)])
        self.assertIn("| HTTP workload (requests/sec) | Rails | C# |\n|---|---:|---:|\n| Health check (/up) | 3,200 | 16,000 |", out)
        self.assertIn("| up c=16 req/s | 3,200 [1,600–4,800] | 16,000 [16,000–16,000] | 5.0× |", out)
        self.assertNotIn("Not validated", out)

    def test_flags_unvalidated_and_failed_runs(self):
        out = self.report([{**run_result("reference", 1, 100), "validated": False}], {"failed.txt": "csharp rep 1: up differs\n"})
        self.assertIn("**The run stopped before finishing:** csharp rep 1: up differs", out)
        self.assertIn("**Not validated against the reference:** reference", out)


if __name__ == "__main__":
    unittest.main()
