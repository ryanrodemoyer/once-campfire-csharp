"""HTTP replay: scripted sessions run against the reference (Rails) and a candidate, each with its
own cookie jar, and every response pair diffed after normalization (parity/normalize).

Generalizes the Rust port's reference-rust/reference-tools/campfire/controllers_a/replay.py from
one script into route families (parity/replay/families/) that a feature task runs on its own:

    parity/replay/run http://127.0.0.1:3100 http://127.0.0.1:3200 --family messages

A response pair is compared on:

    status        the status code
    header-shape  the set of header names, less transport headers (Date, Connection, framing)
    header:NAME   each header's value, masked as bodies are; X-Request-Id, X-Runtime and ETag
                  only by presence (and ETag's weakness)
    set-cookie    each cookie's name, attributes and value type (encrypted, signed, deleted, plain)
    body          the normalized body (parity/normalize/normalize.py)
"""

import difflib
import http.client
import json
import os
import re
import sys
import urllib.parse

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "parity", "normalize"))
import normalize  # noqa: E402

HOST = "campfire.test"
UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
TRANSPORT_HEADERS = {"date", "connection", "keep-alive", "transfer-encoding", "content-length"}
PRESENCE_HEADERS = {"x-request-id", "x-runtime", "server-timing"}
SIDES = ("reference", "candidate")
# Active Storage names a variant sent inline after its random blob key.
BLOB_KEY_FILENAME = re.compile(r'filename="[a-z0-9]{28}"; filename\*=UTF-8\'\'[a-z0-9]{28}')
CSP_NONCE = re.compile(r"'nonce-[^']+'")


class Reply:
    def __init__(self, status, headers, body):
        self.status, self.headers, self.body = status, headers, body

    def header(self, name):
        values = [v for k, v in self.headers if k.lower() == name]
        return ", ".join(values) if values else None

    def set_cookies(self):
        return [v for k, v in self.headers if k.lower() == "set-cookie"]

    def text(self):
        return self.body.decode("utf-8", "replace")

    def form_token(self, action):
        """The authenticity token of the form posting to action (a path, or a path ending in "?"
        to match any query string)."""
        for form in self.forms():
            if form_action_matches(form, action):
                return token_in(form, action)
        raise KeyError(f"no form for {action} (status {self.status})")

    def button_token(self, action, method):
        for form in self.forms():
            if form_action_matches(form, action) and f'name="_method" value="{method}"' in form:
                return token_in(form, action)
        raise KeyError(f"no {method} button for {action} (status {self.status})")

    def meta_token(self):
        match = re.search(r'name="csrf-token" content="([^"]+)"', self.text())
        if not match:
            raise KeyError(f"no csrf-token meta tag (status {self.status})")
        return match.group(1)

    def forms(self):
        return [part.split("</form>")[0] for part in self.text().split("<form")[1:]]


def form_action_matches(form, action):
    head = form.split(">", 1)[0]
    prefixes = [action, f"http://{HOST}{action}"]
    if action.endswith("?"):
        return any(f'action="{p}' in head for p in prefixes)
    return any(f'action="{p}"' in head for p in prefixes)


def token_in(form, action):
    match = re.search(r'name="authenticity_token" value="([^"]+)"', form)
    if not match:
        raise KeyError(f"form for {action} has no authenticity token")
    return match.group(1)


class Browser:
    """One server's user agent: a cookie jar and a client IP (Rails reads X-Forwarded-For from a
    local proxy, so rate limits and bans see it)."""

    def __init__(self, base, ip):
        self.base = urllib.parse.urlparse(base)
        self.cookies = {}
        self.ip = ip

    def request(self, method, path, body=None, headers=None):
        conn = http.client.HTTPConnection(self.base.hostname, self.base.port or 80, timeout=60)
        all_headers = {"Host": HOST, "User-Agent": UA, "X-Forwarded-For": self.ip, "Accept-Encoding": "identity"}
        if self.cookies:
            all_headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in self.cookies.items())
        all_headers.update(headers or {})
        try:
            conn.request(method, path, body=body, headers=all_headers)
            response = conn.getresponse()
            reply = Reply(response.status, response.getheaders(), response.read())
        finally:
            conn.close()
        for cookie in reply.set_cookies():
            name, _, value = cookie.split(";")[0].partition("=")
            lowered = cookie.lower()
            if value == "" or "max-age=0" in lowered or "1970" in lowered:
                self.cookies.pop(name.strip(), None)
            else:
                self.cookies[name.strip()] = value
        return reply

    def get(self, path, headers=None):
        return self.request("GET", path, headers=headers)

    def form(self, method, path, token, fields=(), headers=None):
        pairs = ([("authenticity_token", token)] if token is not None else [])
        if method != "post":
            pairs.append(("_method", method))
        pairs.extend(fields)
        all_headers = {"Content-Type": "application/x-www-form-urlencoded"}
        all_headers.update(headers or {})
        return self.request("POST", path, urllib.parse.urlencode(pairs), all_headers)

    def multipart(self, method, path, token, fields=(), files=()):
        boundary = "----replayboundary7MA4YWxkTrZu0gW"
        parts = [("authenticity_token", token)] + ([("_method", method)] if method != "post" else []) + list(fields)
        body = b""
        for name, value in parts:
            body += f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode()
        for name, filename, content_type, data in files:
            body += (f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
                     f"Content-Type: {content_type}\r\n\r\n").encode() + data + b"\r\n"
        body += f"--{boundary}--\r\n".encode()
        return self.request("POST", path, body, {"Content-Type": f"multipart/form-data; boundary={boundary}"})

    def json(self, method, path, value, token=None, headers=None):
        all_headers = {"Content-Type": "application/json", "Accept": "application/json"}
        if token:
            all_headers["X-CSRF-Token"] = token
        all_headers.update(headers or {})
        return self.request(method, path, json.dumps(value), all_headers)

    def sign_in(self, email, password):
        token = self.get("/session/new").form_token("/session")
        reply = self.form("post", "/session", token, [("email_address", email), ("password", password)])
        if reply.status != 302:
            raise AssertionError(f"sign in as {email} on {self.base.geturl()}: {reply.status}")
        return reply


class Pair:
    """The same user on both servers. Every call runs on the reference, then the candidate, and
    returns both replies; calls taking a token take a pair of them, one per server."""

    def __init__(self, bases, ip):
        self.browsers = [Browser(base, ip) for base in bases]

    def each(self, fn):
        return [fn(b) for b in self.browsers]

    def get(self, path, headers=None):
        return self.each(lambda b: b.get(path, headers))

    def request(self, method, path, body=None, headers=None):
        return self.each(lambda b: b.request(method, path, body, headers))

    def sign_in(self, email, password):
        return self.each(lambda b: b.sign_in(email, password))

    def tokens(self, page, take):
        """take(reply) pulls a token out of each server's own copy of page."""
        return [take(b.get(page)) for b in self.browsers]

    def form(self, method, path, tokens, fields=(), headers=None):
        return [b.form(method, path, t, fields, headers) for b, t in zip(self.browsers, tokens)]

    def multipart(self, method, path, tokens, fields=(), files=()):
        return [b.multipart(method, path, t, fields, files) for b, t in zip(self.browsers, tokens)]

    def json(self, method, path, value, tokens=(None, None), headers=None):
        return [b.json(method, path, value, t, headers) for b, t in zip(self.browsers, tokens)]

    def submit(self, page, action, method="post", fields=(), headers=None):
        """Loads page on each server and submits its form for action with that page's token."""
        return self.form(method, action, self.tokens(page, lambda r: r.form_token(action)), fields, headers)

    def press(self, page, action, method, fields=()):
        """Loads page and presses its button_to for action and method."""
        return self.form(method, action, self.tokens(page, lambda r: r.button_token(action, method)), fields)

    def xhr(self, page, method, path, fields=(), headers=None):
        """A fetch from page's JavaScript: the meta tag's token in X-CSRF-Token."""
        tokens = self.tokens(page, lambda r: r.meta_token())
        return [b.form(method, path, None, fields, dict({"X-CSRF-Token": t}, **(headers or {})))
                for b, t in zip(self.browsers, tokens)]


# --- Comparison -------------------------------------------------------------------------------


class Difference:
    def __init__(self, kind, detail):
        self.kind, self.detail = kind, detail

    def as_dict(self):
        return {"kind": self.kind, "detail": self.detail}


def header_shape(reply, ignored):
    return sorted({k.lower() for k, _ in reply.headers} - TRANSPORT_HEADERS - {"set-cookie"} - ignored)


def header_value(reply, name, options):
    value = reply.header(name)
    if value is None or name in PRESENCE_HEADERS:
        return value if value is None else "«present»"
    if name == "etag":
        return "W/«etag»" if value.startswith("W/") else "«etag»"
    value = CSP_NONCE.sub("'nonce-«nonce»'", value)
    value = BLOB_KEY_FILENAME.sub("«blob_key»", value)
    return normalize.mask_text(value, options)


def cookie_shape(cookie):
    """name, value type and sorted attributes (with values, except expiry instants, which depend on
    when each server answered)."""
    parts = [p.strip() for p in cookie.split(";")]
    name, _, value = parts[0].partition("=")
    attributes = []
    for part in parts[1:]:
        key, sep, val = part.partition("=")
        key = key.lower()
        if key == "expires":
            val = "«time»"
        elif key == "max-age" and val != "0":
            val = "«seconds»" if not val.isdigit() else val
        attributes.append(f"{key}={val}" if sep else key)
    deleted = value == "" or "max-age=0" in cookie.lower() or "1970" in cookie
    return f"{name} {'(deleted)' if deleted else cookie_value_type(value)}: {'; '.join(sorted(attributes))}"


def cookie_value_type(value):
    value = urllib.parse.unquote(value)
    if normalize.ENCRYPTED_MESSAGE.fullmatch(value):
        return "«encrypted»"
    if normalize.SIGNED_TOKEN.fullmatch(value):
        return normalize.describe_signed_token(value, normalize.DEFAULT)
    return value


def differences(reference, candidate, options, body=True, ignored_headers=frozenset()):
    found = []
    if reference.status != candidate.status:
        found.append(Difference("status", f"{reference.status} != {candidate.status}"))
    shapes = header_shape(reference, ignored_headers), header_shape(candidate, ignored_headers)
    if shapes[0] != shapes[1]:
        missing = sorted(set(shapes[0]) - set(shapes[1]))
        extra = sorted(set(shapes[1]) - set(shapes[0]))
        found.append(Difference("header-shape", f"missing {missing}, extra {extra}"))
    for name in sorted(set(shapes[0]) & set(shapes[1])):
        a, b = header_value(reference, name, options), header_value(candidate, name, options)
        if a != b:
            found.append(Difference(f"header:{name}", f"{a!r} != {b!r}"))
    cookies = sorted(map(cookie_shape, reference.set_cookies())), sorted(map(cookie_shape, candidate.set_cookies()))
    if cookies[0] != cookies[1]:
        found.append(Difference("set-cookie", f"{cookies[0]} != {cookies[1]}"))
    if body:
        a = normalize.normalize_body(reference.body, reference.header("content-type"), options)
        b = normalize.normalize_body(candidate.body, candidate.header("content-type"), options)
        if a != b:
            diff = difflib.unified_diff(a.splitlines(), b.splitlines(), "reference", "candidate", lineterm="", n=2)
            found.append(Difference("body", "\n".join(list(diff)[:80])))
    return found


class Run:
    """One replay run: the two servers, the seed's labels, and the results so far."""

    def __init__(self, reference, candidate, seed="default", seed_dir=None, options=None, ignored_headers=(),
                 capture=None, out=sys.stdout):
        self.bases = (reference, candidate)
        self.seed = seed
        self.labels = load_labels(seed, seed_dir)
        normalize.Volatile.seeded = {str(v) for k, v in self.labels.items() if k.startswith(("join_codes.", "bot_keys."))}
        self.options = options or normalize.Options(seed_time=normalize.seed_time_from_labels(self.labels))
        self.ignored_headers = frozenset(h.lower() for h in ignored_headers)
        self.capture = capture
        self.out = out
        self.family = None
        self.results = []

    def label(self, key):
        return self.labels[key]

    @property
    def password(self):
        return self.labels.get("passwords.all", "secret123456")

    def pair(self, ip):
        return Pair(self.bases, ip)

    def signed_in(self, user, ip):
        pair = self.pair(ip)
        pair.sign_in(self.label(f"emails.{user}"), self.password)
        return pair

    def compare(self, name, replies, body=True):
        reference, candidate = replies
        found = differences(reference, candidate, self.options, body, self.ignored_headers)
        result = {"family": self.family, "name": name, "status": [reference.status, candidate.status],
                  "differences": [d.as_dict() for d in found]}
        self.results.append(result)
        if found:
            print(f"✗ [{self.family}] {name}", file=self.out)
            for d in found:
                print(f"    {d.kind}: " + d.detail.replace("\n", "\n    "), file=self.out)
            self.save_capture(name, reference, candidate)
        else:
            print(f"✓ [{self.family}] {name}", file=self.out)
        return replies

    def save_capture(self, name, reference, candidate):
        if not self.capture:
            return
        os.makedirs(self.capture, exist_ok=True)
        slug = re.sub(r"[^A-Za-z0-9]+", "-", f"{self.family}-{len(self.results):03d}-{name}").strip("-")[:120]
        for side, reply in zip(SIDES, (reference, candidate)):
            with open(os.path.join(self.capture, f"{slug}.{side}.txt"), "w") as f:
                f.write(f"HTTP {reply.status}\n")
                for k, v in reply.headers:
                    f.write(f"{k}: {v}\n")
                f.write("\n" + normalize.normalize_body(reply.body, reply.header("content-type"), self.options))

    def failures(self):
        return [r for r in self.results if r["differences"]]

    def summary(self):
        families = {}
        for r in self.results:
            matched, differed = families.get(r["family"], (0, 0))
            families[r["family"]] = (matched + (not r["differences"]), differed + bool(r["differences"]))
        return families


def load_labels(seed, seed_dir=None):
    seed_dir = seed_dir or os.environ.get("PARITY_SEED_DIR") or os.path.join(ROOT, "parity", ".seed")
    path = os.path.join(seed_dir, seed, "labels.json")
    if not os.path.exists(path):
        raise SystemExit(f"replay: {path} is missing; build it with parity/bin/seed build {seed}")
    with open(path) as f:
        return json.load(f)
