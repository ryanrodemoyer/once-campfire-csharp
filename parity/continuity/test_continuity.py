"""Offline unit tests for cross-server continuity harness and scenario runners:
python3 -m unittest discover -s parity/continuity -p 'test_*.py'
"""

import os
import sys
import unittest
from typing import Dict, List, Optional, Tuple

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import continuity  # noqa: E402
from continuity import Client, Response, is_cookie_deletion, parse_cookie  # noqa: E402
import scenarios  # noqa: E402


class CookieParsingTests(unittest.TestCase):
    def test_parse_cookie_attributes(self):
        cookie = "session_token=abc123xyz; path=/; HttpOnly; SameSite=Lax; Max-Age=1209600"
        name, val, attrs = parse_cookie(cookie)
        self.assertEqual(name, "session_token")
        self.assertEqual(val, "abc123xyz")
        self.assertEqual(attrs.get("path"), "/")
        self.assertIn("httponly", attrs)
        self.assertEqual(attrs.get("samesite"), "Lax")
        self.assertEqual(attrs.get("max-age"), "1209600")

    def test_is_cookie_deletion(self):
        self.assertTrue(is_cookie_deletion("session_token=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT"))
        self.assertTrue(is_cookie_deletion("session_token=deleted; path=/; max-age=0"))
        self.assertTrue(is_cookie_deletion("session_token=; path=/"))
        self.assertFalse(is_cookie_deletion("session_token=real_token_1970_text; path=/; max-age=3600"))


class TokenExtractionTests(unittest.TestCase):
    def test_extract_form_token(self):
        html = """
        <html><body>
          <form action="/other" method="post">
            <input type="hidden" name="authenticity_token" value="other_token" />
          </form>
          <form action="/session" method="post">
            <input type="hidden" name="authenticity_token" value="session_csrf_12345" />
            <input type="text" name="email" />
          </form>
        </body></html>
        """
        token = Client.extract_form_token(html, "/session")
        self.assertEqual(token, "session_csrf_12345")

    def test_extract_form_token_with_full_url(self):
        html = """
        <form action="http://campfire.test/rooms/1/messages" method="post">
            <input type="hidden" name="authenticity_token" value="room_token_abc" />
        </form>
        """
        token = Client.extract_form_token(html, "/rooms/1/messages")
        self.assertEqual(token, "room_token_abc")

    def test_extract_meta_token(self):
        html = '<html><head><meta name="csrf-token" content="meta_csrf_xyz987" /></head></html>'
        self.assertEqual(Client.extract_meta_token(html), "meta_csrf_xyz987")

    def test_extract_transfer_token(self):
        html = """
        <div class="transfer-section">
            <a href="/session/transfers/eyJfcmFpbHMiOnsiZGF0YSI6MTIzfX0">Transfer Session</a>
        </div>
        """
        token = Client.extract_transfer_token(html)
        self.assertEqual(token, "eyJfcmFpbHMiOnsiZGF0YSI6MTIzfX0")


class MockClient(Client):
    """Mock client for testing scenario logic without live HTTP servers."""

    def __init__(self, responses: Optional[Dict[Tuple[str, str], Response]] = None):
        super().__init__("http://127.0.0.1:9999")
        self.mock_responses = responses or {}
        self.history: List[Tuple[str, str, Optional[bytes]]] = []

    def new_session(self, ip: Optional[str] = None) -> "MockClient":
        client = MockClient(self.mock_responses)
        client.ip = ip or self.ip
        return client

    def request(self, method: str, path: str, body: Optional[bytes] = None, headers: Optional[Dict[str, str]] = None) -> Response:
        self.history.append((method, path, body))
        key = (method, path)
        if key in self.mock_responses:
            resp = self.mock_responses[key]
            for c in resp.set_cookies():
                name, val, _ = parse_cookie(c)
                if is_cookie_deletion(c):
                    self.cookies.pop(name, None)
                else:
                    self.cookies[name] = val
            return resp
        # Default 404
        return Response(404, [], b"Not found")


class ScenariosMockedTests(unittest.TestCase):
    def test_session_continuity_success(self):
        client_a = MockClient({
            ("GET", "/session/new"): Response(200, [("Set-Cookie", "_campfire_session=sess_a; path=/")],
                                             b'<form action="/session"><input name="authenticity_token" value="tok_a"/></form>'),
            ("POST", "/session"): Response(302, [("Set-Cookie", "session_token=st_david; path=/"),
                                                ("Location", "http://campfire.test/")], b""),
            ("GET", "/"): Response(302, [("Location", "http://campfire.test/session/new")], b""),
        })

        client_b = MockClient({
            ("GET", "/"): Response(302, [("Location", "http://campfire.test/rooms/104393281")], b""),
            ("GET", "/rooms/104393281"): Response(200, [], b'<div>Room 1 <form action="/session"><input name="authenticity_token" value="signout_tok"/></form></div>'),
            ("GET", "/users/me/profile"): Response(200, [], b'<div>david@37signals.com</div>'),
            ("POST", "/session"): Response(302, [("Set-Cookie", "session_token=; max-age=0; path=/"),
                                                ("Location", "http://campfire.test/session/new")], b""),
        })

        res = scenarios.run_session_continuity(client_a, client_b, "rails_to_csharp")
        self.assertTrue(res.passed, res.message)

    def test_session_continuity_detected_defect(self):
        # Client B refuses the session from Client A
        client_a = MockClient({
            ("GET", "/session/new"): Response(200, [], b'<form action="/session"><input name="authenticity_token" value="t"/></form>'),
            ("POST", "/session"): Response(302, [("Set-Cookie", "session_token=st_david; path=/")], b""),
        })
        client_b = MockClient({
            ("GET", "/"): Response(302, [("Location", "http://campfire.test/session/new")], b""),
        })

        res = scenarios.run_session_continuity(client_a, client_b, "rails_to_csharp")
        self.assertFalse(res.passed)
        self.assertIn("Server B did not recognize session", res.message)

    def test_form_login_continuity_success(self):
        client_a = MockClient({
            ("GET", "/session/new"): Response(200, [("Set-Cookie", "_campfire_session=enc_sess_1; path=/")],
                                             b'<form action="/session"><input name="authenticity_token" value="form_csrf_tok"/></form>'),
        })
        client_b = MockClient({
            ("POST", "/session"): Response(302, [("Set-Cookie", "session_token=st_kevin; path=/"),
                                                ("Location", "http://campfire.test/")], b""),
            ("GET", "/users/me/profile"): Response(200, [], b'<div>kevin@37signals.com</div>'),
        })

        res = scenarios.run_form_login_continuity(client_a, client_b, "rails_to_csharp")
        self.assertTrue(res.passed, res.message)

    def test_transfer_link_continuity_success(self):
        client_a = MockClient({
            ("GET", "/session/new"): Response(200, [], b'<form action="/session"><input name="authenticity_token" value="t"/></form>'),
            ("POST", "/session"): Response(302, [("Set-Cookie", "session_token=st_kevin; path=/")], b""),
            ("GET", "/users/me/profile"): Response(200, [], b'<a href="/session/transfers/transfer_token_valid_123">Transfer</a>'),
        })
        client_b = MockClient({
            ("GET", "/session/transfers/transfer_token_valid_123"): Response(200, [],
                                                                            b'<form action="/session/transfers/transfer_token_valid_123"><input name="authenticity_token" value="put_csrf"/></form>'),
            ("POST", "/session/transfers/transfer_token_valid_123"): Response(302, [("Set-Cookie", "session_token=new_st_kevin; path=/"),
                                                                                   ("Location", "http://campfire.test/")], b""),
            ("GET", "/users/me/profile"): Response(200, [], b'<div>kevin@37signals.com</div>'),
        })

        res = scenarios.run_transfer_link_continuity(client_a, client_b, "rails_to_csharp")
        self.assertTrue(res.passed, res.message)

    def test_security_tamper_defects(self):
        # Tampered CSRF returns 422 as expected
        client_a = MockClient({
            ("GET", "/session/new"): Response(200, [], b'<form action="/session"><input name="authenticity_token" value="good_token_123"/></form>'),
        })
        client_b = MockClient({
            ("POST", "/session"): Response(422, [], b"Invalid Authenticity Token"),
        })

        res_csrf = scenarios.run_security_csrf_tamper_continuity(client_a, client_b, "rails_to_csharp")
        self.assertTrue(res_csrf.passed)

        # If Server B incorrectly accepts tampered CSRF (200/302), harness catches defect
        client_b_broken = MockClient({
            ("POST", "/session"): Response(302, [], b""),
        })
        res_csrf_bad = scenarios.run_security_csrf_tamper_continuity(client_a, client_b_broken, "rails_to_csharp")
        self.assertFalse(res_csrf_bad.passed)

        # Tampered transfer returns 400 as expected on PUT
        client_a_trans = MockClient({
            ("GET", "/session/new"): Response(200, [], b'<form action="/session"><input name="authenticity_token" value="t"/></form>'),
            ("POST", "/session"): Response(302, [], b""),
            ("GET", "/users/me/profile"): Response(200, [], b'<a href="/session/transfers/token_good">Transfer</a>'),
        })
        client_b_trans = MockClient({
            ("GET", "/session/transfers/token_yyyy"): Response(200, [], b'<form action="/session/transfers/token_yyyy"><input name="authenticity_token" value="csrf"/></form>'),
            ("POST", "/session/transfers/token_yyyy"): Response(400, [], b"Bad Request"),
        })
        res_trans = scenarios.run_security_transfer_tamper_continuity(client_a_trans, client_b_trans, "rails_to_csharp")
        self.assertTrue(res_trans.passed)


if __name__ == "__main__":
    unittest.main()
