"""Live Action Cable comparison: the same steps against the reference and the candidate.

`parity/cable/run REF CAND` used to replay the checked-in golden sequences against themselves
and ignore the two URLs. These scenarios open both servers, so a pass is parity with edbc779.
"""

import json
import os
import urllib.error
import urllib.parse
import urllib.request

from client import RawWebSocketClient
from diff import diff_sequences, format_diff_report
from normalizer import normalize_sequence
from scenarios import GOLDEN_SEQUENCES, ScenarioResult

# Side-effect scenarios (revocation, lag) need a second client and a timed disconnect.
# They stay out of the subscribe pass; message_create covers the unread fan-out broadcast.
SUBSCRIBE_SKIP = {"revocation_membership", "revocation_deactivate", "revocation_ban", "lag_disconnect"}


def session_cookie(base_url, email, password):
    """POST /session and return the Cookie header value. Rails accepts the post with no token."""
    data = urllib.parse.urlencode({"email_address": email, "password": password}).encode()
    req = urllib.request.Request(base_url.rstrip("/") + "/session", data=data, method="POST")
    opener = urllib.request.build_opener(_NoRedirect())
    try:
        resp = opener.open(req, timeout=20)
    except urllib.error.HTTPError as error:
        resp = error
    pieces = []
    for header in resp.headers.get_all("Set-Cookie") or []:
        pieces.append(header.split(";", 1)[0])
    if not pieces:
        raise RuntimeError(f"sign-in at {base_url} set no cookie (HTTP {getattr(resp, 'status', getattr(resp, 'code', '?'))})")
    return "; ".join(pieces)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _ws_url(base_url):
    parsed = urllib.parse.urlparse(base_url)
    scheme = "wss" if parsed.scheme == "https" else "ws"
    return urllib.parse.urlunparse((scheme, parsed.netloc, "/cable", "", "", ""))


def _origin(base_url):
    parsed = urllib.parse.urlparse(base_url)
    return f"{parsed.scheme}://{parsed.netloc}"


def _collect(base_url, cookie, identifier, settle=0.6):
    client = RawWebSocketClient(_ws_url(base_url), cookie=cookie, origin=_origin(base_url), timeout=5)
    try:
        client.connect()
        if identifier is not None:
            client.subscribe(identifier)
        return client.drain(timeout=settle)
    finally:
        client.close()


def _result(name, ref_frames, cand_frames):
    norm_ref = normalize_sequence(ref_frames)
    norm_cand = normalize_sequence(cand_frames)
    diffs = diff_sequences(norm_ref, norm_cand, channel=name)
    success = not diffs
    message = f"Passed: {len(norm_cand)} frame(s) matched reference" if success else f"Failed: {len(diffs)} difference(s)"
    return ScenarioResult(name, name, success, norm_ref, norm_cand, diffs, message), diffs


def run_live(reference_url, candidate_url, email, password, channel_filter=None):
    """Subscribe on both servers and compare the frames. Returns (ok, results)."""
    ref_cookie = session_cookie(reference_url, email, password)
    cand_cookie = session_cookie(candidate_url, email, password)
    results = []
    all_ok = True

    for name, golden in sorted(GOLDEN_SEQUENCES.items()):
        if name in SUBSCRIBE_SKIP:
            continue
        channel = golden["channel"]
        if channel_filter and channel_filter.lower() not in channel.lower() and channel_filter.lower() not in name.lower():
            continue
        identifier = None if name == "unauthenticated" else golden["identifier"]
        cookie_ref = None if name == "unauthenticated" else ref_cookie
        cookie_cand = None if name == "unauthenticated" else cand_cookie
        ref_frames = _collect(reference_url, cookie_ref, identifier)
        cand_frames = _collect(candidate_url, cookie_cand, identifier)
        result, diffs = _result(name, ref_frames, cand_frames)
        results.append(result)
        status = "PASS" if result.success else "FAIL"
        print(f"  [{status}] {name:32} ({channel}): {result.message}")
        if diffs:
            all_ok = False
            print(format_diff_report(diffs))

    # A message fans out on UnreadRoomsChannel to the other members (U04). The poster does not
    # get that frame, so listen as Kevin and post as David into the seeded HQ room.
    if channel_filter is None or "unread" in channel_filter.lower() or "message" in channel_filter.lower():
        kevin = _kevin_email()
        ref_listener = session_cookie(reference_url, kevin, password)
        cand_listener = session_cookie(candidate_url, kevin, password)
        ref_frames = _message_frames(reference_url, ref_cookie, ref_listener)
        cand_frames = _message_frames(candidate_url, cand_cookie, cand_listener)
        result, diffs = _result("message_create_unread", ref_frames, cand_frames)
        results.append(result)
        status = "PASS" if result.success else "FAIL"
        print(f"  [{status}] {'message_create_unread':32} (UnreadRoomsChannel): {result.message}")
        if diffs:
            all_ok = False
            print(format_diff_report(diffs))

    return all_ok, results


def _labels():
    labels = os.path.join(os.path.dirname(__file__), "..", ".seed", "default", "labels.json")
    with open(labels) as handle:
        return json.load(handle)


def _hq_room_id():
    """The seeded HQ room. Goldens say room 1; this pin's rooms are snowflakes."""
    return _labels()["rooms.hq"]


def _kevin_email():
    return _labels()["emails.kevin"]


def _message_frames(base_url, poster_cookie, listener_cookie):
    """Kevin listens for unreads while David posts in the HQ room."""
    client = RawWebSocketClient(_ws_url(base_url), cookie=listener_cookie, origin=_origin(base_url), timeout=5)
    try:
        client.connect()
        client.subscribe(json.dumps({"channel": "UnreadRoomsChannel"}))
        client.drain(timeout=0.4)
        _post_message(base_url, poster_cookie, room_id=_hq_room_id(), body="q11 cable parity")
        return client.drain(timeout=3.0)
    finally:
        client.close()


def _post_message(base_url, cookie, room_id, body):
    data = urllib.parse.urlencode({
        "message[body]": body,
        "message[client_message_id]": "q11-cable-parity",
    }).encode()
    req = urllib.request.Request(
        base_url.rstrip("/") + f"/rooms/{room_id}/messages",
        data=data,
        method="POST",
        headers={"Cookie": cookie, "Accept": "text/vnd.turbo-stream.html"},
    )
    opener = urllib.request.build_opener(_NoRedirect())
    try:
        opener.open(req, timeout=20)
    except urllib.error.HTTPError:
        pass


def default_identity(root):
    labels = os.path.join(root, "parity", ".seed", "default", "labels.json")
    with open(labels) as handle:
        data = json.load(handle)
    email = data.get("emails.david") or data["emails"]["david"]
    password = data.get("passwords.all") or "secret123456"
    return email, password
