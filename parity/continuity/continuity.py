"""Cross-server continuity engine.

Executes continuity scenarios across Rails and C# servers in both directions:
- Rails -> C# (mint/render on Rails, verify/submit/consume on C#)
- C# -> Rails (mint/render on C#, verify/submit/consume on Rails)
"""

import http.client
import json
import re
import urllib.parse
from typing import Dict, List, Optional, Tuple

HOST = "campfire.test"
UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"


def parse_cookie(header: str) -> Tuple[str, str, Dict[str, str]]:
    """Parse a Set-Cookie header into (name, value, attributes)."""
    parts = [p.strip() for p in header.split(";")]
    name, _, value = parts[0].partition("=")
    attributes = {}
    for part in parts[1:]:
        attr_key, _, attr_val = part.partition("=")
        attributes[attr_key.strip().lower()] = attr_val.strip()
    return name.strip(), value, attributes


def is_cookie_deletion(header: str) -> bool:
    """Determine whether a Set-Cookie header deletes a cookie."""
    _, value, attributes = parse_cookie(header)
    if "max-age" in attributes and attributes["max-age"] == "0":
        return True
    if "expires" in attributes and "1970" in attributes["expires"]:
        return True
    return value == ""


class Response:
    def __init__(self, status: int, headers: List[Tuple[str, str]], body: bytes):
        self.status = status
        self.headers = headers
        self.body = body

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", "replace")

    def header(self, name: str) -> Optional[str]:
        values = [v for k, v in self.headers if k.lower() == name.lower()]
        return ", ".join(values) if values else None

    @property
    def location(self) -> Optional[str]:
        return self.header("location")

    def set_cookies(self) -> List[str]:
        return [v for k, v in self.headers if k.lower() == "set-cookie"]


class Client:
    """HTTP client with cookie jar and Rails/C# form helpers."""

    def __init__(self, base_url: str, ip: str = "127.0.0.1"):
        self.base = urllib.parse.urlparse(base_url)
        self.ip = ip
        self.cookies: Dict[str, str] = {}

    def clone(self) -> "Client":
        new_client = self.__class__(self.base.geturl(), self.ip)
        new_client.cookies = dict(self.cookies)
        return new_client

    def new_session(self, ip: Optional[str] = None) -> "Client":
        """Create a new unauthenticated client pointing to the same server."""
        return self.__class__(self.base.geturl(), ip=ip or self.ip)

    def request(self, method: str, path: str, body: Optional[bytes] = None, headers: Optional[Dict[str, str]] = None) -> Response:
        conn = http.client.HTTPConnection(self.base.hostname, self.base.port or 80, timeout=30)
        req_headers = {
            "Host": HOST,
            "User-Agent": UA,
            "X-Forwarded-For": self.ip,
            "Accept-Encoding": "identity",
        }
        if self.cookies:
            req_headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in self.cookies.items())
        if headers:
            req_headers.update(headers)

        try:
            conn.request(method, path, body=body, headers=req_headers)
            res = conn.getresponse()
            response = Response(res.status, res.getheaders(), res.read())
        finally:
            conn.close()

        for c in response.set_cookies():
            name, value, _ = parse_cookie(c)
            if is_cookie_deletion(c):
                self.cookies.pop(name, None)
            else:
                self.cookies[name] = value

        return response

    def get(self, path: str, headers: Optional[Dict[str, str]] = None) -> Response:
        return self.request("GET", path, headers=headers)

    def post(self, path: str, data: Optional[Dict[str, str]] = None, headers: Optional[Dict[str, str]] = None) -> Response:
        all_headers = {"Content-Type": "application/x-www-form-urlencoded"}
        if headers:
            all_headers.update(headers)
        body = urllib.parse.urlencode(data or {}).encode("utf-8")
        return self.request("POST", path, body=body, headers=all_headers)

    def form_submit(self, method: str, path: str, token: Optional[str], fields: Optional[Dict[str, str]] = None, headers: Optional[Dict[str, str]] = None) -> Response:
        data = {}
        if token is not None:
            data["authenticity_token"] = token
        if method.lower() != "post":
            data["_method"] = method.lower()
        if fields:
            data.update(fields)
        return self.post(path, data=data, headers=headers)

    @staticmethod
    def extract_form_token(html: str, action: str) -> Optional[str]:
        """Extract authenticity_token for a form matching action."""
        forms = [part.split("</form>")[0] for part in html.split("<form")[1:]]
        for f in forms:
            head = f.split(">", 1)[0]
            if f'action="{action}"' in head or f'action="http://{HOST}{action}"' in head or f'action="{action}?' in head:
                m = re.search(r'name="authenticity_token"\s+value="([^"]+)"', f)
                if m:
                    return m.group(1)
        return None

    @staticmethod
    def extract_meta_token(html: str) -> Optional[str]:
        """Extract CSRF token from meta tag."""
        m = re.search(r'<meta\s+name="csrf-token"\s+content="([^"]+)"', html)
        return m.group(1) if m else None

    @staticmethod
    def extract_transfer_token(html: str) -> Optional[str]:
        """Extract transfer token from user profile page."""
        m = re.search(r'/session/transfers/([a-zA-Z0-9_\-]+)', html)
        return m.group(1) if m else None

    def login(self, email: str, password: str = "secret123456") -> Response:
        """Sign in via GET /session/new and POST /session."""
        res_new = self.get("/session/new")
        if res_new.status != 200:
            raise AssertionError(f"GET /session/new returned {res_new.status} on {self.base.geturl()}")
        token = self.extract_form_token(res_new.text, "/session")
        if not token:
            raise AssertionError(f"Could not find authenticity token in /session/new form on {self.base.geturl()}")
        res_post = self.post("/session", {"authenticity_token": token, "email_address": email, "password": password})
        if res_post.status != 302:
            raise AssertionError(f"POST /session for {email} returned {res_post.status} on {self.base.geturl()}")
        return res_post


class ScenarioResult:
    def __init__(self, name: str, direction: str, passed: bool, message: str, details: Optional[dict] = None):
        self.name = name
        self.direction = direction
        self.passed = passed
        self.message = message
        self.details = details or {}

    def to_dict(self) -> dict:
        return {
            "name": self.name,
            "direction": self.direction,
            "passed": self.passed,
            "message": self.message,
            "details": self.details,
        }
