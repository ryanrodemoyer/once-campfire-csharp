"""Planted defects: an HTTP proxy that forwards to a server and breaks its responses in one known
way, so a replay against reference and proxy shows whether the harness catches it.

    python3 parity/replay/defects.py --listen 3102 --upstream http://127.0.0.1:3101 --defect missing-class:message--emoji
    parity/replay/run http://127.0.0.1:3100 http://127.0.0.1:3102 --family messages:read     # must report diffs

Defects:
    missing-class:NAME   drop the class NAME from every class attribute in HTML responses
    page-size:-1         drop the last item of every page: the last .message element of a room or a
                         page of its messages, the last element of a top-level JSON array
    page-size:+1         repeat the last item of every page
    status:PATH=CODE     answer CODE instead of the real status for PATH
    header:NAME          drop the response header NAME
"""

import argparse
import http.client
import http.server
import json
import re
import threading
import urllib.parse

HOP_BY_HOP = {"connection", "keep-alive", "transfer-encoding", "content-length", "proxy-connection", "upgrade", "te", "trailer"}
MESSAGE_START = re.compile(r'<div[^>]*\bclass="message\b[^"]*"[^>]*>')
DIV_TAG = re.compile(r"<div\b|</div>")
# A room (/rooms/1, /rooms/1/@2) and a page of its messages (/rooms/1/messages?before=2).
MESSAGE_PAGE = re.compile(r"^/rooms/\d+(/@\d+|/messages)?$")


class Defect:
    def __init__(self, spec):
        self.kind, _, self.arg = spec.partition(":")
        if self.kind not in ("missing-class", "page-size", "status", "header"):
            raise ValueError(f"unknown defect {spec}")

    def apply(self, path, status, headers, body):
        content_type = next((v for k, v in headers if k.lower() == "content-type"), "")
        if self.kind == "status":
            target, _, code = self.arg.partition("=")
            if urllib.parse.urlparse(path).path == target:
                status = int(code)
        elif self.kind == "header":
            headers = [(k, v) for k, v in headers if k.lower() != self.arg.lower()]
        elif self.kind == "missing-class" and "html" in content_type:
            body = drop_class(body.decode("utf-8"), self.arg).encode("utf-8")
        elif self.kind == "page-size" and ("json" in content_type or MESSAGE_PAGE.match(urllib.parse.urlparse(path).path)):
            body = resize_page(body, content_type, int(self.arg))
        return status, headers, body


def drop_class(html, name):
    def rewrite(match):
        classes = match.group(2).split()
        if name not in classes:
            return match.group(0)
        return f'{match.group(1)}"{" ".join(c for c in classes if c != name)}"'
    return re.sub(r'(\bclass=)"([^"]*)"', rewrite, html)


def resize_page(body, content_type, delta):
    if "json" in content_type:
        try:
            value = json.loads(body)
        except ValueError:
            return body
        if isinstance(value, list) and value:
            value = value[:-1] if delta < 0 else value + [value[-1]]
            return json.dumps(value).encode()
        return body
    if "html" not in content_type:
        return body
    html = body.decode("utf-8")
    spans = message_spans(html)
    if not spans:
        return body
    start, end = spans[-1]
    html = html[:start] + html[end:] if delta < 0 else html[:end] + html[start:end] + html[end:]
    return html.encode("utf-8")


def message_spans(html):
    """The (start, end) of each top-level .message element, matching nested divs."""
    spans, at = [], 0
    while True:
        match = MESSAGE_START.search(html, at)
        if not match:
            return spans
        depth, end = 1, match.end()
        for tag in DIV_TAG.finditer(html, match.end()):
            depth += 1 if tag.group(0) == "<div" else -1
            if depth == 0:
                end = tag.end()
                break
        spans.append((match.start(), end))
        at = end


def handler(upstream, defect):
    target = urllib.parse.urlparse(upstream)

    class Proxy(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def forward(self):
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length) if length else None
            conn = http.client.HTTPConnection(target.hostname, target.port, timeout=60)
            headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP_BY_HOP}
            conn.request(self.command, self.path, body=body, headers=headers)
            response = conn.getresponse()
            status, headers, data = defect.apply(self.path, response.status, response.getheaders(), response.read())
            conn.close()
            self.send_response_only(status)
            for k, v in headers:
                if k.lower() not in HOP_BY_HOP:
                    self.send_header(k, v)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = do_HEAD = forward

        def log_message(self, *args):
            pass

    return Proxy


def serve(port, upstream, spec):
    """Starts the proxy on a background thread; returns the server (shutdown() to stop it)."""
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler(upstream, Defect(spec)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--listen", type=int, required=True)
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--defect", required=True)
    args = parser.parse_args()
    http.server.ThreadingHTTPServer(("127.0.0.1", args.listen), handler(args.upstream, Defect(args.defect))).serve_forever()
