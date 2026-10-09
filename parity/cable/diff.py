"""Frame sequence diffing and comparison engine."""

import difflib
import json
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Union

try:
    from .normalizer import canonical_identifier, normalize_frame, normalize_sequence
except ImportError:
    from normalizer import canonical_identifier, normalize_frame, normalize_sequence


@dataclass
class Difference:
    kind: str
    channel: str
    index: Optional[int]
    expected: Any
    actual: Any
    detail: str


def diff_frames(expected: Dict[str, Any], actual: Dict[str, Any], channel: str, index: int) -> Optional[Difference]:
    """Compares two normalized frame dictionaries."""
    exp_type = expected.get("type")
    act_type = actual.get("type")
    if exp_type != act_type:
        return Difference(
            kind="type-mismatch",
            channel=channel,
            index=index,
            expected=exp_type,
            actual=act_type,
            detail=f"Frame #{index} on {channel}: expected type {exp_type!r}, got {act_type!r}",
        )

    exp_id = expected.get("identifier")
    act_id = actual.get("identifier")
    if exp_id != act_id:
        return Difference(
            kind="identifier-mismatch",
            channel=channel,
            index=index,
            expected=exp_id,
            actual=act_id,
            detail=f"Frame #{index}: expected identifier {exp_id!r}, got {act_id!r}",
        )

    if "reason" in expected or "reason" in actual:
        exp_reason = expected.get("reason")
        act_reason = actual.get("reason")
        if exp_reason != act_reason:
            return Difference(
                kind="disconnect-reason-mismatch",
                channel=channel,
                index=index,
                expected=exp_reason,
                actual=act_reason,
                detail=f"Disconnect #{index}: expected reason {exp_reason!r}, got {act_reason!r}",
            )

    if "reconnect" in expected or "reconnect" in actual:
        exp_recon = expected.get("reconnect")
        act_recon = actual.get("reconnect")
        if exp_recon != act_recon:
            return Difference(
                kind="disconnect-reconnect-mismatch",
                channel=channel,
                index=index,
                expected=exp_recon,
                actual=act_recon,
                detail=f"Disconnect #{index}: expected reconnect={exp_recon!r}, got {act_recon!r}",
            )

    if "message" in expected or "message" in actual:
        exp_msg = expected.get("message")
        act_msg = actual.get("message")
        if exp_msg != act_msg:
            return Difference(
                kind="message-payload-mismatch",
                channel=channel,
                index=index,
                expected=exp_msg,
                actual=act_msg,
                detail=f"Message #{index} on {channel}: payload differed",
            )

    return None


def diff_sequences(
    expected_frames: List[Union[str, Dict[str, Any]]],
    actual_frames: List[Union[str, Dict[str, Any]]],
    channel: str = "(all)",
) -> List[Difference]:
    """Diffs two lists of raw or normalized frames, returning all differences found."""
    norm_exp = [f if isinstance(f, dict) else normalize_frame(f) for f in expected_frames]
    norm_act = [f if isinstance(f, dict) else normalize_frame(f) for f in actual_frames]

    # Filter out None (pings)
    clean_exp = [f for f in norm_exp if f is not None]
    clean_act = [f for f in norm_act if f is not None]

    differences: List[Difference] = []
    min_len = min(len(clean_exp), len(clean_act))

    for i in range(min_len):
        diff = diff_frames(clean_exp[i], clean_act[i], channel=channel, index=i)
        if diff:
            differences.append(diff)

    if len(clean_exp) != len(clean_act):
        differences.append(
            Difference(
                kind="count-mismatch",
                channel=channel,
                index=min_len,
                expected=len(clean_exp),
                actual=len(clean_act),
                detail=f"Frame count mismatch on {channel}: expected {len(clean_exp)}, got {len(clean_act)}",
            )
        )

    return differences


def format_diff_report(differences: List[Difference]) -> str:
    """Formats a list of Differences into a readable string."""
    if not differences:
        return "No differences found (perfect match)."

    lines = [f"Found {len(differences)} difference(s):"]
    for i, d in enumerate(differences, 1):
        lines.append(f"  {i}. [{d.kind}] {d.detail}")
        if d.expected is not None or d.actual is not None:
            exp_s = json.dumps(d.expected, sort_keys=True) if isinstance(d.expected, (dict, list)) else str(d.expected)
            act_s = json.dumps(d.actual, sort_keys=True) if isinstance(d.actual, (dict, list)) else str(d.actual)
            lines.append(f"     expected: {exp_s}")
            lines.append(f"     actual:   {act_s}")
    return "\n".join(lines)
