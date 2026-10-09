"""Planted state defect generator for self-testing and mutation testing.

Mutates database and storage tree state dumps to verify that the state differential
harness reliably catches differences.
"""

import copy
import re


class StateDefect:
    def __init__(self, kind, target):
        self.kind = kind
        self.target = target

    @classmethod
    def parse(cls, spec):
        """Parse a defect spec string, e.g. 'missing-row:messages', 'extra-file:test.png'."""
        kind, _, target = spec.partition(":")
        return cls(kind, target)

    def apply_to_database(self, db_dump):
        """Apply defect to a database dump dict in place and return it."""
        dump = copy.deepcopy(db_dump)
        if self.kind == "missing-row":
            table = self.target
            if table in dump and dump[table]:
                dump[table] = dump[table][1:]
        elif self.kind == "extra-row":
            table = self.target
            if table in dump:
                extra = "id=999999999 created_at=time:0 updated_at=time:0 defect_marker=1"
                dump[table] = dump[table] + [extra]
        elif self.kind == "modified-row":
            parts = self.target.split(".", 1)
            table = parts[0]
            col = parts[1] if len(parts) > 1 else None
            if table in dump and dump[table]:
                first = dump[table][0]
                if col:
                    mutated = re.sub(rf"\b{col}=[^ ]+", f"{col}=PLANTED_DEFECT", first)
                else:
                    mutated = first + " PLANTED_DEFECT=1"
                dump[table][0] = mutated
        return dump

    def apply_to_storage(self, storage_dump):
        """Apply defect to a storage dump list in place and return it."""
        dump = list(storage_dump)
        if self.kind == "missing-file":
            if dump:
                dump = dump[1:]
        elif self.kind == "extra-file":
            dump.append(f"path={self.target or 'extra/defect.bin'} size=123 sha256=planteddefectsha256")
        elif self.kind == "modified-file":
            if dump:
                first = dump[0]
                mutated = re.sub(r"sha256=[a-f0-9]+", "sha256=ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", first)
                dump[0] = mutated
        return dump

    def apply_to_state(self, state):
        """Apply defect to a full state dict."""
        return {
            "database": self.apply_to_database(state.get("database", {})),
            "storage": self.apply_to_storage(state.get("storage", [])),
        }
