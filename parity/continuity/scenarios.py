"""Continuity scenarios exercising cross-server session, form, and transfer compatibility."""

import re
import urllib.parse
import uuid
from typing import Callable, Dict, List, Tuple

try:
    from .continuity import Client, ScenarioResult
except ImportError:
    from continuity import Client, ScenarioResult

STREAM_ACCEPT = {"Accept": "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"}


def run_session_continuity(client_a: Client, client_b: Client, direction: str) -> ScenarioResult:
    """Scenario 1: Sign in on Server A, continue on Server B, sign out on Server B."""
    # 1. Sign in on Server A as David
    res_login = client_a.login("david@37signals.com", "secret123456")
    if res_login.status != 302:
        return ScenarioResult("session_continuity", direction, False, f"Sign-in on server A failed with {res_login.status}")

    # 2. Transfer cookies to client for Server B
    client_b.cookies = dict(client_a.cookies)

    # 3. Access root on Server B - should redirect to authenticated room (302)
    res_root = client_b.get("/")
    if res_root.status != 302 or not res_root.location or "/session/new" in res_root.location:
        return ScenarioResult("session_continuity", direction, False,
                              f"Server B did not recognize session on root: status={res_root.status}, loc={res_root.location}")

    # 4. Access room on Server B - should return 200 OK
    room_path = res_root.location
    if room_path.startswith("http"):
        room_path = "/" + "/".join(room_path.split("/")[3:])
    res_room = client_b.get(room_path)
    if res_room.status != 200:
        return ScenarioResult("session_continuity", direction, False,
                              f"Server B returned {res_room.status} for room {room_path}")

    # 5. Access profile on Server B - should return 200 OK and show David
    res_profile = client_b.get("/users/me/profile")
    if res_profile.status != 200 or "david@37signals.com" not in res_profile.text:
        return ScenarioResult("session_continuity", direction, False,
                              f"Server B profile page failed: status={res_profile.status}, contains david={'david@37signals.com' in res_profile.text}")

    # 6. Sign out on Server B
    token_signout = Client.extract_form_token(res_room.text, "/session") or Client.extract_meta_token(res_room.text)
    res_signout = client_b.form_submit("delete", "/session", token_signout)
    if res_signout.status != 302:
        return ScenarioResult("session_continuity", direction, False, f"Sign-out on Server B returned {res_signout.status}")

    # 7. Subsequent request to Server A with invalidated session cookie should be unauthenticated
    client_a.cookies = dict(client_b.cookies)
    res_check = client_a.get("/")
    if res_check.status != 302 or (res_check.location and "/session/new" not in res_check.location):
        return ScenarioResult("session_continuity", direction, False,
                              f"Session was not invalidated on Server A after Server B sign-out: status={res_check.status}, loc={res_check.location}")

    return ScenarioResult("session_continuity", direction, True,
                          "Successfully signed in on Server A, browsed authenticated pages on Server B, and signed out across servers.")


def run_form_login_continuity(client_a: Client, client_b: Client, direction: str) -> ScenarioResult:
    """Scenario 2: Render unauthenticated login form on Server A, submit credentials to Server B."""
    # 1. Render /session/new on Server A
    res_form = client_a.get("/session/new")
    if res_form.status != 200:
        return ScenarioResult("form_login_continuity", direction, False, f"GET /session/new on Server A returned {res_form.status}")

    token = Client.extract_form_token(res_form.text, "/session")
    if not token:
        return ScenarioResult("form_login_continuity", direction, False, "Could not extract authenticity_token from Server A form")

    # 2. Transfer session cookie from Server A to Server B client
    client_b.cookies = dict(client_a.cookies)

    # 3. Submit form to Server B
    res_submit = client_b.post("/session", {
        "authenticity_token": token,
        "email_address": "kevin@37signals.com",
        "password": "secret123456",
    })

    if res_submit.status != 302 or not res_submit.location:
        return ScenarioResult("form_login_continuity", direction, False,
                              f"POST /session on Server B returned {res_submit.status}, loc={res_submit.location}")

    # 4. Verify Server B issued session_token cookie and client is signed in
    if "session_token" not in client_b.cookies:
        return ScenarioResult("form_login_continuity", direction, False, "Server B did not set session_token cookie upon login")

    res_me = client_b.get("/users/me/profile")
    if res_me.status != 200 or "kevin@37signals.com" not in res_me.text:
        return ScenarioResult("form_login_continuity", direction, False,
                              f"Signed-in check on Server B failed: status={res_me.status}, contains kevin={'kevin@37signals.com' in res_me.text}")

    return ScenarioResult("form_login_continuity", direction, True,
                          "Successfully rendered login form on Server A and submitted authenticated session creation to Server B.")


def run_form_message_continuity(client_a: Client, client_b: Client, direction: str) -> ScenarioResult:
    """Scenario 3: Render authenticated message form on Server A, post message to Server B."""
    # Sign in David on Server A
    client_a.login("david@37signals.com", "secret123456")
    client_b.cookies = dict(client_a.cookies)

    room_id = "104393281"
    room_path = f"/rooms/{room_id}"

    # 1. Render room page on Server A to extract per-form authenticity token
    res_room = client_a.get(room_path)
    if res_room.status != 200:
        return ScenarioResult("form_message_continuity", direction, False, f"GET {room_path} on Server A returned {res_room.status}")

    token = Client.extract_form_token(res_room.text, f"{room_path}/messages")
    if not token:
        # Fall back to meta token if per-form token not distinct
        token = Client.extract_meta_token(res_room.text)
    if not token:
        return ScenarioResult("form_message_continuity", direction, False, f"Could not extract authenticity token for {room_path}/messages")

    # 2. Submit message to Server B using Server A's token & cookies
    msg_id = f"cross-server-{uuid.uuid4().hex[:12]}"
    msg_body = f"<div>Cross-server continuity post: {direction} ({msg_id})</div>"

    res_post = client_b.request("POST", f"{room_path}/messages",
                                body=f"authenticity_token={urllib.parse.quote(token)}&message%5Bbody%5D={urllib.parse.quote(msg_body)}&message%5Bclient_message_id%5D={msg_id}".encode("utf-8"),
                                headers={
                                    "Content-Type": "application/x-www-form-urlencoded",
                                    **STREAM_ACCEPT,
                                })

    if res_post.status not in (200, 302):
        return ScenarioResult("form_message_continuity", direction, False,
                              f"POST {room_path}/messages on Server B failed with status {res_post.status}")

    # 3. Verify message is visible on Server A
    res_check = client_a.get(f"{room_path}/messages")
    if res_check.status != 200 or msg_id not in res_check.text:
        return ScenarioResult("form_message_continuity", direction, False,
                              f"Message {msg_id} posted on Server B not visible on Server A: status={res_check.status}")

    return ScenarioResult("form_message_continuity", direction, True,
                          "Successfully rendered message form on Server A and posted message to Server B with cross-server verification.")


def run_form_profile_update_continuity(client_a: Client, client_b: Client, direction: str) -> ScenarioResult:
    """Scenario 4: Render profile form on Server A, submit update to Server B."""
    # Sign in Kevin on Server A
    client_a.login("kevin@37signals.com", "secret123456")
    client_b.cookies = dict(client_a.cookies)

    # 1. Get profile page on Server A
    res_prof = client_a.get("/users/me/profile")
    if res_prof.status != 200:
        return ScenarioResult("form_profile_update_continuity", direction, False, f"GET profile on Server A returned {res_prof.status}")

    # Extract user ID and CSRF token
    m_uid = re.search(r'action="[^"]*/users/(\d+)/profile"', res_prof.text)
    user_id = m_uid.group(1) if m_uid else "712064548"
    update_path = f"/users/{user_id}/profile"

    token = Client.extract_form_token(res_prof.text, update_path) or Client.extract_meta_token(res_prof.text)
    if not token:
        return ScenarioResult("form_profile_update_continuity", direction, False, "Could not extract CSRF token for profile update")

    # 2. Submit update to Server B
    new_bio = f"Programmer continuity update {uuid.uuid4().hex[:6]}"
    res_update = client_b.form_submit("patch", update_path, token, {"user[bio]": new_bio})
    if res_update.status not in (200, 302):
        return ScenarioResult("form_profile_update_continuity", direction, False,
                              f"PATCH {update_path} on Server B returned {res_update.status}")

    # 3. Verify on Server A
    res_verify = client_a.get("/users/me/profile")
    if res_verify.status != 200 or new_bio not in res_verify.text:
        return ScenarioResult("form_profile_update_continuity", direction, False,
                              f"Profile update on Server B not reflected on Server A: status={res_verify.status}")

    return ScenarioResult("form_profile_update_continuity", direction, True,
                          "Successfully rendered profile edit on Server A, updated bio on Server B, and verified on Server A.")


def run_transfer_link_continuity(client_a: Client, client_b: Client, direction: str) -> ScenarioResult:
    """Scenario 5: Mint 4-hour session transfer link on Server A, redeem on Server B."""
    # 1. Sign in as Kevin on Server A
    client_a.login("kevin@37signals.com", "secret123456")

    # 2. Mint transfer link via profile page
    res_prof = client_a.get("/users/me/profile")
    if res_prof.status != 200:
        return ScenarioResult("transfer_link_continuity", direction, False, f"GET profile on Server A returned {res_prof.status}")

    transfer_token = Client.extract_transfer_token(res_prof.text)
    if not transfer_token:
        return ScenarioResult("transfer_link_continuity", direction, False, "Could not extract transfer token from Server A profile page")

    transfer_path = f"/session/transfers/{transfer_token}"

    # 3. New unauthenticated browser on Server B accesses the transfer link
    fresh_client_b = client_b.new_session(ip=f"203.0.113.{hash(direction) % 200 + 10}")
    res_trans_page = fresh_client_b.get(transfer_path)
    if res_trans_page.status != 200:
        return ScenarioResult("transfer_link_continuity", direction, False,
                              f"GET {transfer_path} on Server B returned {res_trans_page.status}")

    # 4. Redeem transfer on Server B via PUT /session/transfers/:token
    trans_csrf = Client.extract_form_token(res_trans_page.text, transfer_path) or Client.extract_meta_token(res_trans_page.text)
    res_redeem = fresh_client_b.form_submit("put", transfer_path, trans_csrf)
    if res_redeem.status != 302 or not res_redeem.location:
        return ScenarioResult("transfer_link_continuity", direction, False,
                              f"Redeem PUT {transfer_path} on Server B returned {res_redeem.status}, loc={res_redeem.location}")

    if "session_token" not in fresh_client_b.cookies:
        return ScenarioResult("transfer_link_continuity", direction, False, "Server B did not set session_token cookie after transfer redemption")

    # 5. Verify the redeemed session is Kevin on Server B
    res_me = fresh_client_b.get("/users/me/profile")
    if res_me.status != 200 or "kevin@37signals.com" not in res_me.text:
        return ScenarioResult("transfer_link_continuity", direction, False,
                              f"Transferred session on Server B failed profile check: status={res_me.status}, contains kevin={'kevin@37signals.com' in res_me.text}")

    return ScenarioResult("transfer_link_continuity", direction, True,
                          "Successfully minted transfer link on Server A, redeemed on Server B, and authenticated as target user.")


def run_security_csrf_tamper_continuity(client_a: Client, client_b: Client, direction: str) -> ScenarioResult:
    """Scenario 6: Cross-server tampered authenticity token is rejected with 422."""
    res_form = client_a.get("/session/new")
    token = Client.extract_form_token(res_form.text, "/session") or "bogus"
    tampered_token = token[:-4] + "xxxx" if len(token) > 4 else "tampered_csrf_token"

    client_b.cookies = dict(client_a.cookies)
    res_submit = client_b.post("/session", {
        "authenticity_token": tampered_token,
        "email_address": "david@37signals.com",
        "password": "secret123456",
    })

    if res_submit.status != 422:
        return ScenarioResult("security_csrf_tamper_continuity", direction, False,
                              f"Server B failed to reject tampered CSRF token: expected 422, got {res_submit.status}")

    return ScenarioResult("security_csrf_tamper_continuity", direction, True,
                          "Server B correctly rejected cross-server tampered CSRF token with 422 Unprocessable Entity.")


def run_security_transfer_tamper_continuity(client_a: Client, client_b: Client, direction: str) -> ScenarioResult:
    """Scenario 7: Cross-server tampered transfer link is rejected with 400 Bad Request."""
    client_a.login("kevin@37signals.com", "secret123456")
    res_prof = client_a.get("/users/me/profile")
    transfer_token = Client.extract_transfer_token(res_prof.text) or "bogus"
    tampered_transfer = transfer_token[:-4] + "yyyy" if len(transfer_token) > 4 else "tampered_transfer_token"

    fresh_client_b = client_b.new_session()
    res_tamper_page = fresh_client_b.get(f"/session/transfers/{tampered_transfer}")
    trans_csrf = Client.extract_form_token(res_tamper_page.text, f"/session/transfers/{tampered_transfer}") or Client.extract_meta_token(res_tamper_page.text)
    res_tamper = fresh_client_b.form_submit("put", f"/session/transfers/{tampered_transfer}", trans_csrf)

    if res_tamper.status != 400:
        return ScenarioResult("security_transfer_tamper_continuity", direction, False,
                              f"Server B failed to reject tampered transfer token: expected 400, got {res_tamper.status}")

    return ScenarioResult("security_transfer_tamper_continuity", direction, True,
                          "Server B correctly rejected cross-server tampered transfer token with 400 Bad Request.")


SCENARIOS: List[Tuple[str, Callable[[Client, Client, str], ScenarioResult], str]] = [
    ("session_continuity", run_session_continuity, "Sign in on Server A, continue on Server B, sign out across servers"),
    ("form_login_continuity", run_form_login_continuity, "Render login form on Server A, submit credentials to Server B"),
    ("form_message_continuity", run_form_message_continuity, "Render message form on Server A, post message to Server B"),
    ("form_profile_update_continuity", run_form_profile_update_continuity, "Render profile edit on Server A, submit update to Server B"),
    ("transfer_link_continuity", run_transfer_link_continuity, "Mint 4h transfer link on Server A, redeem and continue on Server B"),
    ("security_csrf_tamper_continuity", run_security_csrf_tamper_continuity, "Tampered cross-server CSRF token rejected with 422"),
    ("security_transfer_tamper_continuity", run_security_transfer_tamper_continuity, "Tampered cross-server transfer link rejected with 400"),
]
