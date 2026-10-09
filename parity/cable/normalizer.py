"""Action Cable frame normalizer matching reference-rust/parity/capture/network.ts."""

import json
import os
import re
import sys
from typing import Any, Dict, List, Optional, Tuple, Union

# Import HTML normalizer from parity/normalize if available
NORMALIZE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "normalize"))
if os.path.isdir(NORMALIZE_DIR) and NORMALIZE_DIR not in sys.path:
    sys.path.insert(0, NORMALIZE_DIR)

try:
    import normalize as html_normalizer
except ImportError:
    html_normalizer = None


def canonical_identifier(identifier: Union[str, Dict[str, Any]]) -> str:
    """Canonicalizes Action Cable channel identifier into stable sorted JSON."""
    if isinstance(identifier, dict):
        return json.dumps(identifier, sort_keys=True, separators=(",", ":"))
    try:
        parsed = json.loads(identifier)
        if isinstance(parsed, dict):
            return json.dumps(parsed, sort_keys=True, separators=(",", ":"))
    except (json.JSONDecodeError, TypeError):
        pass
    return str(identifier)


def normalize_html_fragment(html_str: str) -> str:
    """Normalizes HTML fragment (e.g. Turbo Streams markup)."""
    if not html_str:
        return ""
    if html_normalizer and hasattr(html_normalizer, "normalize_response"):
        try:
            return html_normalizer.normalize_response(html_str.encode("utf-8"), "text/html").decode("utf-8").strip()
        except Exception:
            pass

    # Fallback clean normalization: collapse consecutive whitespace, strip empty lines
    text = re.sub(r">\s+<", "><", html_str.strip())
    text = re.sub(r"\s+", " ", text)
    return text


def normalize_payload(data: Any) -> Any:
    """Normalizes a message payload (string, dict, or primitive)."""
    if isinstance(data, str):
        if "<turbo-stream" in data:
            return normalize_html_fragment(data)
        try:
            parsed = json.loads(data)
            return normalize_payload(parsed)
        except (json.JSONDecodeError, TypeError):
            return data.strip()
    elif isinstance(data, dict):
        return {k: normalize_payload(v) for k, v in sorted(data.items())}
    elif isinstance(data, list):
        return [normalize_payload(x) for x in data]
    return data


def normalize_frame(frame_text: str) -> Optional[Dict[str, Any]]:
    """Normalizes a single Action Cable frame JSON string.

    Returns None for pings. Returns standardized dictionary for all other frames.
    """
    try:
        obj = json.loads(frame_text)
    except (json.JSONDecodeError, TypeError):
        return {"raw": frame_text}

    if not isinstance(obj, dict):
        return {"raw": frame_text}

    frame_type = obj.get("type")
    if frame_type == "ping":
        return None

    norm: Dict[str, Any] = {}
    if frame_type:
        norm["type"] = frame_type

    if "identifier" in obj:
        norm["identifier"] = canonical_identifier(obj["identifier"])

    if "reason" in obj:
        norm["reason"] = obj["reason"]

    if "reconnect" in obj:
        norm["reconnect"] = obj["reconnect"]

    if "message" in obj:
        norm["message"] = normalize_payload(obj["message"])

    return norm


def normalize_sequence(frames: List[str]) -> List[Dict[str, Any]]:
    """Normalizes an ordered list of raw frames, filtering pings."""
    res = []
    for f in frames:
        norm = normalize_frame(f)
        if norm is not None:
            res.append(norm)
    return res


def group_frames_by_channel(frames: List[str]) -> Dict[str, List[Dict[str, Any]]]:
    """Groups normalized frames by channel/identifier or '(connection)'."""
    grouped: Dict[str, List[Dict[str, Any]]] = {}
    for f in frames:
        norm = normalize_frame(f)
        if norm is None:
            continue
        key = norm.get("identifier") or "(connection)"
        grouped.setdefault(key, []).append(norm)
    return grouped
