"""Normalized responses for the replay diff. Ported from the Rust port's
reference-rust/parity/capture/normalize.ts, in Python's standard library so the harness runs
anywhere the parity tools do.

A response body becomes text that diffs like its DOM: one node per line, sorted attributes,
collapsed whitespace, entities decoded. Values that legitimately differ between two servers on the
same seed become *typed* placeholders rather than blanks, so a token for the wrong record still
differs:

    «csrf»                       a well-formed masked authenticity token (64 bytes, Rails' mask_token)
    «sgid:Room#486777696»        a signed GlobalID, decoded
    «signed_id:transfer:7:exp»   ActiveRecord::SignedId and other MessageVerifier output, decoded
    «encrypted»                  MessageEncryptor output (the session cookie)
    «t-3600s»  «epochms-60s»     timestamps decoded relative to the seed clock
    «t:live»                     a timestamp after the seed clock: written during the run, whose
                                 exact value depends on when each server rendered it
    «uuid» «join_code» «bot_key» values that are random by design (SecureRandom, has_secure_token)

Unlike normalize.ts, CSRF fields are kept (masked): the C# port renders them exactly as Rails does.
The Rust-only "Copy link" rewrite is not ported.
"""

import base64
import binascii
import hashlib
import html.parser
import json
import re
import urllib.parse
from datetime import datetime, timezone

RAW_TEXT = {"pre", "textarea", "listing", "plaintext", "xmp"}
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}
FOREIGN = {"svg", "math"}

# The WHATWG implied end tags the tree builder honours: a start tag of the key closes an open element
# of one of the values when it is the current node. (Rails' views always close their elements; this
# keeps a hand-written `<li>…<li>` from nesting.)
IMPLIED_END = {
    "li": {"li"},
    "dt": {"dt", "dd"},
    "dd": {"dt", "dd"},
    "option": {"option"},
    "optgroup": {"option", "optgroup"},
    "tr": {"tr", "td", "th"},
    "td": {"td", "th"},
    "th": {"td", "th"},
}
CLOSES_P = {"address", "article", "aside", "blockquote", "details", "dialog", "div", "dl", "fieldset", "figcaption",
            "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "main", "menu",
            "nav", "ol", "p", "pre", "section", "table", "ul"}


class Options:
    """seed_time: the seed's clock.now in epoch ms, which timestamps are decoded against.
    frozen_clock: both servers run with a frozen clock (`reference up --freeze`), so a time written
    during the run is exact too and is decoded instead of reported as «t:live».
    keep_asset_digests: compare Propshaft digests instead of masking them."""

    def __init__(self, seed_time=None, frozen_clock=False, keep_asset_digests=False):
        self.seed_time = seed_time
        self.frozen_clock = frozen_clock
        self.keep_asset_digests = keep_asset_digests


DEFAULT = Options()


# --- Tree -------------------------------------------------------------------------------------


class Node:
    def __init__(self, tag, attrs=(), foreign=False):
        self.tag, self.attrs, self.children, self.foreign = tag, list(attrs), [], foreign


class TreeBuilder(html.parser.HTMLParser):
    """A small HTML tree builder: void elements, ignored self-closing slashes on HTML elements
    (honoured in SVG and MathML), implied end tags and stray end tags dropped, as a browser does.
    It does not synthesize <html>, <head> or <body>; both servers' bytes go through the same builder,
    so that only matters for markup Rails never produces."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = Node("#root")
        self.stack = [self.root]

    @property
    def current(self):
        return self.stack[-1]

    def handle_starttag(self, tag, attrs, self_closing=False):
        foreign = self.current.foreign or tag in FOREIGN
        if not foreign:
            closes = IMPLIED_END.get(tag, set()) | ({"p"} if tag in CLOSES_P else set())
            if self.current.tag in closes:
                self.stack.pop()
        seen, unique = set(), []
        for name, value in attrs:
            if name not in seen:  # the first of duplicate attributes wins
                seen.add(name)
                unique.append((name, value or ""))
        node = Node(tag, unique, foreign)
        self.current.children.append(node)
        if tag not in VOID and not (self_closing and foreign):
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs, self_closing=True)

    def handle_endtag(self, tag):
        for depth in range(len(self.stack) - 1, 0, -1):
            if self.stack[depth].tag == tag:
                del self.stack[depth:]
                return

    def handle_data(self, data):
        self.current.children.append(("#text", data))

    def handle_comment(self, data):
        self.current.children.append(("#comment", data))

    def handle_decl(self, decl):
        self.current.children.append(("#doctype", decl.split()[1] if len(decl.split()) > 1 else decl))

    def unknown_decl(self, data):
        self.current.children.append(("#text", data))


def parse(text):
    builder = TreeBuilder()
    builder.feed(text)
    builder.close()
    return builder.root


# --- Serialization ----------------------------------------------------------------------------


def normalize_html(text, options=DEFAULT):
    lines = []
    for child in parse(text).children:
        walk(child, 0, lines, options, False)
    return "\n".join(lines) + "\n"


def walk(node, depth, out, options, raw):
    indent = "  " * depth
    if isinstance(node, tuple):
        kind, value = node
        if kind == "#doctype":
            out.append(f"{indent}<!DOCTYPE {value.lower()}>")
        elif kind == "#comment":
            text = collapse(value)
            if text:
                out.append(f"{indent}<!-- {text} -->")
        elif raw:
            if value:
                out.append(indent + json.dumps(mask_text(value, options), ensure_ascii=False))
        else:
            text = collapse(value)
            if text:
                out.append(indent + mask_text(text, options))
        return
    attrs = "".join(format_attr(name, mask_attr(node, name, value, options)) for name, value in sorted(node.attrs))
    out.append(f"{indent}<{node.tag}{attrs}>")
    if node.tag in ("script", "style"):
        text = "".join(c[1] for c in node.children if isinstance(c, tuple))
        kind = attr(node, "type") or ""
        body = normalize_json_text(text) if node.tag == "script" and re.search("importmap|json", kind) else collapse(text)
        if body:
            out.append(f"{indent}  {mask_text(body, options)}")
    else:
        for child in node.children:
            walk(child, depth + 1, out, options, raw or node.tag in RAW_TEXT)
    if node.tag not in VOID:
        out.append(f"{indent}</{node.tag}>")


def format_attr(name, value):
    return f" {name}" if value == "" else f" {name}={json.dumps(value, ensure_ascii=False)}"


def attr(node, name):
    return next((v for n, v in node.attrs if n == name), None)


def collapse(text):
    return re.sub(r"[\t\n\f\r ]+", " ", text).strip()


def normalize_json_text(text):
    try:
        return json.dumps(json.loads(text), ensure_ascii=False, separators=(",", ":"))
    except ValueError:
        return collapse(text)


# --- Masking ----------------------------------------------------------------------------------


def mask_attr(node, name, value, options):
    if name == "nonce" or (node.tag == "meta" and attr(node, "name") == "csp-nonce" and name == "content"):
        return "«nonce»"
    if node.tag == "input" and name == "value" and attr(node, "name") == "authenticity_token":
        return describe_csrf(value)
    if node.tag == "meta" and name == "content" and attr(node, "name") == "csrf-token":
        return describe_csrf(value)
    return relative_epoch(name, value, options) or mask_text(value, options)


def describe_csrf(token):
    """ActionController::RequestForgeryProtection#mask_token: urlsafe base64 of a 32-byte one-time
    pad and the 32-byte token XOR the pad. Each render differs, so only its shape is compared."""
    try:
        raw = base64.urlsafe_b64decode(token + "=" * (-len(token) % 4))
    except (binascii.Error, ValueError):
        raw = b""
    return "«csrf»" if len(raw) == 64 else f"«csrf:malformed:{token}»"


# URL.createObjectURL: a random UUID per call (upload previews).
BLOB_URL = re.compile(r"blob:[a-z]+://[^/\s\"]+/[0-9a-f-]{36}")
# QrCodeController takes the URL to encode as base64 in the path.
QR_CODE = re.compile(r"/qr_code/([A-Za-z0-9_=-]{8,})")
# Propshaft: /assets/name-<7+ hex>.ext.
ASSET_DIGEST = re.compile(r"/assets/([^\s\"'()?#]+?)-([0-9a-f]{7,64})(\.[A-Za-z0-9.]+)")
# ActiveSupport::MessageVerifier output: base64(data)--hexdigest, possibly URL-escaped.
SIGNED_TOKEN = re.compile(r"(?<![A-Za-z0-9+%_-])((?:[A-Za-z0-9+/_-]|%2[BbFf]|%3[Dd])+={0,2}(?:%3[Dd]){0,2})--([0-9a-f]{40,128})(?![0-9a-f])")
# ActiveSupport::MessageEncryptor output: base64--base64--base64.
ENCRYPTED_MESSAGE = re.compile(r"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{16,}={0,2}--[A-Za-z0-9+/]{12,}={0,2}--[A-Za-z0-9+/]{16,}={0,2}(?![A-Za-z0-9+/])")
# to_fs(:number) cache busters: fresh_account_logo_path and fresh_user_avatar_path add ?v=updated_at.
NUMBER_TIME = re.compile(r"(?<=[?&]v=)(\d{14})\b")
ISO_TIME = re.compile(r"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})\b")
# SecureRandom.uuid (request ids, Active Storage direct upload keys).
UUID = re.compile(r"\b[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b")
# Account#join_code: SecureRandom.alphanumeric(12).scan(/.{4}/).join("-") after a reset.
JOIN_CODE = re.compile(r"/join/([A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4})\b")
# User#bot_key is "#{id}-#{bot_token}", bot_token a has_secure_token(length: 12) for bots created
# during the run.
BOT_KEY = re.compile(r"/(\d+)-([A-Za-z0-9]{12})(?=/messages)")


# Signed values whose payload is a has_secure_token minted at sign-in (Session#token, set by
# app/controllers/concerns/authentication.rb as cookies.signed.permanent[:session_token]).
RANDOM_PAYLOADS = {"cookie.session_token"}


class Volatile:
    """Values random by design that the seed fixes: a seeded join code or bot key is compared as
    is, one made during the run (a reset join code, a new bot) is typed."""

    seeded = set()


def mask_text(text, options=DEFAULT):
    text = QR_CODE.sub(lambda m: f"/qr_code/«qr:{mask_text(decode_text(m.group(1)) or m.group(1), options)}»", text)
    text = ISO_TIME.sub(lambda m: relative_iso(m.group(0), options), text)
    text = NUMBER_TIME.sub(lambda m: relative_number(m.group(1), options), text)
    text = SIGNED_TOKEN.sub(lambda m: describe_signed_token(m.group(0), options), text)
    text = ENCRYPTED_MESSAGE.sub("«encrypted»", text)
    text = BLOB_URL.sub("blob:«object-url»", text)
    text = UUID.sub("«uuid»", text)
    text = JOIN_CODE.sub(lambda m: m.group(0) if m.group(1) in Volatile.seeded else "/join/«join_code»", text)
    text = BOT_KEY.sub(lambda m: m.group(0) if m.group(0)[1:] in Volatile.seeded else f"/{m.group(1)}-«bot_token»", text)
    if not options.keep_asset_digests:
        text = ASSET_DIGEST.sub(lambda m: f"/assets/{m.group(1)}-«digest»{m.group(3)}", text)
    return text


def describe_signed_token(token, options):
    """Standard base64 includes "/", so a match can swallow the URL path in front of the token
    (/rails/active_storage/blobs/redirect/<token>). Keep the longest suffix that decodes."""
    encoded = token.split("--")[0]
    starts = [0] + [m.start() + 1 for m in re.finditer("/", encoded)]
    for start in starts:
        description = describe_signed_message(encoded[start:], options)
        if description:
            return encoded[:start] + description
    return encoded[:starts[-1]] + "«signed»"


def describe_signed_message(encoded, options):
    data = decode_base64(urllib.parse.unquote(encoded))
    if data is None:
        return None
    text = data.decode("latin-1")
    value = parse_json(text)
    envelope = value.get("_rails") if isinstance(value, dict) else None
    if envelope:
        payload = envelope.get("data")
        if payload is None and isinstance(envelope.get("message"), str):
            inner = decode_text(envelope["message"])
            payload = (parse_json(inner) if parse_json(inner) is not None else inner) if inner is not None else envelope["message"]
        purpose = envelope.get("pur") or "default"
        expires = f":{describe_expiry(envelope['exp'], options)}" if envelope.get("exp") else ""
        gid = describe_gid(payload) if isinstance(payload, str) else None
        if gid:
            return f"«sgid:{'' if purpose == 'default' else purpose + ':'}{gid}{expires}»"
        shown = payload if isinstance(payload, str) else json.dumps(payload, separators=(",", ":"))
        if purpose in RANDOM_PAYLOADS:
            shown = "«token»"
        return f"«signed_id:{purpose}:{shown}{expires}»"
    # Marshal-era messages (\x04\x08) and bare ones: pull out a GlobalID if there is one.
    gid = describe_gid(text)
    if gid:
        return f"«sgid:{gid}»"
    # Turbo signed stream names: a JSON string of ":"-joined GlobalID params (base64 gid://…).
    if isinstance(value, str):
        return "«stream:" + ":".join(describe_gid_param(p) for p in value.split(":")) + "»"
    if value is not None:
        return f"«signed:{json.dumps(value, separators=(',', ':'))}»"
    if text.startswith("\x04\x08"):
        return "«signed:marshal»"
    return None


def describe_expiry(exp, options):
    """An expiry is "time of rendering + lifetime". With a ticking clock the time of rendering
    isn't observable, so only an expiry's presence is compared; with a frozen clock it's decoded."""
    return "exp" + relative_iso(exp, options) if options.frozen_clock else "expires"


def describe_gid_param(part):
    decoded = decode_text(part)
    return (decoded and describe_gid(decoded)) or part


def describe_gid(text):
    m = re.search(r"gid://[^/\s]+/([A-Za-z:]+)/([^\s?\"\x00-\x1f]+)", text)
    return f"{m.group(1)}#{urllib.parse.unquote(m.group(2))}" if m else None


def relative_iso(iso, options):
    if options.seed_time is None:
        return iso
    try:
        t = datetime.fromisoformat(iso.replace("Z", "+00:00"))
    except ValueError:
        return iso
    ms = round(t.timestamp() * 1000)
    return describe_time("t", ms, options)


def relative_number(number, options):
    if options.seed_time is None:
        return number
    try:
        t = datetime.strptime(number, "%Y%m%d%H%M%S").replace(tzinfo=timezone.utc)
    except ValueError:
        return number
    return describe_time("number", round(t.timestamp() * 1000), options)


def describe_time(kind, ms, options):
    delta = ms - options.seed_time
    if delta > 0 and not options.frozen_clock:
        return f"«{kind}:live»"
    return f"«{kind}{format_delta(delta)}»"


# data-message-timestamp and friends are epoch milliseconds (config/initializers/time_formats.rb).
def relative_epoch(name, value, options):
    if options.seed_time is None or not re.fullmatch(r"\d{10}(\d{3})?", value):
        return None
    ms = int(value) if len(value) == 13 else int(value) * 1000
    plausible = abs(ms - options.seed_time) < 20 * 365 * 86_400_000
    timeish = len(value) == 13 or re.search(r"(_at|-at|time|date|timestamp|sort|number|expires)", name, re.I)
    if not (plausible and timeish):
        return None
    return describe_time("epochms" if len(value) == 13 else "epochs", ms, options)


def format_delta(ms):
    sign = "-" if ms < 0 else "+"
    ms = abs(ms)
    return f"{sign}{ms // 1000}s" if ms % 1000 == 0 else f"{sign}{ms / 1000:.3f}s"


def decode_base64(text):
    if not re.fullmatch(r"[A-Za-z0-9+/_-]+={0,2}", text or ""):
        return None
    try:
        data = base64.b64decode(text.replace("-", "+").replace("_", "/") + "=" * (-len(text.rstrip("=")) % 4), validate=False)
    except (binascii.Error, ValueError):
        return None
    return data or None


def decode_text(text):
    data = decode_base64(text)
    if data is None:
        return None
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return None


def parse_json(text):
    try:
        return json.loads(text)
    except (ValueError, TypeError):
        return None


# --- Responses --------------------------------------------------------------------------------


def normalize_body(body, content_type, options=DEFAULT):
    """Documents and fragments (HTML, Turbo Streams, SVG) as trees, JSON with sorted keys, other text
    masked, anything else as its length and digest."""
    content_type = (content_type or "").lower()
    text = body.decode("utf-8", "replace")
    if "json" in content_type:
        value = parse_json(text)
        if value is not None:
            return json.dumps(mask_json(value, options), indent=2, ensure_ascii=False, sort_keys=True) + "\n"
    if re.search("html|xml|svg|turbo-stream", content_type):
        return normalize_html(text, options)
    if re.match(r"(text|application/(javascript|manifest))", content_type):
        return mask_text(text, options)
    if not body:
        return ""
    return f"«{len(body)} bytes sha256:{hashlib.sha256(body).hexdigest()}»\n"


def mask_json(value, options, key=""):
    if isinstance(value, list):
        return [mask_json(v, options, key) for v in value]
    if isinstance(value, dict):
        return {k: mask_json(v, options, k) for k, v in value.items()}
    if isinstance(value, str):
        return mask_text(value, options)
    if isinstance(value, int) and not isinstance(value, bool):
        return relative_epoch(key, str(value), options) or value
    return value


def seed_time_from_labels(labels):
    """labels.json's clock.now ("2026-03-02 16:00:00 UTC" or ISO 8601) in epoch ms."""
    now = labels.get("clock.now") or (labels.get("clock") or {}).get("now")
    if not now:
        return None
    now = now.replace(" UTC", "+00:00").replace(" ", "T", 1).replace("Z", "+00:00")
    t = datetime.fromisoformat(now)
    if t.tzinfo is None:
        t = t.replace(tzinfo=timezone.utc)
    return round(t.timestamp() * 1000)
