"""Planted defect injection and verification for cable replay diff."""

import copy
import json
from typing import Any, Dict, List, Tuple

try:
    from .diff import Difference, diff_sequences
    from .normalizer import normalize_frame
except ImportError:
    from diff import Difference, diff_sequences
    from normalizer import normalize_frame

DEFECT_WRONG_OUTCOME = "wrong-outcome"
DEFECT_MISSING_MESSAGE = "missing-message"
DEFECT_EXTRA_MESSAGE = "extra-message"
DEFECT_MODIFIED_PAYLOAD = "modified-payload"
DEFECT_WRONG_DISCONNECT_REASON = "wrong-disconnect-reason"
DEFECT_WRONG_RECONNECT_FLAG = "wrong-reconnect-flag"
DEFECT_MISSING_LAG_DISCONNECT = "missing-lag-disconnect"

ALL_DEFECT_KINDS = [
    DEFECT_WRONG_OUTCOME,
    DEFECT_MISSING_MESSAGE,
    DEFECT_EXTRA_MESSAGE,
    DEFECT_MODIFIED_PAYLOAD,
    DEFECT_WRONG_DISCONNECT_REASON,
    DEFECT_WRONG_RECONNECT_FLAG,
    DEFECT_MISSING_LAG_DISCONNECT,
]


def inject_defect(frames: List[Dict[str, Any]], defect_kind: str) -> List[Dict[str, Any]]:
    """Injects a specific defect into a sequence of normalized frames."""
    mutated = copy.deepcopy(frames)

    if defect_kind == DEFECT_WRONG_OUTCOME:
        # Flip first confirm_subscription or reject_subscription
        for f in mutated:
            if f.get("type") == "confirm_subscription":
                f["type"] = "reject_subscription"
                return mutated
            elif f.get("type") == "reject_subscription":
                f["type"] = "confirm_subscription"
                return mutated
        # If neither found, alter first frame
        if mutated:
            mutated[0]["type"] = "reject_subscription"

    elif defect_kind == DEFECT_MISSING_MESSAGE:
        # Drop a message frame
        for i, f in enumerate(mutated):
            if "message" in f:
                mutated.pop(i)
                return mutated
        if mutated:
            mutated.pop(-1)

    elif defect_kind == DEFECT_EXTRA_MESSAGE:
        # Insert an unexpected message frame
        extra = {
            "identifier": json.dumps({"channel": "SpuriousChannel"}),
            "message": "unsolicited-broadcast",
        }
        mutated.append(extra)

    elif defect_kind == DEFECT_MODIFIED_PAYLOAD:
        # Modify the message payload
        for f in mutated:
            if "message" in f:
                if isinstance(f["message"], dict):
                    f["message"]["tampered"] = True
                elif isinstance(f["message"], str):
                    f["message"] += "<!-- tampered -->"
                else:
                    f["message"] = "tampered"
                return mutated
        if mutated:
            mutated[-1]["message"] = "tampered"

    elif defect_kind == DEFECT_WRONG_DISCONNECT_REASON:
        # Change disconnect reason
        for f in mutated:
            if f.get("type") == "disconnect":
                f["reason"] = "remote" if f.get("reason") != "remote" else "unauthorized"
                return mutated
        mutated.append({"type": "disconnect", "reason": "unauthorized", "reconnect": False})

    elif defect_kind == DEFECT_WRONG_RECONNECT_FLAG:
        # Flip reconnect flag
        for f in mutated:
            if f.get("type") == "disconnect":
                f["reconnect"] = not f.get("reconnect", False)
                return mutated
        mutated.append({"type": "disconnect", "reason": "remote", "reconnect": False})

    elif defect_kind == DEFECT_MISSING_LAG_DISCONNECT:
        # Remove lag disconnect frame
        for i, f in enumerate(mutated):
            if f.get("type") == "disconnect" and f.get("reason") is None and f.get("reconnect") is True:
                mutated.pop(i)
                return mutated
        # Remove any disconnect frame
        for i, f in enumerate(mutated):
            if f.get("type") == "disconnect":
                mutated.pop(i)
                return mutated

    return mutated


def verify_all_defects(sample_frames: List[Dict[str, Any]]) -> Dict[str, Tuple[bool, str]]:
    """Verifies that the diff engine successfully detects all planted defect kinds."""
    results = {}
    for kind in ALL_DEFECT_KINDS:
        defective = inject_defect(sample_frames, kind)
        diffs = diff_sequences(sample_frames, defective)
        caught = len(diffs) > 0
        detail = diffs[0].detail if diffs else "No difference detected"
        results[kind] = (caught, detail)
    return results
