"""State differential engine: compares normalized database tables and storage trees
between reference (Rails) and candidate (C#) servers.
"""

import difflib
import re

ID_FIELD_RE = re.compile(r"\b(id=[^ ]+|room_id=[^ ]+ user_id=[^ ]+)")


class TableDiff:
    def __init__(self, table, missing=None, extra=None, modified=None):
        self.table = table
        self.missing = missing or []
        self.extra = extra or []
        self.modified = modified or []

    @property
    def has_differences(self):
        return bool(self.missing or self.extra or self.modified)

    def as_dict(self):
        return {
            "table": self.table,
            "missing": self.missing,
            "extra": self.extra,
            "modified": self.modified,
        }


class StorageDiff:
    def __init__(self, missing=None, extra=None, modified=None):
        self.missing = missing or []
        self.extra = extra or []
        self.modified = modified or []

    @property
    def has_differences(self):
        return bool(self.missing or self.extra or self.modified)

    def as_dict(self):
        return {
            "missing": self.missing,
            "extra": self.extra,
            "modified": self.modified,
        }


class StateDiff:
    def __init__(self, db_diffs, storage_diff):
        self.db_diffs = db_diffs
        self.storage_diff = storage_diff

    @property
    def has_differences(self):
        return any(d.has_differences for d in self.db_diffs.values()) or self.storage_diff.has_differences

    def as_dict(self):
        return {
            "database": {name: d.as_dict() for name, d in self.db_diffs.items() if d.has_differences},
            "storage": self.storage_diff.as_dict() if self.storage_diff.has_differences else {},
        }


def _extract_key(row_str):
    m = ID_FIELD_RE.search(row_str)
    return m.group(1) if m else row_str


def diff_table(table_name, ref_rows, cand_rows):
    """Diff the rows of a single table between reference and candidate."""
    if ref_rows == cand_rows:
        return TableDiff(table_name)

    ref_set = set(ref_rows)
    cand_set = set(cand_rows)

    only_ref = [r for r in ref_rows if r not in cand_set]
    only_cand = [r for r in cand_rows if r not in ref_set]

    ref_by_key = {_extract_key(r): r for r in only_ref}
    cand_by_key = {_extract_key(r): r for r in only_cand}

    common_keys = set(ref_by_key.keys()) & set(cand_by_key.keys())
    modified = []
    for k in sorted(common_keys):
        r_val = ref_by_key.pop(k)
        c_val = cand_by_key.pop(k)
        modified.append({"key": k, "reference": r_val, "candidate": c_val})

    missing = [ref_by_key[k] for k in sorted(ref_by_key.keys())]
    extra = [cand_by_key[k] for k in sorted(cand_by_key.keys())]

    return TableDiff(table_name, missing=missing, extra=extra, modified=modified)


def diff_databases(ref_db_dump, cand_db_dump):
    """Diff two normalized database dumps table by table."""
    all_tables = sorted(set(ref_db_dump.keys()) | set(cand_db_dump.keys()))
    diffs = {}
    for table in all_tables:
        ref_rows = ref_db_dump.get(table, [])
        cand_rows = cand_db_dump.get(table, [])
        td = diff_table(table, ref_rows, cand_rows)
        if td.has_differences:
            diffs[table] = td
    return diffs


def _parse_storage_entry(entry):
    parts = {}
    for item in entry.split(" "):
        k, _, v = item.partition("=")
        parts[k] = v
    return parts


def diff_storage_trees(ref_tree_dump, cand_tree_dump):
    """Diff two storage tree dumps."""
    ref_map = {_parse_storage_entry(e).get("path"): e for e in ref_tree_dump}
    cand_map = {_parse_storage_entry(e).get("path"): e for e in cand_tree_dump}

    ref_paths = set(ref_map.keys())
    cand_paths = set(cand_map.keys())

    missing = [ref_map[p] for p in sorted(ref_paths - cand_paths)]
    extra = [cand_map[p] for p in sorted(cand_paths - ref_paths)]

    modified = []
    for p in sorted(ref_paths & cand_paths):
        if ref_map[p] != cand_map[p]:
            modified.append({
                "path": p,
                "reference": ref_map[p],
                "candidate": cand_map[p],
            })

    return StorageDiff(missing=missing, extra=extra, modified=modified)


def diff_states(ref_state, cand_state):
    """Diff two full state snapshots (database and storage)."""
    db_diffs = diff_databases(ref_state.get("database", {}), cand_state.get("database", {}))
    storage_diff = diff_storage_trees(ref_state.get("storage", []), cand_state.get("storage", []))
    return StateDiff(db_diffs, storage_diff)


def format_diff(state_diff):
    """Render a StateDiff into human-readable text."""
    if not state_diff.has_differences:
        return "State matches: 0 differences found."

    lines = ["State differences detected:"]
    for table, td in sorted(state_diff.db_diffs.items()):
        if not td.has_differences:
            continue
        lines.append(f"  Table '{table}':")
        if td.missing:
            lines.append(f"    Missing rows ({len(td.missing)} in reference only):")
            for r in td.missing[:10]:
                lines.append(f"      - {r}")
            if len(td.missing) > 10:
                lines.append(f"      ... and {len(td.missing) - 10} more")
        if td.extra:
            lines.append(f"    Extra rows ({len(td.extra)} in candidate only):")
            for r in td.extra[:10]:
                lines.append(f"      + {r}")
            if len(td.extra) > 10:
                lines.append(f"      ... and {len(td.extra) - 10} more")
        if td.modified:
            lines.append(f"    Modified rows ({len(td.modified)}):")
            for m in td.modified[:10]:
                lines.append(f"      ~ {m['key']}:")
                lines.append(f"        ref : {m['reference']}")
                lines.append(f"        cand: {m['candidate']}")
            if len(td.modified) > 10:
                lines.append(f"      ... and {len(td.modified) - 10} more")

    sd = state_diff.storage_diff
    if sd.has_differences:
        lines.append("  Storage tree:")
        if sd.missing:
            lines.append(f"    Missing files ({len(sd.missing)} in reference only):")
            for f in sd.missing[:10]:
                lines.append(f"      - {f}")
        if sd.extra:
            lines.append(f"    Extra files ({len(sd.extra)} in candidate only):")
            for f in sd.extra[:10]:
                lines.append(f"      + {f}")
        if sd.modified:
            lines.append(f"    Modified files ({len(sd.modified)}):")
            for m in sd.modified[:10]:
                lines.append(f"      ~ {m['path']}:")
                lines.append(f"        ref : {m['reference']}")
                lines.append(f"        cand: {m['candidate']}")

    return "\n".join(lines)
