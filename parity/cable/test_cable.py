"""Unit tests for Action Cable replay harness, framing, diffing, and defect detection:
python3 -m unittest discover -s parity/cable -p 'test_*.py'
"""

import json
import os
import struct
import sys
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from client import RawWebSocketClient
from defects import (
    DEFECT_EXTRA_MESSAGE,
    DEFECT_MISSING_LAG_DISCONNECT,
    DEFECT_MISSING_MESSAGE,
    DEFECT_MODIFIED_PAYLOAD,
    DEFECT_WRONG_DISCONNECT_REASON,
    DEFECT_WRONG_OUTCOME,
    DEFECT_WRONG_RECONNECT_FLAG,
    inject_defect,
    verify_all_defects,
)
from diff import Difference, diff_frames, diff_sequences, format_diff_report
from normalizer import (
    canonical_identifier,
    group_frames_by_channel,
    normalize_frame,
    normalize_html_fragment,
    normalize_payload,
    normalize_sequence,
)
from scenarios import GOLDEN_SEQUENCES, replay_scenario


class NormalizerTests(unittest.TestCase):
    def test_canonical_identifier_sorting(self):
        raw = '{"room_id":1,"channel":"RoomChannel"}'
        expected = '{"channel":"RoomChannel","room_id":1}'
        self.assertEqual(canonical_identifier(raw), expected)

    def test_canonical_identifier_dict(self):
        d = {"room_id": 42, "channel": "PresenceChannel"}
        expected = '{"channel":"PresenceChannel","room_id":42}'
        self.assertEqual(canonical_identifier(d), expected)

    def test_pings_are_filtered(self):
        ping_frame = '{"type":"ping","message":1741234567}'
        self.assertIsNone(normalize_frame(ping_frame))

    def test_welcome_frame_normalization(self):
        welcome = '{"type":"welcome"}'
        self.assertEqual(normalize_frame(welcome), {"type": "welcome"})

    def test_confirm_subscription_normalization(self):
        confirm = '{"type":"confirm_subscription","identifier":"{\\"room_id\\":1,\\"channel\\":\\"RoomChannel\\"}"}'
        expected = {
            "type": "confirm_subscription",
            "identifier": '{"channel":"RoomChannel","room_id":1}',
        }
        self.assertEqual(normalize_frame(confirm), expected)

    def test_reject_subscription_normalization(self):
        reject = '{"type":"reject_subscription","identifier":"{\\"channel\\":\\"InvalidChannel\\"}"}'
        expected = {
            "type": "reject_subscription",
            "identifier": '{"channel":"InvalidChannel"}',
        }
        self.assertEqual(normalize_frame(reject), expected)

    def test_disconnect_frame_normalization(self):
        disconnect = '{"type":"disconnect","reason":"remote","reconnect":true}'
        expected = {"type": "disconnect", "reason": "remote", "reconnect": True}
        self.assertEqual(normalize_frame(disconnect), expected)

    def test_lag_disconnect_frame_normalization(self):
        disconnect = '{"type":"disconnect","reason":null,"reconnect":true}'
        expected = {"type": "disconnect", "reason": None, "reconnect": True}
        self.assertEqual(normalize_frame(disconnect), expected)

    def test_turbo_stream_payload_normalization(self):
        html = '<turbo-stream action="append" target="messages">  <template>  <div class="msg">Hi</div> </template> </turbo-stream>'
        frame = json.dumps({
            "identifier": '{"channel":"Turbo::StreamsChannel"}',
            "message": html,
        })
        norm = normalize_frame(frame)
        self.assertIn("message", norm)
        self.assertNotIn("  <template>", norm["message"])

    def test_group_frames_by_channel(self):
        frames = [
            '{"type":"welcome"}',
            '{"type":"confirm_subscription","identifier":"{\\"channel\\":\\"HeartbeatChannel\\"}"}',
            '{"identifier":"{\\"channel\\":\\"HeartbeatChannel\\"}","message":"beat"}',
            '{"type":"disconnect","reason":null,"reconnect":true}',
        ]
        grouped = group_frames_by_channel(frames)
        self.assertIn("(connection)", grouped)
        self.assertIn('{"channel":"HeartbeatChannel"}', grouped)
        self.assertEqual(len(grouped['{"channel":"HeartbeatChannel"}']), 2)


class DiffEngineTests(unittest.TestCase):
    def test_identical_sequences_match(self):
        frames = [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": '{"channel":"HeartbeatChannel"}'},
        ]
        diffs = diff_sequences(frames, frames)
        self.assertEqual(diffs, [])

    def test_type_mismatch_detected(self):
        exp = [{"type": "confirm_subscription", "identifier": '{"channel":"RoomChannel"}'}]
        act = [{"type": "reject_subscription", "identifier": '{"channel":"RoomChannel"}'}]
        diffs = diff_sequences(exp, act)
        self.assertEqual(len(diffs), 1)
        self.assertEqual(diffs[0].kind, "type-mismatch")

    def test_disconnect_reason_mismatch_detected(self):
        exp = [{"type": "disconnect", "reason": "remote", "reconnect": True}]
        act = [{"type": "disconnect", "reason": "unauthorized", "reconnect": True}]
        diffs = diff_sequences(exp, act)
        self.assertEqual(len(diffs), 1)
        self.assertEqual(diffs[0].kind, "disconnect-reason-mismatch")

    def test_disconnect_reconnect_flag_mismatch_detected(self):
        exp = [{"type": "disconnect", "reason": "remote", "reconnect": True}]
        act = [{"type": "disconnect", "reason": "remote", "reconnect": False}]
        diffs = diff_sequences(exp, act)
        self.assertEqual(len(diffs), 1)
        self.assertEqual(diffs[0].kind, "disconnect-reconnect-mismatch")

    def test_message_payload_divergence_detected(self):
        exp = [{"identifier": "x", "message": {"user_id": 1}}]
        act = [{"identifier": "x", "message": {"user_id": 2}}]
        diffs = diff_sequences(exp, act)
        self.assertEqual(len(diffs), 1)
        self.assertEqual(diffs[0].kind, "message-payload-mismatch")

    def test_frame_count_mismatch_detected(self):
        exp = [{"type": "welcome"}, {"type": "confirm_subscription", "identifier": "x"}]
        act = [{"type": "welcome"}]
        diffs = diff_sequences(exp, act)
        self.assertEqual(len(diffs), 1)
        self.assertEqual(diffs[0].kind, "count-mismatch")


class DefectDetectionTests(unittest.TestCase):
    def setUp(self):
        self.base_frames = [
            {"type": "welcome"},
            {"type": "confirm_subscription", "identifier": '{"channel":"RoomChannel","room_id":1}'},
            {"identifier": '{"channel":"RoomChannel","room_id":1}', "message": {"text": "hello"}},
            {"type": "disconnect", "reason": None, "reconnect": True},
        ]

    def test_all_defects_are_caught(self):
        results = verify_all_defects(self.base_frames)
        for kind, (caught, detail) in results.items():
            self.assertTrue(caught, f"Planted defect {kind} was not detected by diff engine: {detail}")

    def test_wrong_outcome_defect(self):
        defective = inject_defect(self.base_frames, DEFECT_WRONG_OUTCOME)
        diffs = diff_sequences(self.base_frames, defective)
        self.assertTrue(any(d.kind == "type-mismatch" for d in diffs))

    def test_missing_message_defect(self):
        defective = inject_defect(self.base_frames, DEFECT_MISSING_MESSAGE)
        diffs = diff_sequences(self.base_frames, defective)
        self.assertTrue(len(diffs) > 0)

    def test_modified_payload_defect(self):
        defective = inject_defect(self.base_frames, DEFECT_MODIFIED_PAYLOAD)
        diffs = diff_sequences(self.base_frames, defective)
        self.assertTrue(any(d.kind == "message-payload-mismatch" for d in diffs))

    def test_missing_lag_disconnect_defect(self):
        defective = inject_defect(self.base_frames, DEFECT_MISSING_LAG_DISCONNECT)
        diffs = diff_sequences(self.base_frames, defective)
        self.assertTrue(len(diffs) > 0)


class GoldenReplayParityTests(unittest.TestCase):
    def test_all_golden_scenarios_pass_replay(self):
        for name, golden in GOLDEN_SEQUENCES.items():
            res = replay_scenario(name, golden["frames"])
            self.assertTrue(res.success, f"Scenario {name} failed replay: {res.message}")
            self.assertEqual(len(res.differences), 0)

    def test_golden_reference_channels_file(self):
        golden_file = os.path.join(
            os.path.dirname(__file__), "..", "..", "reference-rust", "crates", "campfire", "src", "channels", "tests", "golden", "reference.json"
        )
        if not os.path.isfile(golden_file):
            self.skipTest(f"Golden file not found: {golden_file}")

        with open(golden_file, "r") as f:
            data = json.load(f)

        steps = data.get("steps", [])
        self.assertGreater(len(steps), 40)

        # Verify step frame sequences
        for s in steps:
            step_name = s.get("step")
            frames_map = s.get("frames", {})
            for socket_name, socket_frames in frames_map.items():
                norm = normalize_sequence(socket_frames)
                self.assertEqual(len(norm), len(socket_frames), f"Step {step_name} lost frames during normalization")


class RevocationAndLagTests(unittest.TestCase):
    def test_membership_revocation_sequence(self):
        res = replay_scenario("revocation_membership", GOLDEN_SEQUENCES["revocation_membership"]["frames"])
        self.assertTrue(res.success)
        disc_frame = res.actual_frames[-1]
        self.assertEqual(disc_frame["type"], "disconnect")
        self.assertEqual(disc_frame["reason"], "remote")
        self.assertTrue(disc_frame["reconnect"])

    def test_deactivation_revocation_sequence(self):
        res = replay_scenario("revocation_deactivate", GOLDEN_SEQUENCES["revocation_deactivate"]["frames"])
        self.assertTrue(res.success)
        disc_frame = res.actual_frames[-1]
        self.assertEqual(disc_frame["type"], "disconnect")
        self.assertEqual(disc_frame["reason"], "remote")
        self.assertFalse(disc_frame["reconnect"])

    def test_lag_disconnect_sequence(self):
        res = replay_scenario("lag_disconnect", GOLDEN_SEQUENCES["lag_disconnect"]["frames"])
        self.assertTrue(res.success)
        disc_frame = res.actual_frames[-1]
        self.assertEqual(disc_frame["type"], "disconnect")
        self.assertIsNone(disc_frame["reason"])
        self.assertTrue(disc_frame["reconnect"])


if __name__ == "__main__":
    unittest.main()
