"""Scenario definitions covering all channels, revocation, and lag disconnect."""

import json
import time
from dataclasses import dataclass
from typing import Any, Callable, Dict, List, Optional, Tuple, Union

try:
    from .client import RawWebSocketClient
    from .diff import Difference, diff_sequences
    from .normalizer import canonical_identifier, normalize_frame, normalize_sequence
except ImportError:
    from client import RawWebSocketClient
    from diff import Difference, diff_sequences
    from normalizer import canonical_identifier, normalize_frame, normalize_sequence


@dataclass
class ScenarioResult:
    name: str
    channel: str
    success: bool
    expected_frames: List[Dict[str, Any]]
    actual_frames: List[Dict[str, Any]]
    differences: List[Difference]
    message: str


# Golden vectors recorded from reference Rails Action Cable
GOLDEN_SEQUENCES: Dict[str, Dict[str, Any]] = {
    "HeartbeatChannel": {
        "channel": "HeartbeatChannel",
        "identifier": json.dumps({"channel": "HeartbeatChannel"}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "HeartbeatChannel"})},
        ],
    },
    "PresenceChannel_member": {
        "channel": "PresenceChannel",
        "identifier": json.dumps({"channel": "PresenceChannel", "room_id": 1}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "PresenceChannel", "room_id": 1})},
        ],
    },
    "PresenceChannel_non_member": {
        "channel": "PresenceChannel",
        "identifier": json.dumps({"channel": "PresenceChannel", "room_id": 9999}),
        "frames": [
            {"type": "welcome"},
            {"type": "reject_subscription", "identifier": json.dumps({"channel": "PresenceChannel", "room_id": 9999})},
        ],
    },
    "ReadRoomsChannel": {
        "channel": "ReadRoomsChannel",
        "identifier": json.dumps({"channel": "ReadRoomsChannel"}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "ReadRoomsChannel"})},
        ],
    },
    "RoomChannel_member": {
        "channel": "RoomChannel",
        "identifier": json.dumps({"channel": "RoomChannel", "room_id": 1}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "RoomChannel", "room_id": 1})},
        ],
    },
    "RoomChannel_non_member": {
        "channel": "RoomChannel",
        "identifier": json.dumps({"channel": "RoomChannel", "room_id": 9999}),
        "frames": [
            {"type": "welcome"},
            {"type": "reject_subscription", "identifier": json.dumps({"channel": "RoomChannel", "room_id": 9999})},
        ],
    },
    "RoomMessagesChannel_valid": {
        "channel": "RoomMessagesChannel",
        "identifier": json.dumps({"channel": "RoomMessagesChannel", "signed_stream_name": "valid_stream"}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "RoomMessagesChannel", "signed_stream_name": "valid_stream"})},
        ],
    },
    "RoomMessagesChannel_forged": {
        "channel": "RoomMessagesChannel",
        "identifier": json.dumps({"channel": "RoomMessagesChannel", "signed_stream_name": "forged_stream"}),
        "frames": [
            {"type": "welcome"},
            {"type": "reject_subscription", "identifier": json.dumps({"channel": "RoomMessagesChannel", "signed_stream_name": "forged_stream"})},
        ],
    },
    "TypingNotificationsChannel_member": {
        "channel": "TypingNotificationsChannel",
        "identifier": json.dumps({"channel": "TypingNotificationsChannel", "room_id": 1}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "TypingNotificationsChannel", "room_id": 1})},
        ],
    },
    "UnreadRoomsChannel": {
        "channel": "UnreadRoomsChannel",
        "identifier": json.dumps({"channel": "UnreadRoomsChannel"}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "UnreadRoomsChannel"})},
        ],
    },
    "TurboStreamsChannel_valid": {
        "channel": "Turbo::StreamsChannel",
        "identifier": json.dumps({"channel": "Turbo::StreamsChannel", "signed_stream_name": "valid_stream"}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "Turbo::StreamsChannel", "signed_stream_name": "valid_stream"})},
        ],
    },
    "TurboStreamsChannel_forged": {
        "channel": "Turbo::StreamsChannel",
        "identifier": json.dumps({"channel": "Turbo::StreamsChannel", "signed_stream_name": "forged_stream"}),
        "frames": [
            {"type": "welcome"},
            {"type": "reject_subscription", "identifier": json.dumps({"channel": "Turbo::StreamsChannel", "signed_stream_name": "forged_stream"})},
        ],
    },
    "ApplicationCable_base": {
        "channel": "ApplicationCable::Channel",
        "identifier": json.dumps({"channel": "ApplicationCable::Channel"}),
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "ApplicationCable::Channel"})},
        ],
    },
    "InvalidChannel": {
        "channel": "InvalidChannel",
        "identifier": json.dumps({"channel": "InvalidChannel"}),
        "frames": [
            {"type": "welcome"},
        ],
    },
    "unauthenticated": {
        "channel": "(connection)",
        "identifier": "(connection)",
        "frames": [
            {"type": "disconnect", "reason": "unauthorized", "reconnect": False},
        ],
    },
    "revocation_membership": {
        "channel": "(revocation)",
        "identifier": "(revocation)",
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "RoomChannel", "room_id": 1})},
            {"type": "disconnect", "reason": "remote", "reconnect": True},
        ],
    },
    "revocation_deactivate": {
        "channel": "(revocation)",
        "identifier": "(revocation)",
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "HeartbeatChannel"})},
            {"type": "disconnect", "reason": "remote", "reconnect": False},
        ],
    },
    "revocation_ban": {
        "channel": "(revocation)",
        "identifier": "(revocation)",
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "HeartbeatChannel"})},
            {"type": "disconnect", "reason": "remote", "reconnect": False},
        ],
    },
    "lag_disconnect": {
        "channel": "(lag)",
        "identifier": "(lag)",
        "frames": [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": json.dumps({"channel": "RoomChannel", "room_id": 1})},
            {"type": "disconnect", "reason": None, "reconnect": True},
        ],
    },
}


def run_live_scenario(
    name: str,
    ref_client_factory: Callable[[], RawWebSocketClient],
    cand_client_factory: Callable[[], RawWebSocketClient],
    action_fn: Callable[[RawWebSocketClient, RawWebSocketClient], Tuple[List[str], List[str]]],
) -> ScenarioResult:
    """Runs a scenario against live reference and candidate servers and diffs frame sequences."""
    client_ref = ref_client_factory()
    client_cand = cand_client_factory()

    try:
        raw_ref_frames, raw_cand_frames = action_fn(client_ref, client_cand)
    finally:
        client_ref.close()
        client_cand.close()

    norm_ref = normalize_sequence(raw_ref_frames)
    norm_cand = normalize_sequence(raw_cand_frames)
    diffs = diff_sequences(norm_ref, norm_cand, channel=name)

    success = len(diffs) == 0
    msg = f"Passed: {len(norm_cand)} frame(s) matched reference" if success else f"Failed: {len(diffs)} difference(s)"
    return ScenarioResult(
        name=name,
        channel=name,
        success=success,
        expected_frames=norm_ref,
        actual_frames=norm_cand,
        differences=diffs,
        message=msg,
    )


def replay_scenario(name: str, actual_frames: List[Union[str, Dict[str, Any]]]) -> ScenarioResult:
    """Verifies actual frame sequence against golden reference vectors."""
    if name not in GOLDEN_SEQUENCES:
        raise KeyError(f"Unknown scenario {name}")

    golden = GOLDEN_SEQUENCES[name]
    expected_frames = golden["frames"]
    norm_actual = [f if isinstance(f, dict) else normalize_frame(f) for f in actual_frames]
    clean_actual = [f for f in norm_actual if f is not None]

    diffs = diff_sequences(expected_frames, clean_actual, channel=golden["channel"])
    success = len(diffs) == 0
    msg = f"Replay matched reference: {len(clean_actual)} frames" if success else f"Replay diverged: {len(diffs)} differences"
    return ScenarioResult(
        name=name,
        channel=golden["channel"],
        success=success,
        expected_frames=expected_frames,
        actual_frames=clean_actual,
        differences=diffs,
        message=msg,
    )
