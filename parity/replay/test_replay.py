"""Offline tests for the replay comparison and the planted defects:
python3 -m unittest discover -s parity/replay -p 'test_*.py'

The live checks (Ruby vs Ruby, planted defects against a running reference) are parity/replay/selftest.
"""

import base64
import json
import os
import sys
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import defects  # noqa: E402
import normalize  # noqa: E402
import replay  # noqa: E402
from replay import Reply, cookie_shape, differences, is_deletion  # noqa: E402

OPTIONS = normalize.Options(seed_time=normalize.seed_time_from_labels({"clock.now": "2026-03-02T16:00:00Z"}))
CSRF_A = base64.urlsafe_b64encode(bytes(range(64))).decode()
CSRF_B = base64.urlsafe_b64encode(bytes(range(64, 128))).decode()
HEADERS = [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "max-age=0, private, must-revalidate"),
           ("X-Request-Id", "0f8fad5b-d9cb-469f-a165-70867728950e"), ("X-Runtime", "0.012"), ("ETag", 'W/"abc"'),
           ("Date", "Mon, 02 Mar 2026 16:00:01 GMT"), ("Content-Length", "10")]


def page(token, body='<div class="message message--emoji">🎉</div>'):
    return f'<html><head><meta name="csrf-token" content="{token}"></head><body>{body}</body></html>'.encode()


def kinds(found):
    return [d.kind for d in found]


class DifferencesTests(unittest.TestCase):
    def test_volatile_values_match(self):
        other = [(k, {"X-Request-Id": "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed", "X-Runtime": "0.5", "ETag": 'W/"def"',
                      "Date": "Mon, 02 Mar 2026 16:00:09 GMT", "Content-Length": "12"}.get(k, v)) for k, v in HEADERS]
        self.assertEqual(differences(Reply(200, HEADERS, page(CSRF_A)), Reply(200, other, page(CSRF_B)), OPTIONS), [])

    def test_status_header_shape_and_values(self):
        found = differences(Reply(200, HEADERS, page(CSRF_A)), Reply(302, HEADERS[1:] + [("Location", "/x")], page(CSRF_A)), OPTIONS)
        self.assertEqual(kinds(found), ["status", "header-shape", "body"])  # no Content-Type: the body is compared as bytes
        self.assertIn("missing ['content-type'], extra ['location']", found[1].detail)
        changed = [(k, "no-cache" if k == "Cache-Control" else v) for k, v in HEADERS]
        self.assertEqual(kinds(differences(Reply(200, HEADERS, b""), Reply(200, changed, b""), OPTIONS)), ["header:cache-control"])
        strong = [(k, '"abc"' if k == "ETag" else v) for k, v in HEADERS]
        self.assertEqual(kinds(differences(Reply(200, HEADERS, b""), Reply(200, strong, b""), OPTIONS)), ["header:etag"])

    def test_missing_class_and_page_size(self):
        found = differences(Reply(200, HEADERS, page(CSRF_A)), Reply(200, HEADERS, page(CSRF_B, '<div class="message">🎉</div>')), OPTIONS)
        self.assertEqual(kinds(found), ["body"])
        self.assertIn('-    <div class="message message--emoji">', found[0].detail)
        self.assertIn('+    <div class="message">', found[0].detail)
        json_headers = [("Content-Type", "application/json")]
        found = differences(Reply(200, json_headers, b'[{"id":1},{"id":2}]'), Reply(200, json_headers, b'[{"id":1}]'), OPTIONS)
        self.assertEqual(kinds(found), ["body"])

    def test_header_values_are_masked(self):
        a = [("Location", "http://campfire.test/session/transfers/" + normalize_signed(7))]
        b = [("Location", "http://campfire.test/session/transfers/" + normalize_signed(7, "cd"))]
        self.assertEqual(differences(Reply(302, a, b""), Reply(302, b, b""), OPTIONS), [])
        c = [("Location", "http://campfire.test/session/transfers/" + normalize_signed(8))]
        self.assertEqual(kinds(differences(Reply(302, a, b""), Reply(302, c, b""), OPTIONS)), ["header:location"])
        modified = [("Last-Modified", "Mon, 02 Mar 2026 15:00:00 GMT")]
        self.assertEqual(replay.header_value(Reply(200, modified, b""), "last-modified", OPTIONS), "«http-date-3600s»")

    def test_ignored_headers(self):
        self.assertEqual(differences(Reply(200, HEADERS, b""), Reply(200, HEADERS + [("X-Port", "1")], b""), OPTIONS, ignored_headers={"x-port"}), [])


def normalize_signed(record, digest="ab"):
    data = base64.b64encode(json.dumps({"_rails": {"data": record, "pur": "transfer"}}).encode()).decode()
    return f"{data}--{digest * 32}"


class CookieTests(unittest.TestCase):
    def test_deletion_is_read_from_attributes(self):
        self.assertTrue(is_deletion("session_token=; path=/; max-age=0; expires=Thu, 01 Jan 1970 00:00:00 GMT"))
        self.assertTrue(is_deletion("session_token=x; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT"))
        self.assertFalse(is_deletion("session_token=eyJfcmFp1970bGzZ--ab19701; path=/; expires=Fri, 02 Mar 2046 16:00:00 GMT"))

    def test_shape_types_values_and_keeps_attributes(self):
        encrypted = "abcdefghijklmnopqrstuvwx--ABCDEFGHIJKLMNOP--abcdefghijklmnopqrstuvwx%3D%3D"
        self.assertEqual(cookie_shape(f"_campfire_session={encrypted}; path=/; expires=Fri, 02 Mar 2046 16:00:00 GMT; HttpOnly; SameSite=Lax"),
                         "_campfire_session «encrypted»: expires=«time»; httponly; path=/; samesite=Lax")
        token = base64.b64encode(json.dumps({"_rails": {"data": "rJTbusMDfTW9jkyJ4qqyodbM", "pur": "cookie.session_token"}}).encode()).decode()
        self.assertEqual(cookie_shape(f"session_token={token}--{'ab' * 32}; path=/"), "session_token «signed_id:cookie.session_token:«token»»: path=/")
        self.assertNotEqual(cookie_shape("a=1; path=/"), cookie_shape("a=1; path=/; secure"))

    def test_jar_follows_deletions(self):
        browser = replay.Browser("http://127.0.0.1:1", "203.0.113.1")
        browser.cookies = {"session_token": "x"}
        for cookie in ["session_token=; path=/; max-age=0; expires=Thu, 01 Jan 1970 00:00:00 GMT"]:
            name, value, _ = replay.parse_cookie(cookie)
            if is_deletion(cookie):
                browser.cookies.pop(name, None)
        self.assertEqual(browser.cookies, {})


class FormTests(unittest.TestCase):
    HTML = ('<form class="button_to" method="post" action="/account/logo?v=1"><input type="hidden" name="_method" value="delete">'
            '<input type="hidden" name="authenticity_token" value="LOGO"></form>'
            '<form action="http://campfire.test/session" method="post"><input type="hidden" name="authenticity_token" value="SESSION"></form>'
            '<meta name="csrf-token" content="META">')

    def test_tokens(self):
        reply = Reply(200, [], self.HTML.encode())
        self.assertEqual(reply.form_token("/session"), "SESSION")
        self.assertEqual(reply.button_token("/account/logo?", "delete"), "LOGO")
        self.assertEqual(reply.meta_token(), "META")
        with self.assertRaises(KeyError):
            reply.form_token("/account/logo")


class DefectTests(unittest.TestCase):
    MESSAGES = ('<div id="messages"><div class="message " id="m1"><div class="message__body"><div>One</div></div></div>'
                '<div class="message  message--emoji" id="m2"><div>Two</div></div></div>')

    def test_page_size(self):
        html = self.MESSAGES.encode()
        self.assertEqual(defects.resize_page(html, "text/html", -1).decode(),
                         '<div id="messages"><div class="message " id="m1"><div class="message__body"><div>One</div></div></div></div>')
        self.assertEqual(defects.resize_page(html, "text/html", +1).decode().count('id="m2"'), 2)
        self.assertEqual(json.loads(defects.resize_page(b"[1,2,3]", "application/json", -1)), [1, 2])

    def test_missing_class_touches_only_that_class(self):
        html = defects.drop_class(self.MESSAGES, "message--emoji")
        self.assertIn('class="message " id="m1"', html)
        self.assertIn('class="message" id="m2"', html)

    def test_defects_only_change_what_they_name(self):
        headers = [("Content-Type", "text/html"), ("X-Frame-Options", "SAMEORIGIN")]
        self.assertEqual(defects.Defect("header:x-frame-options").apply("/", 200, headers, b"")[1], headers[:1])
        self.assertEqual(defects.Defect("status:/up=503").apply("/up?x=1", 200, headers, b"")[0], 503)
        self.assertEqual(defects.Defect("status:/up=503").apply("/upp", 200, headers, b"")[0], 200)
        body = self.MESSAGES.encode()
        self.assertEqual(defects.Defect("page-size:-1").apply("/rooms/1/messages/2", 200, headers, body)[2], body)
        self.assertNotEqual(defects.Defect("page-size:-1").apply("/rooms/1/messages?before=2", 200, headers, body)[2], body)


if __name__ == "__main__":
    unittest.main()
