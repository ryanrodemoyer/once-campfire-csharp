"""Tests for the normalizer: python3 -m unittest discover -s parity/normalize"""

import base64
import json
import unittest

import normalize
from normalize import Options, mask_text, normalize_body, normalize_html

SEED = normalize.seed_time_from_labels({"clock.now": "2026-03-02 16:00:00 UTC"})
OPTIONS = Options(seed_time=SEED)


def signed(payload, digest="ab" * 32):
    return base64.b64encode(json.dumps(payload).encode()).decode() + "--" + digest


class TreeTests(unittest.TestCase):
    def test_equivalent_markup_normalizes_the_same(self):
        a = '<div class="a  b" id=x><p>Hi&amp;<br/>there</p><img src="/x.png" alt=""></div>'
        b = "<div id='x' class=\"a  b\">\n  <p>Hi&#38;<br>there</p>\n  <img alt src=/x.png>\n</div>"
        self.assertEqual(normalize_html(a), normalize_html(b))

    def test_one_node_per_line_with_sorted_attributes(self):
        self.assertEqual(normalize_html('<ul data-z="1" class="list"><li>One</li><li>Two</ul>'),
                         '<ul class="list" data-z="1">\n  <li>\n    One\n  </li>\n  <li>\n    Two\n  </li>\n</ul>\n')

    def test_implied_end_tags_and_ignored_self_closing(self):
        self.assertEqual(normalize_html("<ul><li>a<li>b</ul>"), normalize_html("<ul><li>a</li><li>b</li></ul>"))
        self.assertEqual(normalize_html("<p>a<div>b</div>"), normalize_html("<p>a</p><div>b</div>"))
        self.assertEqual(normalize_html("<div/>text"), normalize_html("<div>text</div>"))
        self.assertEqual(normalize_html('<svg><path d="M0"/><g></g></svg>'), normalize_html('<svg><path d="M0"></path><g></g></svg>'))

    def test_a_missing_class_is_a_difference(self):
        self.assertNotEqual(normalize_html('<div class="message message--emoji">x</div>'), normalize_html('<div class="message">x</div>'))

    def test_whitespace_kept_in_pre(self):
        self.assertNotEqual(normalize_html("<pre>a\n b</pre>"), normalize_html("<pre>a b</pre>"))
        self.assertEqual(normalize_html("<p>a\n b</p>"), normalize_html("<p>a b</p>"))

    def test_json_script_is_canonical(self):
        a = '<script type="importmap">{"imports": {"a": "/assets/a-0123abcd.js"}}</script>'
        b = '<script type="importmap">{\n "imports":{"a":"/assets/a-fedc9876.js"}\n}</script>'
        self.assertEqual(normalize_html(a), normalize_html(b))
        self.assertNotEqual(normalize_html(a, Options(keep_asset_digests=True)), normalize_html(b, Options(keep_asset_digests=True)))


class MaskTests(unittest.TestCase):
    def test_csrf_tokens_are_typed(self):
        good = base64.urlsafe_b64encode(bytes(range(64))).decode().rstrip("=")
        other = base64.urlsafe_b64encode(bytes(64)).decode()
        a = normalize_html(f'<meta name="csrf-token" content="{good}"><input type="hidden" name="authenticity_token" value="{good}">')
        b = normalize_html(f'<meta name="csrf-token" content="{other}"><input type="hidden" name="authenticity_token" value="{other}">')
        self.assertEqual(a, b)
        self.assertIn("«csrf»", a)
        self.assertIn("«csrf:malformed:short»", normalize_html('<input name="authenticity_token" value="short">'))

    def test_signed_ids_decode_to_their_record(self):
        transfer = {"_rails": {"data": 7, "pur": "transfer", "exp": "2026-03-02T20:00:00.000Z"}}
        self.assertEqual(mask_text(f"/session/transfers/{signed(transfer)}", OPTIONS), "/session/transfers/«signed_id:transfer:7:expires»")
        self.assertNotEqual(mask_text(signed(transfer)), mask_text(signed({"_rails": {"data": 8, "pur": "transfer"}})))
        frozen = Options(seed_time=SEED, frozen_clock=True)
        self.assertEqual(mask_text(signed(transfer), frozen), "«signed_id:transfer:7:exp«t+14400s»»")

    def test_signed_global_ids(self):
        sgid = {"_rails": {"data": "gid://campfire/User/42", "pur": "attachable"}}
        self.assertEqual(mask_text(signed(sgid)), "«sgid:attachable:User#42»")
        stream = base64.b64encode(json.dumps(base64.b64encode(b"gid://campfire/Room/3").decode() + ":messages").encode()).decode()
        self.assertEqual(mask_text(f'signed-stream-name="{stream}--{"cd" * 20}"'), 'signed-stream-name="«stream:Room#3:messages»"')

    def test_signed_token_after_a_path(self):
        blob = {"_rails": {"data": 12, "pur": "blob_id"}}
        self.assertEqual(mask_text(f"/rails/active_storage/blobs/redirect/{signed(blob)}/moon.jpg"),
                         "/rails/active_storage/blobs/redirect/«signed_id:blob_id:12»/moon.jpg")

    def test_times_decode_against_the_seed_clock(self):
        self.assertEqual(mask_text("2026-03-02T15:00:00Z", OPTIONS), "«t-3600s»")
        self.assertEqual(mask_text("2026-03-02T16:00:05Z", OPTIONS), "«t:live»")
        self.assertEqual(mask_text("2026-03-02T16:00:05Z", Options(seed_time=SEED, frozen_clock=True)), "«t+5s»")
        self.assertEqual(normalize_html(f'<div data-message-timestamp="{SEED - 1500}"></div>', OPTIONS), '<div data-message-timestamp="«epochms-1.500s»">\n</div>\n')
        self.assertEqual(mask_text("/account/logo?v=20260302155900&size=small", OPTIONS), "/account/logo?v=«number-60s»&size=small")
        self.assertNotEqual(mask_text("2026-03-02T15:00:00Z", OPTIONS), mask_text("2026-03-02T15:00:01Z", OPTIONS))

    def test_random_values(self):
        self.assertEqual(mask_text("id 0f8fad5b-d9cb-469f-a165-70867728950e"), "id «uuid»")
        normalize.Volatile.seeded = {"CRMu-l8Ge-KB9B", "5-abcdefghijkl"}
        try:
            self.assertEqual(mask_text("/join/CRMu-l8Ge-KB9B /join/Zzzz-yyyy-XXXX"), "/join/CRMu-l8Ge-KB9B /join/«join_code»")
            self.assertEqual(mask_text("/rooms/1/5-abcdefghijkl/messages /rooms/1/6-abcdefghijkl/messages"),
                             "/rooms/1/5-abcdefghijkl/messages /rooms/1/6-«bot_token»/messages")
        finally:
            normalize.Volatile.seeded = set()

    def test_qr_code_paths_show_what_they_encode(self):
        encoded = base64.urlsafe_b64encode(b"http://campfire.test/join/abc").decode().rstrip("=")
        self.assertEqual(mask_text(f"/qr_code/{encoded}"), "/qr_code/«qr:http://campfire.test/join/abc»")

    def test_nonces(self):
        self.assertEqual(normalize_html('<script nonce="abc">x</script>'), normalize_html('<script nonce="def">x</script>'))


class BodyTests(unittest.TestCase):
    def test_json_sorted_and_masked(self):
        a = normalize_body(b'{"b": 1, "a": "2026-03-02T15:00:00Z"}', "application/json; charset=utf-8", OPTIONS)
        b = normalize_body(b'{"a":"2026-03-02T15:00:00Z","b":1}', "application/json", OPTIONS)
        self.assertEqual(a, b)
        self.assertIn("«t-3600s»", a)

    def test_off_by_one_page_is_a_difference(self):
        page = "".join(f'<div class="message" id="message_{i}">{i}</div>' for i in range(40))
        self.assertNotEqual(normalize_body(page.encode(), "text/html"), normalize_body(page.rsplit("<div", 1)[0].encode(), "text/html"))
        users = json.dumps([{"id": i} for i in range(20)]).encode()
        self.assertNotEqual(normalize_body(users, "application/json"), normalize_body(json.dumps([{"id": i} for i in range(21)]).encode(), "application/json"))

    def test_binary_bodies_by_digest(self):
        self.assertEqual(normalize_body(b"\x89PNG", "image/png"), "«4 bytes sha256:" + __import__("hashlib").sha256(b"\x89PNG").hexdigest() + "»\n")
        self.assertEqual(normalize_body(b"", "text/html"), "\n")

    def test_turbo_streams_as_trees(self):
        a = '<turbo-stream action="append" target="x"><template><div class="a">1</div></template></turbo-stream>'
        b = '<turbo-stream target="x" action="append">\n<template>\n<div class="a">1</div>\n</template>\n</turbo-stream>'
        self.assertEqual(normalize_body(a.encode(), "text/vnd.turbo-stream.html"), normalize_body(b.encode(), "text/vnd.turbo-stream.html"))


if __name__ == "__main__":
    unittest.main()
