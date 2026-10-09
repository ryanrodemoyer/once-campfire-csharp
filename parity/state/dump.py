"""Normalized database and storage-tree dumper for Campfire state parity.

Dumps SQLite tables and Active Storage file trees into stable, canonical representations
that can be compared between reference (Rails) and candidate (C#) servers.
Values that are random or clock-dependent are reduced to typed masks or shapes.
"""

import hashlib
import json
import os
import re
import sqlite3
import sys

TABLES = [
    "accounts",
    "action_text_rich_texts",
    "active_storage_attachments",
    "active_storage_blobs",
    "active_storage_variant_records",
    "bans",
    "boosts",
    "memberships",
    "messages",
    "push_subscriptions",
    "rooms",
    "searches",
    "sessions",
    "users",
    "webhooks",
    "message_search_index",
]

ORDER_BY = {
    "memberships": "room_id, user_id",
    "message_search_index": "rowid",
}

UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", re.IGNORECASE)


class DumpOptions:
    def __init__(self, seed_time=None, frozen_clock=False):
        self.seed_time = seed_time
        self.frozen_clock = frozen_clock


def normalize_value(col_name, value, options=None):
    if value is None:
        return "NULL"

    # Password digests: bcrypt salts and digests differ on every hash
    if col_name == "password_digest" and isinstance(value, str):
        if value.startswith("$2"):
            return f"bcrypt:{value[:4]}"
        return "bcrypt"

    # Random tokens and join codes
    if col_name in ("bot_token", "token", "join_code") and isinstance(value, str):
        return f"random:{len(value)}"

    # Deactivated email addresses containing random UUIDs
    if col_name == "email_address" and isinstance(value, str) and "-deactivated-" in value:
        local, rest = value.split("-deactivated-", 1)
        at_idx = rest.find("@")
        if at_idx != -1:
            return f"{local}-deactivated-«uuid»{rest[at_idx:]}"
        return f"{local}-deactivated-«uuid»"

    # Client message IDs: random UUIDs generated for messages without a client message id
    if col_name == "client_message_id" and isinstance(value, str):
        if UUID_RE.match(value):
            return "«uuid»"
        return repr(value)

    # Timestamps
    if col_name.endswith("_at") and isinstance(value, str):
        if "." in value:
            fraction_len = len(value.split(".", 1)[1])
            return f"time:{fraction_len}"
        return "time:0"

    # Account settings JSON column
    if col_name == "settings" and isinstance(value, str):
        try:
            parsed = json.loads(value)
            return json.dumps(parsed, sort_keys=True, separators=(",", ":"))
        except (ValueError, TypeError):
            pass

    return repr(value)


def dump_table(conn, table_name, order_by=None, options=None):
    """Dump all rows of a table in deterministic order with normalized column values."""
    order = order_by or ORDER_BY.get(table_name, "id")
    columns = "rowid AS id, body" if table_name == "message_search_index" else "*"

    cursor = conn.cursor()
    try:
        cursor.execute(f'SELECT {columns} FROM "{table_name}" ORDER BY {order}')
    except sqlite3.OperationalError:
        # Table might not exist in a minimalist or pre-migration DB
        return []

    col_names = [desc[0] for desc in cursor.description]
    rows = cursor.fetchall()

    dumped_rows = []
    for row in rows:
        fields = []
        for col_name, val in zip(col_names, row):
            rendered = normalize_value(col_name, val, options)
            fields.append((col_name, rendered))
        fields.sort(key=lambda f: f[0])
        row_str = " ".join(f"{col}={val}" for col, val in fields)
        dumped_rows.append(row_str)

    return dumped_rows


def dump_database(db_path_or_conn, options=None, tables=None):
    """Dump normalized tables for a SQLite database into a dict of table_name -> [row_strings]."""
    tables_to_dump = tables or TABLES
    close_when_done = False

    if isinstance(db_path_or_conn, (str, bytes, os.PathLike)):
        conn = sqlite3.connect(f"file:{os.path.abspath(db_path_or_conn)}?immutable=1", uri=True)
        close_when_done = True
    else:
        conn = db_path_or_conn

    try:
        cursor = conn.cursor()
        cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")
        existing_tables = {r[0] for r in cursor.fetchall()}

        dump = {}
        for table in tables_to_dump:
            if table in existing_tables:
                dump[table] = dump_table(conn, table, options=options)
        return dump
    finally:
        if close_when_done:
            conn.close()


def dump_storage_tree(storage_dir, options=None):
    """Dump files in an Active Storage directory with relative path, size, and sha256 checksum."""
    if not storage_dir or not os.path.isdir(storage_dir):
        return []

    entries = []
    storage_dir = os.path.abspath(storage_dir)

    for root, _, files in os.walk(storage_dir):
        for f in files:
            # Skip SQLite temp/WAL files if storage points at root storage instead of files/
            if f.endswith((".sqlite3", ".sqlite3-wal", ".sqlite3-shm", ".json", ".lock")):
                continue
            full_path = os.path.join(root, f)
            rel_path = os.path.relpath(full_path, storage_dir)
            try:
                size = os.path.getsize(full_path)
                h = hashlib.sha256()
                with open(full_path, "rb") as fp:
                    while chunk := fp.read(65536):
                        h.update(chunk)
                checksum = h.hexdigest()
                entries.append(f"path={rel_path} size={size} sha256={checksum}")
            except (OSError, IOError):
                continue

    entries.sort()
    return entries


def dump_state(state_dir, options=None):
    """Dump both the database and the storage tree of a state directory.

    Expects directory layout:
      dir/db/production.sqlite3  (or dir/production.sqlite3)
      dir/storage/               (or dir/files/)
    """
    state_dir = os.path.abspath(state_dir)
    db_candidates = [
        os.path.join(state_dir, "db", "production.sqlite3"),
        os.path.join(state_dir, "db", "test.sqlite3"),
        os.path.join(state_dir, "production.sqlite3"),
        os.path.join(state_dir, "test.sqlite3"),
    ]
    db_path = next((p for p in db_candidates if os.path.isfile(p)), None)

    storage_candidates = [
        os.path.join(state_dir, "storage"),
        os.path.join(state_dir, "files"),
        state_dir,
    ]
    storage_dir = next((p for p in storage_candidates if os.path.isdir(p) and p != state_dir), None)

    db_dump = dump_database(db_path, options=options) if db_path else {}
    storage_dump = dump_storage_tree(storage_dir, options=options) if storage_dir else []

    return {
        "db_path": db_path,
        "storage_dir": storage_dir,
        "database": db_dump,
        "storage": storage_dump,
    }
