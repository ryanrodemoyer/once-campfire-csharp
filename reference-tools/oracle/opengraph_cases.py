# Generates ../opengraph_cases.json: fake DNS, the pages a fake server serves, and the URLs to
# unfurl. Both oracle/opengraph.rb (against the reference) and the Rust tests replay it.
#   python3 crates/campfire/src/integrations/testdata/oracle/opengraph_cases.py
import base64
import json
import os

HTML = [["Content-Type", "text/html"]]
routes = []
cases = []


def page(title="Hey!", desc="desc..", url="https://example.com", image="http://example.com/image.png", extra=""):
    tags = ""
    if url is not None:
        tags += f'<meta property="og:url" content="{url}">'
    if title is not None:
        tags += f'<meta property="og:title" content="{title}">'
    if desc is not None:
        tags += f'<meta property="og:description" content="{desc}">'
    if image is not None:
        tags += f'<meta property="og:image" content="{image}">'
    return "<html><head>" + tags + extra + "</head></html>"


def route(host, path, body=None, status=200, headers=None, method="GET", **kw):
    r = {"method": method, "host": host, "path": path, "status": status,
         "headers": HTML if headers is None else headers}
    if body is not None:
        r["body"] = body
    r.update(kw)
    routes.append(r)


def image(path="/image.png", content_type="image/png", status=200, headers=None):
    if headers is None:
        headers = [["Content-Type", content_type]]
    route("example.com", path, method="HEAD", status=status, headers=headers)


def case(name, url):
    cases.append({"name": name, "url": url})


image()
route("www.example.com", "/", page())
case("success", "http://www.example.com/")
route("www.example.com", "/name", page().replace("property=", "name="))
case("name attribute", "http://www.example.com/name")
route("www.example.com", "/empty", "<html><head></head></html>")
case("missing tags", "http://www.example.com/empty")
route("www.example.com", "/relative-og-url", page(url="/foo"))
case("relative og:url falls back", "http://www.example.com/relative-og-url")
route("www.example.com", "/no-og-url", page(url=None))
case("missing og:url falls back", "http://www.example.com/no-og-url")
route("www.example.com", "/private-og-url", page(url="http://private.example/"))
case("private og:url falls back", "http://www.example.com/private-og-url")

image("/image.svg", "image/svg+xml")
route("www.example.com", "/svg", page(image="http://example.com/image.svg"))
case("svg image dropped", "http://www.example.com/svg")
image("/charset.png", "image/png; charset=binary")
route("www.example.com", "/charset-image", page(image="http://example.com/charset.png"))
case("image content type with params dropped", "http://www.example.com/charset-image")
image("/upper.png", "IMAGE/PNG")
route("www.example.com", "/upper-image", page(image="http://example.com/upper.png"))
case("uppercase image content type accepted", "http://www.example.com/upper-image")
image("/missing.png", status=404)
route("www.example.com", "/404-image", page(image="http://example.com/missing.png"))
case("image HEAD status is not checked", "http://www.example.com/404-image")
image("/redirected.png", status=302, headers=[["Location", "http://example.com/image.png"]])
route("www.example.com", "/redirected-image", page(image="http://example.com/redirected.png"))
case("image HEAD follows redirects", "http://www.example.com/redirected-image")
image("/private-redirect.png", status=302, headers=[["Location", "http://10.0.0.1/image.png"]])
route("www.example.com", "/private-redirect-image", page(image="http://example.com/private-redirect.png"))
case("image redirect to private network dropped", "http://www.example.com/private-redirect-image")
for i, bad in enumerate(["/image.png", "foo", "https/incorrect", "~/etc/password",
                         "http://10.0.0.1/image.png", "ftp://example.com/x.png", "http://example.com/a b.png"]):
    route("www.example.com", f"/bad-image-{i}", page(image=bad))
    case(f"invalid image url {bad}", f"http://www.example.com/bad-image-{i}")
route("www.example.com", "/no-image", page(image=None))
case("no image", "http://www.example.com/no-image")

route("www.example.com", "/script",
      page(title="Hey!&lt;script&gt;alert('hi')&lt;/script&gt;", desc="Hello<script>alert('hi')</script>"))
case("script stripped", "http://www.example.com/script")
enc = ("&#x3c;&#x2f;&#x73;&#x63;&#x72;&#x69;&#x70;&#x74;&#x3e;&#x3c;&#x69;&#x6d;&#x67;&#x20;&#x73;&#x72;&#x63;"
       "&#x3d;&#x61;&#x20;&#x6f;&#x6e;&#x65;&#x72;&#x72;&#x6f;&#x72;&#x3d;&#x70;&#x72;&#x6f;&#x6d;&#x70;&#x74;"
       "&#x28;&#x31;&#x29;&#x3e;")
route("www.example.com", "/encoded", page(title="Hey!" + enc, desc=enc + "desc.."))
case("encoded tags removed", "http://www.example.com/encoded")
markup = "<img src='x' onerror='alert(document.domain)'/>"
route("www.example.com", "/only-markup", page(title=markup, desc=markup))
case("only markup is blank", "http://www.example.com/only-markup")
route("www.example.com", "/entities", page(title="Tom &amp; Jerry &lt;3 &nbsp;x", desc="a &gt; b \"q\" 'a'"))
case("entities are escaped", "http://www.example.com/entities")
route("www.example.com", "/whitespace", page(title="   ", desc="  x  "))
case("whitespace title skipped", "http://www.example.com/whitespace")

route("www.example.com", "/plain", "I'm not HTML!", headers=[["Content-Type", "text/plain"]])
case("non html", "http://www.example.com/plain")
route("www.example.com", "/no-content-type", page(), headers=[])
case("missing content type", "http://www.example.com/no-content-type")
route("www.example.com", "/forbidden", page(), status=403)
case("403", "http://www.example.com/forbidden")
route("www.example.com", "/created", page(), status=201)
case("201 is not OK", "http://www.example.com/created")
route("www.example.com", "/html-charset", page(), headers=[["Content-Type", "Text/HTML ; charset=utf-8"]])
case("content type params and case", "http://www.example.com/html-charset")

route("www.example.com", "/redirect", status=302, headers=[["Location", "http://www.other.com/"]])
route("www.other.com", "/", page(title="Other"))
case("redirect followed", "http://www.example.com/redirect")
route("www.example.com", "/relative-redirect", status=301, headers=[["Location", "/"]])
case("relative redirect denied", "http://www.example.com/relative-redirect")
route("www.example.com", "/redirect-no-location", status=302, headers=[])
case("redirect without location", "http://www.example.com/redirect-no-location")
route("www.example.com", "/not-modified", status=304, headers=[])
case("304 is a redirection", "http://www.example.com/not-modified")
route("www.example.com", "/redirect-private-host", status=302, headers=[["Location", "http://private.example/"]])
case("redirect to private host", "http://www.example.com/redirect-private-host")
route("www.example.com", "/redirect-private-ip", status=307,
      headers=[["Location", "http://169.254.169.254/latest/meta-data/"]])
case("redirect to link-local ip", "http://www.example.com/redirect-private-ip")
route("www.example.com", "/redirect-localhost", status=302, headers=[["Location", "http://localhost/"]])
case("redirect to localhost", "http://www.example.com/redirect-localhost")
route("www.example.com", "/redirect-ftp", status=302, headers=[["Location", "ftp://www.other.com/"]])
case("redirect to ftp", "http://www.example.com/redirect-ftp")
route("www.example.com", "/loop", status=302, headers=[["Location", "http://www.example.com/loop"]])
case("redirect loop", "http://www.example.com/loop")
for i in range(0, 10):
    route("www.example.com", f"/chain/{i}", status=302, headers=[["Location", f"http://www.example.com/chain/{i + 1}"]])
route("www.example.com", "/chain/10", page(title="Chain end"))
case("nine redirects are followed", "http://www.example.com/chain/1")
case("ten redirects are too many", "http://www.example.com/chain/0")
route("www.example.com", "/rebind-redirect", status=302, headers=[["Location", "http://rebind.example/"]])
route("rebind.example", "/", page(title="Pinned"))
case("dns rebinding after redirect is pinned", "http://www.example.com/rebind-redirect")
case("dns rebinding is pinned", "http://rebind.example/")

route("www.example.com", "/big", body_repeat=["x", 5 * 1024 * 1024 + 1], chunked=True)
case("large chunked body", "http://www.example.com/big")
route("www.example.com", "/big-length", body="x" * 16,
      headers=[["Content-Type", "text/html"], ["Content-Length", str(5 * 1024 * 1024 + 1)]])
case("large content length", "http://www.example.com/big-length")
exact = page(title="Exact")
route("www.example.com", "/exact", body=exact, pad_to=5 * 1024 * 1024, chunked=True)
case("body of exactly 5MB", "http://www.example.com/exact")
route("www.example.com", "/gzip", page(title="Zipped"), gzip=True)
case("gzip body", "http://www.example.com/gzip")

case("private host", "http://private.example/")
case("localhost", "http://localhost/")
case("loopback literal", "http://127.0.0.1/")
case("10/8 literal", "http://10.1.2.3/")
case("link-local metadata", "http://169.254.169.254/latest/meta-data/")
case("ipv6 loopback", "http://[::1]/")
case("ipv6 ula", "http://[fd00::1]/")
case("ipv4 mapped ipv6", "http://[::ffff:192.168.1.1]/")
case("decimal ip", "http://2130706433/")
case("hex ip", "http://0x7f.1/")
case("unresolvable", "http://nowhere.example/")
case("mapped private dns answer", "http://mapped.example/")
route("mixed.example", "/", page(title="Mixed"))
case("mixed dns answers use the public one", "http://mixed.example/")
route("ipv6.example", "/", page(title="IPv6"))
case("ipv6 public host", "http://ipv6.example/")
case("underscore host", "http://under_score.example/")

case("media url not fetched", "http://www.example.com/video.mp4")
case("archive url not fetched", "http://www.example.com/archive.tar.gz?x=1")
route("www.example.com", "/archive.tar.gzip", page(title="Not an archive"))
case("extension needs a word boundary", "http://www.example.com/archive.tar.gzip")
route("www.example.com", "/UPPER.MP4", page(title="Upper"))
case("extension match is case sensitive", "http://www.example.com/UPPER.MP4")
case("ftp url", "ftp://www.example.com/")
case("not a url", "httpfake")
case("space", " foo")
case("non ascii url", "http://www.example.com/é")
case("mailto raises", "mailto:foo")
case("uppercase scheme", "HTTP://www.example.com/")
route("www.example.com", "/port", page(title="Port"))
case("explicit port", "http://www.example.com:8080/port")
route("www.example.com", "/query?a=1&b=2", page(title="Query"))
case("query string and fragment", "http://www.example.com/query?a=1&b=2#frag")

route("fxtwitter.com", "/dhh/status/1",
      "<html><head><meta property=\"og:title\" content=\"DHH 😀 “quoted”\">"
      "<meta property=\"og:description\" content=\"tweet é\"></head></html>")
case("twitter", "http://twitter.com/dhh/status/1")
case("x.com", "http://x.com/dhh/status/1")
case("www.x.com", "http://www.x.com/dhh/status/1")
route("twitter.com", "/", page(title="Twitter root"))
case("twitter root is not a tweet", "http://twitter.com/")
case("fxtwitter failure raises", "http://twitter.com/missing/status/2")

route("www.example.com", "/utf8-no-meta",
      "<html><head><meta property=\"og:title\" content=\"Café 😀 ok\">"
      "<meta property=\"og:description\" content=\"naïve &eacute; &#233; x\"></head></html>")
case("non-ascii stripped without meta charset", "http://www.example.com/utf8-no-meta")
route("www.example.com", "/utf8-meta",
      "<html><head><meta charset=\"utf-8\"><meta property=\"og:title\" content=\"Café 😀 ok\">"
      "<meta property=\"og:description\" content=\"naïve &eacute; x\"></head></html>")
case("non-ascii kept with meta charset", "http://www.example.com/utf8-meta")
route("www.example.com", "/http-equiv",
      "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\">"
      "<meta property=\"og:title\" content=\"Café\"><meta property=\"og:description\" content=\"d\"></head></html>")
case("http-equiv charset", "http://www.example.com/http-equiv")
latin1 = ("<html><head><meta charset=\"iso-8859-1\"><meta property=\"og:title\" content=\"Caf\xe9\">"
          "<meta property=\"og:description\" content=\"d\"></head></html>").encode("latin-1")
route("www.example.com", "/latin1", body_b64=base64.b64encode(latin1).decode())
case("latin-1 with meta charset", "http://www.example.com/latin1")
invalid = (b"<html><head><meta charset=\"utf-8\"><meta property=\"og:title\" content=\"Bad \xff\xfe bytes\">"
           b"<meta property=\"og:description\" content=\"d\"></head></html>")
route("www.example.com", "/invalid-utf8", body_b64=base64.b64encode(invalid).decode())
case("invalid utf-8 with meta charset", "http://www.example.com/invalid-utf8")
route("www.example.com", "/duplicates",
      "<html><head><meta property=\"og:image\" content=\"http://example.com/image.png\">"
      "<meta property=\"og:title\" content=\"First\"><meta property=\"og:description\" content=\"d\">"
      "<meta property=\"og:title\" content=\"Second\"><meta property=\"og:type\" content=\"article\"></head></html>")
case("duplicates and key order", "http://www.example.com/duplicates")
route("www.example.com", "/mixed-attrs",
      "<html><head><meta property=\"description\" name=\"og:title\" content=\"Wrong\">"
      "<meta name=\"og:title\" content=\"Right\"><meta property=\"og:description\" content=\"d\">"
      "<meta property=\"OG:title\" content=\"Upper\"></head></html>")
case("property wins over name", "http://www.example.com/mixed-attrs")
route("www.example.com", "/body-meta",
      "<html><head></head><body><div><meta property=\"og:title\" content=\"In body\">"
      "<meta property=\"og:description\" content=\"d\"></div></body></html>")
case("meta in body", "http://www.example.com/body-meta")
route("www.example.com", "/og-og",
      "<html><head><meta property=\"og:og:title\" content=\"Doubled\">"
      "<meta property=\"og:description\" content=\"d\"></head></html>")
case("og: removed everywhere", "http://www.example.com/og-og")
route("www.example.com", "/unicode-space", page(title="  ", desc="d", extra="<meta charset=\"utf-8\">"))
case("unicode space title with meta charset", "http://www.example.com/unicode-space")
route("www.example.com", "/json-escapes",
      "<html><head><meta charset=\"utf-8\"><meta property=\"og:title\" content=\"a b &lt;/script&gt; c\">"
      "<meta property=\"og:description\" content=\"line\nbreak\ttab\">"
      "<meta property=\"og:url\" content=\"http://example.com/?a=1&amp;b=<2>\"></head></html>")
case("json escapes", "http://www.example.com/json-escapes")

doc = {
    "hosts": {
        "www.example.com": [["93.184.216.34"]],
        "example.com": [["93.184.216.35"]],
        "www.other.com": [["93.184.216.36"]],
        "fxtwitter.com": [["93.184.216.37"]],
        "twitter.com": [["93.184.216.38"]],
        "mixed.example": [["10.0.0.9", "::1", "93.184.216.39"]],
        "rebind.example": [["93.184.216.40"], ["127.0.0.1"]],
        "ipv6.example": [["2606:2800:220:1:248:1893:25c8:1946"]],
        "private.example": [["192.168.1.10"]],
        "mapped.example": [["::ffff:10.0.0.1"]],
        "localhost": [["127.0.0.1", "::1"]],
    },
    "public_ips": ["93.184.216.34", "93.184.216.35", "93.184.216.36", "93.184.216.37", "93.184.216.38",
                   "93.184.216.39", "93.184.216.40", "2606:2800:220:1:248:1893:25c8:1946"],
    "routes": routes,
    "cases": cases,
}
out = os.path.join(os.path.dirname(__file__), "..", "opengraph_cases.json")
with open(out, "w") as f:
    json.dump(doc, f, indent=1, ensure_ascii=False)
    f.write("\n")
print(f"{len(cases)} cases, {len(routes)} routes")
