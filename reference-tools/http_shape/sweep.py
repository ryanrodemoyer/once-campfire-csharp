#!/usr/bin/env python3
"""Diff the HTTP header *shape* of the reference and the candidate for a set of representative
requests (plans/rust-conversion.md, Decisions 3 and 4: headers only need the same shape).

    parity/bin/reference up --seed default --port 4731
    parity/bin/candidate up --seed default --port 4732
    reference-tools/http_shape/sweep.py http://127.0.0.1:4731 http://127.0.0.1:4732 [--only SUBSTR] [-v]

Both servers must run the same seed (paths use its labels.json). The script signs in as David on
each side through the real form, discovers asset/avatar/blob URLs from the room page, then sends
every request in REQUESTS to both, and prints the requests whose shapes differ. Exit status 1 when
any differs.

Shape = status; Content-Type; Cache-Control directives; ETag presence and weakness (and value
equality when both bodies are identical); Vary tokens; Content-Encoding; Location (host-less);
Set-Cookie names and attributes (values and expiry dates ignored); the value of every other
header, except the ones in IGNORED. Content-Length vs chunked is ignored, but an empty body vs
a non-empty one is not.

Requests only read, apart from sign-in and the ones that fail before touching the database.
"""

import argparse
import gzip
import http.client
import json
import re
import sys
import time
import urllib.parse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SEED_LABELS = ROOT / "parity/.seed/default/labels.json"

CHROME = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
HTML = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
TURBO_STREAM = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
JSON = "application/json"

# Values that legitimately differ per request (or per server build) and carry no shape.
# x-cache depends on what each server's cache already holds.
IGNORED = {"date", "x-request-id", "x-runtime", "content-length", "transfer-encoding", "connection",
           "keep-alive", "server", "x-cache"}


def requests(labels, found):
    """(name, method, path, headers, options). options: revisit (repeat with If-None-Match /
    If-Modified-Since from the first response), anonymous (no cookies), csrf (add a form token)."""
    # hq has no messages; designers and watercooler (the busy room) do.
    hq, pets, direct = labels["rooms.hq"], labels["rooms.pets"], labels["rooms.david_and_jason"]
    designers, watercooler = labels["rooms.designers"], labels["rooms.watercooler"]
    david, jason = labels["users.david"], labels["users.jason"]
    first, image = labels["messages.first"], labels["messages.image"]
    bender = labels["bot_keys.bender"]
    since = 1772467200000  # 2026-03-02T16:00:00Z, the seed clock
    xhr = {"X-Requested-With": "XMLHttpRequest"}
    frame = {"Turbo-Frame": "user_sidebar"}
    stream = {"Accept": TURBO_STREAM}
    json_ = {"Accept": JSON}
    rs = [
        # Pages
        ("root", "GET", "/", {}, {}),
        ("room", "GET", f"/rooms/{designers}", {}, {"revisit": True}),
        ("room empty", "GET", f"/rooms/{hq}", {}, {}),
        ("room busy", "GET", f"/rooms/{watercooler}", {}, {}),
        ("room direct", "GET", f"/rooms/{direct}", {}, {}),
        ("room at message", "GET", f"/rooms/{designers}/@{first}", {}, {}),
        ("room settings", "GET", f"/rooms/{hq}/settings", {}, {}),
        ("room involvement", "GET", f"/rooms/{hq}/involvement", {}, {}),
        ("room edit", "GET", f"/rooms/opens/{pets}/edit", {}, {}),
        ("new open room", "GET", "/rooms/opens/new", {}, {}),
        ("new direct", "GET", "/rooms/directs/new", {}, {}),
        ("sidebar frame", "GET", "/users/me/sidebar", frame, {"revisit": True}),
        ("profile", "GET", "/users/me/profile", {}, {}),
        ("user", "GET", f"/users/{jason}", {}, {}),
        ("account edit", "GET", "/account/edit", {}, {}),
        ("account users", "GET", "/account/users", {}, {}),
        ("account bots", "GET", "/account/bots", {}, {}),
        ("custom styles", "GET", "/account/custom_styles/edit", {}, {}),
        ("searches", "GET", "/searches?q=pizza", {}, {}),
        ("push subscriptions", "GET", "/users/me/push_subscriptions", {}, {}),
        ("qr code", "GET", "/qr_code/aHR0cDovL2V4YW1wbGUuY29t", {"Accept": "image/*"}, {"revisit": True}),
        ("qr code bad", "GET", "/qr_code/aHR0cDovL2V4YW1wbGUuY29t2", {}, {}),
        # Paginated messages (HTML fragments) and turbo streams
        ("messages before", "GET", f"/rooms/{designers}/messages?before={image}", {}, {"revisit": True}),
        ("messages after", "GET", f"/rooms/{designers}/messages?after={first}", {}, {}),
        ("messages empty page", "GET", f"/rooms/{designers}/messages?after={labels['messages.boosted_by_david']}", {}, {}),
        ("messages none", "GET", f"/rooms/{hq}/messages?before={image}", {}, {}),
        ("refresh", "GET", f"/rooms/{designers}/refresh?since=0", stream, {"revisit": True}),
        ("refresh unchanged", "GET", f"/rooms/{hq}/refresh?since={since}", stream, {"revisit": True}),
        ("refresh empty", "GET", f"/rooms/{hq}/refresh?since=99999999999999", stream, {"revisit": True}),
        ("refresh html", "GET", f"/rooms/{hq}/refresh", {}, {}),
        ("account users stream", "GET", "/account/users?page=2", stream, {}),
        # JSON
        ("autocomplete json", "GET", "/autocompletable/users?query=j", json_, {"revisit": True}),
        ("autocomplete html", "GET", "/autocompletable/users?query=j", {}, {}),
        ("bot messages", "GET", f"/rooms/{hq}/{bender}/messages", {}, {}),
        ("bot messages bad key", "GET", f"/rooms/{hq}/1-nope/messages", {}, {"anonymous": True}),
        ("webmanifest", "GET", "/webmanifest", {}, {"revisit": True}),
        ("service worker", "GET", "/service-worker", {}, {"revisit": True}),
        ("health", "GET", "/up", {}, {"anonymous": True}),
        # Assets, avatars, blobs, logos
        ("css", "GET", found.get("css"), {"Accept": "text/css,*/*;q=0.1"}, {"revisit": True}),
        ("js", "GET", found.get("js"), {"Accept": "*/*"}, {"revisit": True}),
        ("image asset", "GET", found.get("image"), {"Accept": "image/*"}, {"revisit": True}),
        ("robots", "GET", "/robots.txt", {}, {"revisit": True}),
        ("avatar initials", "GET", found.get("avatar_initials"), {"Accept": "image/*"}, {"revisit": True}),
        ("avatar initials browser", "GET", found.get("avatar_initials"),
         {"Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"}, {"revisit": True}),
        ("avatar image", "GET", found.get("avatar_image"), {"Accept": "image/*"}, {"revisit": True}),
        ("account logo", "GET", "/account/logo?size=small", {"Accept": "image/*"}, {"revisit": True}),
        ("blob representation", "GET", found.get("representation"), {"Accept": "image/*"}, {}),
        ("blob redirect", "GET", found.get("blob"), {}, {}),
        # HEAD
        ("head room", "HEAD", f"/rooms/{designers}", {}, {}),
        ("head css", "HEAD", found.get("css"), {}, {}),
        ("head refresh", "HEAD", f"/rooms/{hq}/refresh?since={since}", stream, {}),
        # Signed out
        ("anon root", "GET", "/", {}, {"anonymous": True}),
        ("anon room", "GET", f"/rooms/{hq}", {}, {"anonymous": True}),
        ("anon sign in", "GET", "/session/new", {}, {"anonymous": True, "revisit": True}),
        ("anon join bad", "GET", "/join/nope", {}, {"anonymous": True}),
        # Errors
        ("404 room", "GET", "/rooms/1", {}, {}),
        ("404 route", "GET", "/nope", {}, {}),
        ("404 route json", "GET", "/nope.json", {}, {}),
        ("406 format", "GET", f"/rooms/{hq}.xml", {}, {}),
        ("406 encoding", "GET", f"/rooms/{hq}", {"Accept-Encoding": "identity;q=0"}, {}),
        ("406 stream only", "GET", f"/rooms/{hq}/refresh?since={since}", {"Accept": "application/pdf"}, {}),
        ("no gzip", "GET", f"/rooms/{hq}", {"Accept-Encoding": ""}, {}),
        ("xhr", "GET", f"/rooms/{designers}/messages?before={image}", xhr, {}),
        ("406 yaml", "GET", f"/rooms/{hq}.yaml", {}, {}),
        ("head 404 any", "HEAD", "/nope", {"Accept": "*/*"}, {}),
        ("head 406 xml", "HEAD", f"/rooms/{hq}.xml", {}, {}),
        # A cross-site post: Rails refuses it for having no token, the Rust app for Sec-Fetch-Site.
        ("422 csrf", "POST", f"/rooms/{hq}/messages", {"Content-Type": "application/x-www-form-urlencoded", "Sec-Fetch-Site": "cross-site"},
         {"body": "message[body]=x"}),
        ("401 bad sign in", "POST", "/session", {"Content-Type": "application/x-www-form-urlencoded"},
         {"csrf": "/session", "body": "email_address=nobody%40example.com&password=x"}),
        ("other user avatar 404", "GET", "/users/nope/avatar", {"Accept": "image/*"}, {}),
        ("blob 404", "GET", "/rails/active_storage/blobs/redirect/nope/x.png", {}, {}),
        ("old browser", "GET", f"/rooms/{hq}", {"User-Agent": "Mozilla/5.0 (Windows NT 10.0) Chrome/40.0 Safari/537.36"}, {}),
    ]
    return [r for r in rs if r[2]]


class Client:
    def __init__(self, base):
        url = urllib.parse.urlsplit(base)
        self.host, self.port = url.hostname, url.port or 80
        self.cookies = {}
        self.csrf = {}

    def request(self, method, path, headers=None, body=None, anonymous=False):
        h = {"User-Agent": CHROME, "Accept": HTML, "Accept-Encoding": "gzip, deflate, br, zstd",
             "Host": f"{self.host}:{self.port}"}
        h.update(headers or {})
        if not anonymous and self.cookies:
            h["Cookie"] = "; ".join(f"{k}={v}" for k, v in self.cookies.items())
        conn = http.client.HTTPConnection(self.host, self.port, timeout=60)
        conn.request(method, path, body=body, headers={k: v for k, v in h.items() if v != ""})
        response = conn.getresponse()
        raw = response.read()
        headers = [(k.lower(), v) for k, v in response.getheaders()]
        conn.close()
        if not anonymous:
            for name, value in headers:
                if name == "set-cookie":
                    cookie_name, _, rest = value.partition("=")
                    cookie_value = rest.split(";")[0]
                    if cookie_value:
                        self.cookies[cookie_name] = cookie_value
                    else:
                        self.cookies.pop(cookie_name, None)
        return Response(response.status, headers, raw)

    def form_token(self, page, action):
        html = self.request("GET", page).text()
        for form in re.findall(r"<form[^>]*>.*?</form>", html, re.S):
            if f'action="{action}"' in form or re.search(rf'action="https?://[^/"]+{re.escape(action)}"', form):
                token = re.search(r'name="authenticity_token" value="([^"]+)"', form)
                if token:
                    return token.group(1)
        raise SystemExit(f"no form for {action} on {page}")

    def sign_in(self, email, password):
        token = self.form_token("/session/new", "/session")
        body = urllib.parse.urlencode({"authenticity_token": token, "email_address": email, "password": password})
        r = self.request("POST", "/session", {"Content-Type": "application/x-www-form-urlencoded"}, body)
        if r.status != 302:
            raise SystemExit(f"sign in failed on {self.port}: {r.status}")


class Response:
    def __init__(self, status, headers, raw):
        self.status, self.headers, self.raw = status, headers, raw

    def get(self, name):
        values = [v for k, v in self.headers if k == name]
        return ", ".join(values) if values else None

    def all(self, name):
        return [v for k, v in self.headers if k == name]

    def body(self):
        if self.get("content-encoding") == "gzip" and self.raw:
            return gzip.decompress(self.raw)
        return self.raw

    def text(self):
        return self.body().decode("utf-8", "replace")


def tokens(value, lower=True):
    if value is None:
        return None
    parts = [p.strip() for p in value.split(",") if p.strip()]
    return sorted(p.lower() if lower else p for p in parts)


def cookie_shape(value):
    name, _, rest = value.partition("=")
    parts = [p.strip() for p in rest.split(";")]
    attrs = []
    for attr in parts[1:]:
        key, _, val = attr.partition("=")
        key = key.lower()
        if key == "expires":
            attrs.append("expires" + ("(past)" if "1970" in val else ""))
        elif key in ("path", "samesite", "domain"):
            attrs.append(f"{key}={val.lower()}")
        else:
            attrs.append(key)
    return f"{name}{'' if parts[0] else '(cleared)'}; " + "; ".join(sorted(attrs))


def location_shape(value):
    if value is None:
        return None
    url = urllib.parse.urlsplit(value)
    # Signed segments (`data--digest`) embed expiry times.
    path = "/".join("<signed>" if "--" in segment else segment for segment in url.path.split("/"))
    return urllib.parse.urlunsplit(("", "", path, url.query, url.fragment)) + (" (absolute)" if url.netloc else "")


def shape(response):
    s = {"status": response.status}
    etag = response.get("etag")
    s["etag"] = None if etag is None else ("weak" if etag.startswith("W/") else "strong")
    s["cache-control"] = tokens(response.get("cache-control"))
    s["vary"] = tokens(response.get("vary"))
    s["content-type"] = (response.get("content-type") or "").replace(" ", "").lower() or None
    s["content-encoding"] = response.get("content-encoding")
    s["location"] = location_shape(response.get("location"))
    s["set-cookie"] = sorted(cookie_shape(v) for v in response.all("set-cookie")) or None
    s["body"] = "empty" if not response.body() else "present"
    s["last-modified"] = "present" if response.get("last-modified") else None
    handled = {"etag", "cache-control", "vary", "content-type", "content-encoding", "location", "set-cookie", "last-modified"}
    for name in sorted({k for k, _ in response.headers} - handled - IGNORED):
        s[name] = response.get(name)
    return s


def discover(client, labels):
    html = client.request("GET", f"/rooms/{labels['rooms.designers']}").text()
    html += client.request("GET", f"/users/{labels['users.jason']}").text()
    found = {}

    def first(key, pattern):
        m = re.search(pattern, html)
        if m:
            found[key] = m.group(1).replace("&amp;", "&")

    first("css", r'href="(/assets/[^"]+\.css)"')
    first("js", r'"(/assets/[^"]+\.js)"')
    first("image", r'src="(/assets/[^"]+\.(?:svg|png))"')
    first("avatar_initials", rf'src="(/users/[^"/]+/avatar\?v=[^"]+)"[^>]*alt="David')
    first("avatar_image", rf'src="(/users/{re.escape(labels["avatar_tokens.jason"])}/avatar\?v=[^"]+)"')
    if "avatar_image" not in found:
        found["avatar_image"] = f"/users/{labels['avatar_tokens.jason']}/avatar"
    if "avatar_initials" not in found:
        found["avatar_initials"] = f"/users/{labels['avatar_tokens.david']}/avatar"
    first("representation", r'"(/rails/active_storage/representations/[^"]+)"')
    first("blob", r'"(/rails/active_storage/blobs/[^"]+)"')
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("reference")
    parser.add_argument("candidate")
    parser.add_argument("--only", help="run the requests whose name contains this")
    parser.add_argument("-v", "--verbose", action="store_true", help="print every shape, not just the diffs")
    parser.add_argument("--labels", default=str(SEED_LABELS))
    parser.add_argument("--no-bust", action="store_true", help="don't add a per-run query parameter to GETs")
    args = parser.parse_args()

    labels = json.loads(Path(args.labels).read_text())
    sides = {"reference": Client(args.reference), "candidate": Client(args.candidate)}
    found = {}
    for name, client in sides.items():
        client.sign_in(labels["emails.david"], labels["passwords.all"])
        found[name] = discover(client, labels)
    for key in sorted(set(found["reference"]) | set(found["candidate"])):
        if found["reference"].get(key) != found["candidate"].get(key):
            print(f"! discovered {key} differs: {found['reference'].get(key)} vs {found['candidate'].get(key)}")

    # A fresh query string per run keeps both servers' response caches (Thruster's and the
    # candidate's) out of it: a response cached by an earlier run for another Accept would
    # otherwise answer for this one.
    nonce = f"sweep={int(time.time() * 1000)}"
    failures = 0
    for name, method, path, headers, options in requests(labels, found["reference"]):
        if args.only and args.only not in name:
            continue
        if method in ("GET", "HEAD") and not args.no_bust:
            path += ("&" if "?" in path else "?") + nonce
        results = {}
        for side, client in sides.items():
            body = options.get("body")
            if options.get("csrf"):
                body = f"authenticity_token={urllib.parse.quote(client.form_token('/session/new', options['csrf']), safe='')}&{body}"
            anonymous = options.get("anonymous", False)
            first = client.request(method, path, headers, body, anonymous)
            runs = [("", first)]
            if options.get("revisit"):
                revisit = dict(headers)
                if first.get("etag"):
                    revisit["If-None-Match"] = first.get("etag")
                if first.get("last-modified"):
                    revisit["If-Modified-Since"] = first.get("last-modified")
                runs.append((" (revisit)", client.request(method, path, revisit, body, anonymous)))
            results[side] = runs

        for i, (suffix, ref) in enumerate(results["reference"]):
            cand = results["candidate"][i][1]
            a, b = shape(ref), shape(cand)
            if method == "GET" and ref.status == 200 and a["etag"] and b["etag"] and ref.body() == cand.body() and ref.get("etag") != cand.get("etag"):
                b["etag"] += " (different value for the same body)"
            label = f"{method} {path} [{name}{suffix}]"
            diffs = [k for k in sorted(set(a) | set(b)) if a.get(k) != b.get(k)]
            if diffs:
                failures += 1
                print(f"\n✗ {label}")
                for k in diffs:
                    print(f"    {k}:\n      reference: {a.get(k)}\n      candidate: {b.get(k)}")
            elif args.verbose:
                print(f"\n✓ {label}\n    {json.dumps(a)}")
            else:
                print(f"✓ {label}  {a['status']}")
    print(f"\n{failures} differing response(s)")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
