import os
import shutil
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import defects
import diff
import dump


class TestNormalizer(unittest.TestCase):
    def test_password_digest(self):
        digest = "$2a$12$PRRKGcPA9YrISO9Waii6ue.o4ZWp05y6VF8HLP3YsuGcj1.Y3p11a"
        self.assertEqual(dump.normalize_value("password_digest", digest), "bcrypt:$2a$")
        self.assertEqual(dump.normalize_value("password_digest", None), "NULL")

    def test_random_tokens(self):
        self.assertEqual(dump.normalize_value("token", "abcdef123456"), "random:12")
        self.assertEqual(dump.normalize_value("bot_token", "bot123456789"), "random:12")
        self.assertEqual(dump.normalize_value("join_code", "CRMu-l8Ge-KB9B"), "random:14")

    def test_deactivated_email(self):
        email = "kevin-deactivated-d86b86b4-2391-4cf1-8374-123456789abc@example.com"
        normalized = dump.normalize_value("email_address", email)
        self.assertEqual(normalized, "kevin-deactivated-«uuid»@example.com")

        regular = "kevin@example.com"
        self.assertEqual(dump.normalize_value("email_address", regular), "'kevin@example.com'")

    def test_client_message_id_uuid(self):
        uuid_str = "d86b86b4-2391-4cf1-8374-9876543210ab"
        self.assertEqual(dump.normalize_value("client_message_id", uuid_str), "«uuid»")
        self.assertEqual(dump.normalize_value("client_message_id", "s1"), "'s1'")

    def test_timestamps(self):
        self.assertEqual(dump.normalize_value("created_at", "2026-03-02 16:00:00"), "time:0")
        self.assertEqual(dump.normalize_value("updated_at", "2026-03-02 16:00:00.123456"), "time:6")

    def test_account_settings_canonical_json(self):
        json_str = '{"b": 2, "a": 1}'
        self.assertEqual(dump.normalize_value("settings", json_str), '{"a":1,"b":2}')


class TestDatabaseDumpingAndDiffing(unittest.TestCase):
    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        self.conn.execute("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, email_address TEXT, password_digest TEXT, created_at TEXT)")
        self.conn.execute("CREATE TABLE messages (id INTEGER PRIMARY KEY, client_message_id TEXT, body TEXT, created_at TEXT)")
        self.conn.execute("INSERT INTO users VALUES (1, 'Alice', 'alice@test.com', '$2a$12$saltsalt...', '2026-03-02 16:00:00')")
        self.conn.execute("INSERT INTO users VALUES (2, 'Bob', 'bob@test.com', '$2a$12$othersalt...', '2026-03-02 16:00:00')")
        self.conn.execute("INSERT INTO messages VALUES (10, 's1', 'Hello', '2026-03-02 16:00:00')")

    def tearDown(self):
        self.conn.close()

    def test_dump_table_sorting(self):
        rows = dump.dump_table(self.conn, "users")
        self.assertEqual(len(rows), 2)
        # Columns must be alphabetically sorted
        self.assertTrue(rows[0].startswith("created_at=time:0 email_address='alice@test.com' id=1 name='Alice' password_digest=bcrypt:$2a$"))

    def test_identical_diff_is_clean(self):
        d1 = dump.dump_database(self.conn, tables=["users", "messages"])
        d2 = dump.dump_database(self.conn, tables=["users", "messages"])
        diffs = diff.diff_databases(d1, d2)
        self.assertEqual(len(diffs), 0)

    def test_diff_catches_missing_and_extra_row(self):
        d1 = dump.dump_database(self.conn, tables=["users"])
        self.conn.execute("INSERT INTO users VALUES (3, 'Charlie', 'charlie@test.com', '$2a$12$hash...', '2026-03-02 16:00:00')")
        d2 = dump.dump_database(self.conn, tables=["users"])

        # d1 is missing row 3; d2 has extra row 3 relative to d1
        diff_result = diff.diff_databases(d1, d2)
        self.assertIn("users", diff_result)
        self.assertEqual(len(diff_result["users"].extra), 1)
        self.assertEqual(len(diff_result["users"].missing), 0)

    def test_diff_catches_modified_row(self):
        d1 = dump.dump_database(self.conn, tables=["users"])
        self.conn.execute("UPDATE users SET name = 'Alicia' WHERE id = 1")
        d2 = dump.dump_database(self.conn, tables=["users"])

        diff_result = diff.diff_databases(d1, d2)
        self.assertIn("users", diff_result)
        self.assertEqual(len(diff_result["users"].modified), 1)
        self.assertEqual(diff_result["users"].modified[0]["key"], "id=1")


class TestStorageTreeDumpingAndDiffing(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        os.makedirs(os.path.join(self.tmp, "aa", "bb"))
        with open(os.path.join(self.tmp, "aa", "bb", "file1.txt"), "wb") as f:
            f.write(b"hello world")
        with open(os.path.join(self.tmp, "file2.txt"), "wb") as f:
            f.write(b"second file")

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_dump_storage_tree(self):
        entries = dump.dump_storage_tree(self.tmp)
        self.assertEqual(len(entries), 2)
        self.assertTrue(entries[0].startswith("path=aa/bb/file1.txt size=11 sha256="))
        self.assertTrue(entries[1].startswith("path=file2.txt size=11 sha256="))

    def test_diff_storage_trees(self):
        t1 = dump.dump_storage_tree(self.tmp)

        # Mutate tmp directory
        with open(os.path.join(self.tmp, "file2.txt"), "wb") as f:
            f.write(b"modified contents")
        t2 = dump.dump_storage_tree(self.tmp)

        sd = diff.diff_storage_trees(t1, t2)
        self.assertTrue(sd.has_differences)
        self.assertEqual(len(sd.modified), 1)
        self.assertEqual(sd.modified[0]["path"], "file2.txt")


class TestPlantedDefects(unittest.TestCase):
    def setUp(self):
        self.base_state = {
            "database": {
                "messages": [
                    "created_at=time:0 id=1 client_message_id='s1' body='First'",
                    "created_at=time:0 id=2 client_message_id='s2' body='Second'",
                ],
            },
            "storage": [
                "path=blob1 size=100 sha256=1111111111111111111111111111111111111111111111111111111111111111",
                "path=blob2 size=200 sha256=2222222222222222222222222222222222222222222222222222222222222222",
            ],
        }

    def test_all_defect_types_detected(self):
        defect_specs = [
            "missing-row:messages",
            "extra-row:messages",
            "modified-row:messages.body",
            "missing-file:blob1",
            "extra-file:extra/blob.bin",
            "modified-file:blob1",
        ]

        for spec in defect_specs:
            defect = defects.StateDefect.parse(spec)
            mutated = defect.apply_to_state(self.base_state)
            sd = diff.diff_states(self.base_state, mutated)
            self.assertTrue(sd.has_differences, f"Planted defect '{spec}' was NOT detected!")


if __name__ == "__main__":
    unittest.main()
