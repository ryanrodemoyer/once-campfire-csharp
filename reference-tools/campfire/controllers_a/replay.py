#!/usr/bin/env python3
"""HTTP replay of the session, account and user controllers against the reference and the Rust
port, both booted on the `default` parity seed with the parity SECRET_KEY_BASE:

    parity/bin/reference up --seed default --port 4311 --time 2026-03-02T16:00:00Z
    CAMPFIRE_FROZEN_TIME=2026-03-02T16:00:00Z campfire server   # PORT=4312, on a copy of the seed
    reference-tools/campfire/controllers_a/replay.py http://127.0.0.1:4311 http://127.0.0.1:4312

Each scenario runs the same requests on both servers (each with its own cookie jar) and compares
status, Location, the shape of Set-Cookie (names and attributes, not values), the content and
cache headers, and the body with CSRF tokens, transfer ids and request-specific values masked.
`--cross RAILS_DB RUST_DB` also carries sessions (cookies and CSRF-bound forms) from one server
to the other, copying the session row between the two databases.
"""

import difflib
import http.client
import json
import re
import sys
import urllib.parse

HOST = "campfire.test"
UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
PASSWORD = "secret123456"
LABELS = json.load(open("parity/.seed/default/labels.json"))
COMPARED_HEADERS = ["location", "content-type", "cache-control", "link", "x-version", "x-rev", "x-total-count", "content-disposition", "vary"]
# Blob keys are random: a variant sent inline is named after its key.
KEYED = re.compile(r'filename="[a-z0-9]{28}"; filename\*=UTF-8\'\'[a-z0-9]{28}')


class Reply:
    def __init__(self, status, headers, body):
        self.status, self.headers, self.body = status, headers, body

    def header(self, name):
        values = [v for k, v in self.headers if k.lower() == name]
        return values[0] if values else None

    def set_cookies(self):
        return [v for k, v in self.headers if k.lower() == "set-cookie"]

    def text(self):
        return self.body.decode("utf-8", "replace")

    def form_token(self, action):
        html = self.text()
        for candidate in (f'action="{action}"', f'action="http://{HOST}{action}"'):
            at = html.find(candidate)
            if at >= 0:
                match = re.search(r'name="authenticity_token" value="([^"]+)"', html[at:])
                return match.group(1)
        raise KeyError(f"no form for {action}")

    def button_token(self, action, method):
        for form in self.text().split("<form")[1:]:
            form = form.split("</form>")[0]
            # An action ending in "?" matches any query string (`fresh_account_logo_path`).
            target = f'action="{action}' if action.endswith("?") else f'action="{action}"'
            if target in form and f'name="_method" value="{method}"' in form:
                return re.search(r'name="authenticity_token" value="([^"]+)"', form).group(1)
        raise KeyError(f"no {method} button for {action}")

    def meta_token(self):
        return re.search(r'name="csrf-token" content="([^"]+)"', self.text()).group(1)


class Browser:
    def __init__(self, base, ip="203.0.113.50"):
        self.base = urllib.parse.urlparse(base)
        self.cookies = {}
        self.ip = ip

    def request(self, method, path, body=None, headers=None):
        conn = http.client.HTTPConnection(self.base.hostname, self.base.port, timeout=60)
        all_headers = {"Host": HOST, "User-Agent": UA, "X-Forwarded-For": self.ip}
        if self.cookies:
            all_headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in self.cookies.items())
        all_headers.update(headers or {})
        conn.request(method, path, body=body, headers=all_headers)
        response = conn.getresponse()
        reply = Reply(response.status, response.getheaders(), response.read())
        for cookie in reply.set_cookies():
            name, _, value = cookie.split(";")[0].partition("=")
            lowered = cookie.lower()
            if value == "" or "max-age=0" in lowered or "1970" in lowered:
                self.cookies.pop(name, None)
            else:
                self.cookies[name] = value
        return reply

    def get(self, path, headers=None):
        return self.request("GET", path, headers=headers)

    def form(self, method, path, token, fields=()):
        pairs = [("authenticity_token", token)]
        if method != "post":
            pairs.append(("_method", method))
        pairs.extend(fields)
        body = urllib.parse.urlencode(pairs)
        return self.request("POST", path, body, {"Content-Type": "application/x-www-form-urlencoded"})

    def multipart(self, method, path, token, fields=(), files=()):
        boundary = "----replayboundary7MA4YWxkTrZu0gW"
        parts = [("authenticity_token", token)] + ([("_method", method)] if method != "post" else []) + list(fields)
        body = b""
        for name, value in parts:
            body += f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode()
        for name, filename, content_type, data in files:
            body += f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\nContent-Type: {content_type}\r\n\r\n'.encode() + data + b"\r\n"
        body += f"--{boundary}--\r\n".encode()
        return self.request("POST", path, body, {"Content-Type": f"multipart/form-data; boundary={boundary}"})

    def sign_in(self, email):
        token = self.get("/session/new").form_token("/session")
        reply = self.form("post", "/session", token, [("email_address", email), ("password", PASSWORD)])
        assert reply.status == 302, f"sign in as {email}: {reply.status}"
        return reply


def normalize_body(text):
    text = re.sub(r'(name="authenticity_token" value=")[^"]+"', r'\1«csrf»"', text)
    text = re.sub(r'(name="csrf-token" content=")[^"]+"', r'\1«csrf»"', text)
    text = re.sub(r"/session/transfers/[A-Za-z0-9_=%+/-]+--[0-9a-f]+", "/session/transfers/«transfer»", text)
    text = re.sub(r"transfers/[^\"'\s<]+", "transfers/«transfer»", text)
    text = re.sub(r"[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[0-9a-f]{4}-[0-9a-f]{12}", "«uuid»", text)
    text = re.sub(r"/qr_code/[A-Za-z0-9_=-]+", "/qr_code/«qr»", text)
    text = re.sub(r"/(\d+)-[A-Za-z0-9]{12}/messages", r"/\1-«bot_token»/messages", text)
    text = re.sub(r"/join/[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}", "/join/«join_code»", text)
    text = re.sub(r">\s+<", ">\n<", text)
    text = re.sub(r"[ \t]+\n", "\n", text)
    return text.strip()


def cookie_shape(cookie):
    parts = [p.strip() for p in cookie.split(";")]
    name = parts[0].split("=")[0]
    attributes = sorted(p.split("=")[0].lower() for p in parts[1:])
    deleted = parts[0].endswith("=") or "max-age=0" in cookie.lower()
    return f"{name}{' (deleted)' if deleted else ''}: {', '.join(attributes)}"


FAILURES = []
PASSES = []


def compare(name, rails, rust, body=True, headers=COMPARED_HEADERS):
    problems = []
    if rails.status != rust.status:
        problems.append(f"status {rails.status} != {rust.status}")
    for header in headers:
        a, b = rails.header(header), rust.header(header)
        if header == "location" and a and b:
            a, b = normalize_body(a), normalize_body(b)
        if header == "content-disposition" and a and b:
            a, b = KEYED.sub("«key»", a), KEYED.sub("«key»", b)
        if a != b:
            problems.append(f"{header}: {a!r} != {b!r}")
    if (rails.header("etag") is None) != (rust.header("etag") is None):
        problems.append(f"etag presence: {rails.header('etag')!r} vs {rust.header('etag')!r}")
    shapes = lambda r: sorted(cookie_shape(c) for c in r.set_cookies())
    if shapes(rails) != shapes(rust):
        problems.append(f"set-cookie {shapes(rails)} != {shapes(rust)}")
    if body:
        a, b = rails.body, rust.body
        if (rails.header("content-type") or "").startswith(("text/", "application/json", "image/svg")):
            a, b = normalize_body(rails.text()), normalize_body(rust.text())
        if a != b:
            if isinstance(a, str):
                diff = "\n".join(list(difflib.unified_diff(a.splitlines(), b.splitlines(), "rails", "rust", lineterm="", n=1))[:40])
            else:
                diff = f"binary bodies differ ({len(a)} vs {len(b)} bytes)"
            problems.append("body:\n" + diff)
    if problems:
        FAILURES.append(name)
        print(f"✗ {name}")
        for problem in problems:
            print("    " + problem.replace("\n", "\n    "))
    else:
        PASSES.append(name)
        print(f"✓ {name}")


def both(servers, fn):
    return [fn(server) for server in servers]


def run(rails_base, rust_base, cross, seed="default"):
    if seed == "crowd":
        return finish(crowd(rails_base, rust_base))
    if seed == "first_run":
        return finish(first_run(rails_base, rust_base))
    return finish(default(rails_base, rust_base, cross))


def finish(_):
    print(f"\n{len(PASSES)} matched, {len(FAILURES)} differed")
    return 1 if FAILURES else 0


PNG = open("reference/app/assets/images/campfire-icon.png", "rb").read()


def crowd(rails_base, rust_base):
    admins = [Browser(rails_base, "203.0.113.70"), Browser(rust_base, "203.0.113.70")]
    both(admins, lambda b: b.sign_in(LABELS["emails.david"]))
    members = [Browser(rails_base, "203.0.113.71"), Browser(rust_base, "203.0.113.71")]
    both(members, lambda b: b.sign_in(LABELS["emails.kevin"]))
    for path in ["/account/edit", "/users/me/sidebar", "/autocompletable/users", "/autocompletable/users.json",
                 "/autocompletable/users.json?page=2", "/autocompletable/users.json?page=27", "/autocompletable/users.json?page=99",
                 "/autocompletable/users.json?query=a&page=3&z=1", "/autocompletable/users?filter=Ada"]:
        compare(f"crowd admin GET {path}", *both(admins, lambda b: b.get(path)))
    for page in ["1", "2", "3", "x"]:
        compare(f"crowd admin GET /account/users?page={page} (turbo stream)", *both(admins, lambda b: b.get(f"/account/users?page={page}", {"Accept": "text/vnd.turbo-stream.html"})))
    for path in ["/account/edit", "/users/me/sidebar"]:
        compare(f"crowd member GET {path}", *both(members, lambda b: b.get(path)))


def first_run(rails_base, rust_base):
    browsers = [Browser(rails_base, "203.0.113.80"), Browser(rust_base, "203.0.113.80")]
    for path in ["/session/new", "/", "/first_run", "/webmanifest.json", "/account/logo"]:
        compare(f"first run GET {path}", *both(browsers, lambda b: b.get(path)))
    tokens = both(browsers, lambda b: b.get("/first_run").form_token("/first_run"))
    fields = [("user[name]", "Owner"), ("user[email_address]", "owner@example.com"), ("user[password]", PASSWORD)]
    compare("POST /first_run", *[b.multipart("post", "/first_run", t, fields, [("user[avatar]", "me.png", "image/png", PNG)]) for b, t in zip(browsers, tokens)])
    for path in ["/", "/first_run", "/users/me/profile", "/account/edit"]:
        compare(f"after first run GET {path}", *both(browsers, lambda b: b.get(path)))
    compare("POST /first_run again", *[b.form("post", "/first_run", t, fields) for b, t in zip(browsers, tokens)], body=False)


def default(rails_base, rust_base, cross):
    david, kevin = LABELS["emails.david"], LABELS["emails.kevin"]
    ids = {k: LABELS[k] for k in LABELS}
    anon = [Browser(rails_base), Browser(rust_base)]

    # --- Signed out ---
    for path in ["/session/new", "/session/new?email_address=x%40y.com", "/", "/first_run", f"/join/{ids['join_codes.signal']}",
                 "/join/nope", "/account/logo", "/account/logo?size=small", "/webmanifest.json", "/service-worker.js",
                 "/qr_code/aHR0cDovL2NhbXBmaXJlLnRlc3Qvam9pbi9DUk11LWw4R2UtS0I5Qg", "/account/edit", "/session/transfers/bogus",
                 "/users/me/profile", "/session/new.json", "/account/logo.png"]:
        compare(f"anon GET {path}", *both(anon, lambda b: b.get(path)))
    compare("anon GET /session/new with Turbo-Frame", *both(anon, lambda b: b.get("/session/new", {"Turbo-Frame": "x"})))

    tokens = both(anon, lambda b: b.get("/session/new").form_token("/session"))
    compare("POST /session with a wrong password", *[b.form("post", "/session", t, [("email_address", david), ("password", "nope")]) for b, t in zip(anon, tokens)])
    compare("POST /session with no password", *[b.form("post", "/session", t, [("email_address", david)]) for b, t in zip(anon, tokens)])
    compare("POST /session with a bad token", *both(anon, lambda b: b.form("post", "/session", "bogus", [("email_address", david), ("password", PASSWORD)])), body=False)

    # rate_limit to: 10, within: 3.minutes: the 11th sign-in attempt from one IP is 429
    limited = [Browser(rails_base, "203.0.113.98"), Browser(rust_base, "203.0.113.98")]
    tokens = both(limited, lambda b: b.get("/session/new").form_token("/session"))
    for _ in range(10):
        for b, t in zip(limited, tokens):
            b.form("post", "/session", t, [("email_address", david), ("password", "nope")])
    compare("POST /session, 11th attempt (rate limited)", *[b.form("post", "/session", t, [("email_address", david), ("password", PASSWORD)]) for b, t in zip(limited, tokens)])

    # --- Sign in ---
    admins = [Browser(rails_base, "203.0.113.51"), Browser(rust_base, "203.0.113.51")]
    admins[0].get("/account/edit"), admins[1].get("/account/edit")
    compare("sign in (returns to the page asked for)", *both(admins, lambda b: b.sign_in(david)))
    members = [Browser(rails_base, "203.0.113.52"), Browser(rust_base, "203.0.113.52")]
    compare("sign in as a member", *both(members, lambda b: b.sign_in(kevin)))

    bender, jason_token, david_token = ids["users.bender"], ids["avatar_tokens.jason"], ids["avatar_tokens.david"]
    pages = ["/", "/account/edit", "/account/bots", "/account/bots/new", f"/account/bots/{bender}/edit", f"/account/bots/{bender}",
             "/account/custom_styles/edit", "/users/me/profile", "/users/me/sidebar", "/users/me/push_subscriptions",
             f"/users/{ids['users.kevin']}", f"/users/{ids['users.bender']}", f"/users/{ids['users.rita']}", f"/users/{ids['users.mallory']}",
             "/users/999", "/autocompletable/users", "/autocompletable/users?filter=j", "/autocompletable/users.json",
             "/autocompletable/users.json?query=e&page=1", f"/autocompletable/users?room_id={ids['rooms.watercooler'] if 'rooms.watercooler' in ids else 1}",
             f"/users/{david_token}/avatar", f"/users/{jason_token}/avatar", f"/users/{ids.get('avatar_tokens.bender', 'x')}/avatar",
             "/users/bogus/avatar", "/account/logo", "/webmanifest.json", "/session/new", f"/join/{ids['join_codes.signal']}",
             "/account/users.turbo_stream?page=2", "/account/users?page=2", "/first_run", "/account/join_code", "/users/me/profile/edit"]
    for path in pages:
        compare(f"admin GET {path}", *both(admins, lambda b: b.get(path)))
    for path in pages:
        compare(f"admin GET {path} in a Turbo frame", *both(admins, lambda b: b.get(path, {"Turbo-Frame": "frame"})))
    for path in ["/session/new", f"/join/{ids['join_codes.signal']}", "/first_run"]:
        compare(f"anon GET {path} in a Turbo frame", *both(anon, lambda b: b.get(path, {"Turbo-Frame": "frame"})))
    compare("admin GET /users/me/sidebar in a Turbo frame", *both(admins, lambda b: b.get("/users/me/sidebar", {"Turbo-Frame": "user_sidebar"})))
    compare("admin GET /account/users as a turbo stream", *both(admins, lambda b: b.get("/account/users?page=2", {"Accept": "text/vnd.turbo-stream.html"})))
    compare("admin GET /users/me/profile with a referrer", *both(admins, lambda b: b.get("/users/me/profile", {"Referer": f"http://{HOST}/rooms/1"})))
    for path in ["/qr_code/aHR0cDovL2NhbXBmaXJlLnRlc3Q", "/webmanifest", "/service-worker", "/autocompletable/users", "/users/me/profile", "/account/logo"]:
        compare(f"admin GET {path} with Accept: */*", *both(admins, lambda b: b.get(path, {"Accept": "*/*"})))
    compare("admin GET /autocompletable/users with Accept: application/json", *both(admins, lambda b: b.get("/autocompletable/users", {"Accept": "application/json"})))
    compare("admin GET avatar with */*", *both(admins, lambda b: b.get(f"/users/{david_token}/avatar", {"Accept": "*/*"})))
    compare("admin GET avatar with image accepts", *both(admins, lambda b: b.get(f"/users/{david_token}/avatar", {"Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"})))

    for path in ["/account/edit", "/account/bots", "/users/me/profile", "/users/me/sidebar", f"/users/{ids['users.jz']}"]:
        compare(f"member GET {path}", *both(members, lambda b: b.get(path)))

    # Conditional GETs
    for path in [f"/users/{david_token}/avatar", "/account/logo?size=small"]:
        etags = both(admins, lambda b: b.get(path).header("etag"))
        compare(f"admin GET {path} If-None-Match", *[b.get(path, {"If-None-Match": e or ""}) for b, e in zip(admins, etags)])

    # --- Mutations (each server gets the same writes) ---
    def with_token(page_path, action, method, fields, browsers=admins):
        tokens = both(browsers, lambda b: b.get(page_path).form_token(action))
        return [b.form(method, action, t, fields) for b, t in zip(browsers, tokens)]

    account_action = f"/account.{ids['accounts.signal']}"
    compare("PATCH /account.<id>", *with_token("/account/edit", account_action, "patch", [("account[name]", "Signal HQ")]))
    compare("  then GET /account/edit (notice)", *both(admins, lambda b: b.get("/account/edit")))
    compare("PATCH /account.<id> settings", *with_token("/account/edit", account_action, "patch", [("account[settings][restrict_room_creation_to_administrators]", "true")]))
    compare("  then GET /users/me/sidebar", *both(admins, lambda b: b.get("/users/me/sidebar")))
    compare("PATCH /account/custom_styles", *with_token("/account/custom_styles/edit", "/account/custom_styles", "patch", [("account[custom_styles]", "body { --color: red; }")]))
    compare("  then GET /account/custom_styles/edit", *both(admins, lambda b: b.get("/account/custom_styles/edit")))
    compare("PATCH /users/me/profile", *with_token("/users/me/profile", "/users/me/profile", "patch", [("user[name]", "David H"), ("user[bio]", "Hi")]))
    compare("  then GET /users/me/profile", *both(admins, lambda b: b.get("/users/me/profile")))
    compare("POST /account/bots", *with_token("/account/bots/new", "/account/bots", "post", [("user[name]", "Robo"), ("user[webhook_url]", "https://example.com/robo")]))
    compare("PATCH /account/bots/<id>", *with_token(f"/account/bots/{bender}/edit", f"/account/bots/{bender}", "patch", [("user[name]", "Bender B"), ("user[webhook_url]", "")]))
    compare("  then GET /account/bots/<id>/edit", *both(admins, lambda b: b.get(f"/account/bots/{bender}/edit")))
    compare("PATCH /account/users/<id> (role)", *with_token("/account/edit", f"/account/users/{ids['users.jz']}", "patch", [("user[role]", "administrator")]))
    compare("POST /users/<id>/ban", *with_token(f"/users/{ids['users.jz']}", f"/users/{ids['users.jz']}/ban", "post", []))
    compare("  then GET /users/<id>", *both(admins, lambda b: b.get(f"/users/{ids['users.jz']}")))
    compare("DELETE /users/<id>/ban", *with_token(f"/users/{ids['users.jz']}", f"/users/{ids['users.jz']}/ban", "delete", []))
    compare("member PATCH /account.<id> (forbidden)", *[b.form("patch", account_action, t, [("account[name]", "x")]) for b, t in zip(members, both(members, lambda b: b.get("/account/edit").meta_token()))])
    body = json.dumps({"push_subscription": {"endpoint": "http://example.com/push", "p256dh_key": "a", "auth_key": "b"}})
    compare("POST /users/me/push_subscriptions (invalid endpoint)", *[b.request("POST", "/users/me/push_subscriptions", body, {"Content-Type": "application/json", "X-CSRF-Token": t}) for b, t in zip(admins, both(admins, lambda b: b.get("/users/me/push_subscriptions").meta_token()))])

    # A permitted push endpoint (resolves through DNS on both), again (touched), and signing out
    # with it removes it.
    pushers = [Browser(rails_base, "203.0.113.57"), Browser(rust_base, "203.0.113.57")]
    both(pushers, lambda b: b.sign_in(LABELS["emails.jason"]))
    endpoint = "https://fcm.googleapis.com/fcm/send/replay-abc"
    body = json.dumps({"push_subscription": {"endpoint": endpoint, "p256dh_key": "BK", "auth_key": "au"}})
    for attempt in ["create", "existing"]:
        compare(f"POST /users/me/push_subscriptions ({attempt})", *[b.request("POST", "/users/me/push_subscriptions", body, {"Content-Type": "application/json", "X-CSRF-Token": t}) for b, t in zip(pushers, both(pushers, lambda b: b.get("/users/me/push_subscriptions").meta_token()))])
    compare("  then GET /users/me/push_subscriptions", *both(pushers, lambda b: b.get("/users/me/push_subscriptions")))
    compare("DELETE /session with push_subscription_endpoint", *[b.form("delete", "/session", t, [("push_subscription_endpoint", endpoint)]) for b, t in zip(pushers, both(pushers, lambda b: b.get("/users/me/profile").form_token("/session")))])
    both(pushers, lambda b: b.sign_in(LABELS["emails.jason"]))
    compare("  then GET /users/me/push_subscriptions", *both(pushers, lambda b: b.get("/users/me/push_subscriptions")))

    # Joining
    joiners = [Browser(rails_base, "203.0.113.53"), Browser(rust_base, "203.0.113.53")]
    join = f"/join/{ids['join_codes.signal']}"
    compare("POST /join/<code>", *with_token(join, join, "post", [("user[name]", "New Person"), ("user[email_address]", "new@example.com"), ("user[password]", PASSWORD)], joiners))
    compare("  then GET /", *both(joiners, lambda b: b.get("/")))
    others = [Browser(rails_base, "203.0.113.54"), Browser(rust_base, "203.0.113.54")]
    compare("POST /join/<code> with a taken email", *with_token(join, join, "post", [("user[name]", "Dup"), ("user[email_address]", "new@example.com"), ("user[password]", PASSWORD)], others))
    compare("POST /join/<wrong code>", *[b.form("post", "/join/nope", t, []) for b, t in zip(others, both(others, lambda b: b.get(join).form_token(join)))], body=False)

    # Transfers
    transfer = both(admins, lambda b: re.search(r"/session/transfers/([^\"]+)\"", b.get(f"/users/{ids['users.kevin']}").text()).group(1))
    phones = [Browser(rails_base, "203.0.113.55"), Browser(rust_base, "203.0.113.55")]
    paths = [f"/session/transfers/{t}" for t in transfer]
    compare("GET /session/transfers/<id>", *[b.get(p) for b, p in zip(phones, paths)])
    compare("PUT /session/transfers/<id>", *[b.form("put", p, b.get(p).form_token(p), []) for b, p in zip(phones, paths)])
    compare("  then GET /users/me/profile", *both(phones, lambda b: b.get("/users/me/profile")))

    # Join code reset, sign out
    compare("POST /account/join_code", *with_token("/account/edit", "/account/join_code", "post", []))
    compare("DELETE /session", *with_token("/users/me/profile", "/session", "delete", []))
    compare("  then GET /users/me/profile", *both(admins, lambda b: b.get("/users/me/profile")))

    # Uploads
    both(admins, lambda b: b.sign_in(david))
    compare("PATCH /users/me/profile with an avatar", *[b.multipart("patch", "/users/me/profile", t, [("user[name]", "Kevin")], [("user[avatar]", "me.png", "image/png", PNG)]) for b, t in zip(members, both(members, lambda b: b.get("/users/me/profile").form_token("/users/me/profile")))])
    compare("  then GET /users/me/profile", *both(members, lambda b: b.get("/users/me/profile")))
    avatar = both(members, lambda b: re.search(r'src="(/users/[^"]+/avatar\?v=\d+)"', b.get("/users/me/profile").text()).group(1))
    compare("  then GET the avatar", *[b.get(a) for b, a in zip(members, avatar)], body=False)
    compare("DELETE /users/<token>/avatar", *[b.form("delete", a.split("?")[0], b.get("/users/me/profile").meta_token()) for b, a in zip(members, avatar)])
    compare("PATCH /account.<id> with a logo", *[b.multipart("patch", account_action, t, [], [("account[logo]", "logo.png", "image/png", PNG)]) for b, t in zip(admins, both(admins, lambda b: b.get("/account/edit").form_token(account_action)))])
    compare("  then GET /account/edit", *both(admins, lambda b: b.get("/account/edit")))
    compare("  then GET /account/logo", *both(admins, lambda b: b.get("/account/logo?size=small")), body=False)
    compare("DELETE /account/logo", *[b.form("delete", "/account/logo", t) for b, t in zip(admins, both(admins, lambda b: b.get("/account/edit").button_token("/account/logo?", "delete")))])
    compare("POST /account/bots with an avatar", *[b.multipart("post", "/account/bots", t, [("user[name]", "Pixel"), ("user[webhook_url]", "")], [("user[avatar]", "bot.png", "image/png", PNG)]) for b, t in zip(admins, both(admins, lambda b: b.get("/account/bots/new").form_token("/account/bots")))])
    compare("  then GET /account/bots", *both(admins, lambda b: b.get("/account/bots")))
    compare("DELETE /account/users/<id>", *[b.form("delete", f"/account/users/{ids['users.loner']}", t) for b, t in zip(admins, both(admins, lambda b: b.get("/account/edit").button_token(f"/account/users/{ids['users.loner']}", "delete")))])
    compare("  then GET /account/edit", *both(admins, lambda b: b.get("/account/edit")))

    if cross:
        cross_server(rails_base, rust_base, cross)


def copy_session(token, from_db, to_db):
    """Copies a session row (by token) into the other server's database, so a cookie issued by
    one server names a session the other can find."""
    import sqlite3
    row = sqlite3.connect(from_db).execute(
        "SELECT user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at FROM sessions WHERE token = ?", (token,)).fetchone()
    target = sqlite3.connect(to_db, timeout=10)
    target.execute("INSERT INTO sessions (user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)", row)
    target.commit()


def newest_session_token(db):
    import sqlite3
    return sqlite3.connect(db).execute("SELECT token FROM sessions ORDER BY id DESC LIMIT 1").fetchone()[0]


def cross_server(rails_base, rust_base, dbs):
    rails_db, rust_db = dbs
    vectors = json.load(open("vectors/campfire_sessions.json"))
    # A Rails-issued cookie for a seeded session renders the same page on both.
    pair = [Browser(rails_base, "203.0.113.60"), Browser(rust_base, "203.0.113.60")]
    for browser in pair:
        browser.cookies = dict(p.split("=", 1) for p in vectors["sessions"][0]["cookie_header"].split("; "))
    compare("Rails-issued seed cookie: GET /users/me/profile", *both(pair, lambda b: b.get("/users/me/profile")))

    for issuer, target, issuer_db, target_db, label in [
        (rails_base, rust_base, rails_db, rust_db, "Rails sign-in continued on Rust"),
        (rust_base, rails_base, rust_db, rails_db, "Rust sign-in continued on Rails"),
    ]:
        browser = Browser(issuer, "203.0.113.61")
        browser.sign_in(LABELS["emails.kevin"])
        copy_session(newest_session_token(issuer_db), issuer_db, target_db)
        page = browser.get("/users/me/profile")
        carried = Browser(target, "203.0.113.61")
        carried.cookies = dict(browser.cookies)
        reply = carried.get("/users/me/profile")
        ok = reply.status == 200 and "Kevin" in reply.text()
        (PASSES if ok else FAILURES).append(label)
        print(f"{'✓' if ok else '✗'} {label}: GET /users/me/profile {reply.status}")
        # A form rendered by the issuer submits to the other server (same session and CSRF secret).
        carried.cookies = dict(browser.cookies)
        reply = carried.form("patch", "/users/me/profile", page.form_token("/users/me/profile"), [("user[bio]", label)])
        ok = reply.status == 302 and reply.header("location") == f"http://{HOST}/users/me/profile"
        (PASSES if ok else FAILURES).append(label + " form")
        print(f"{'✓' if ok else '✗'} {label}: issuer's form accepted ({reply.status} {reply.header('location')})")


if __name__ == "__main__":
    cross = None
    if "--cross" in sys.argv:
        at = sys.argv.index("--cross")
        cross = (sys.argv[at + 1], sys.argv[at + 2])
    seed = sys.argv[sys.argv.index("--seed") + 1] if "--seed" in sys.argv else "default"
    sys.exit(run(sys.argv[1], sys.argv[2], cross, seed))
