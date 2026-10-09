"""Raw WebSocket client implementing RFC 6455 and Action Cable protocol."""

import base64
import hashlib
import json
import os
import select
import socket
import struct
import urllib.parse
from typing import Any, Dict, List, Optional, Tuple, Union

# Action Cable subprotocols
ACTIONCABLE_V1_JSON = "actioncable-v1-json"


class WebSocketError(Exception):
    """WebSocket protocol error."""
    pass


class WebSocketClosed(WebSocketError):
    """WebSocket connection was closed."""
    pass


class RawWebSocketClient:
    """Raw WebSocket client speaking RFC 6455 directly over sockets."""

    def __init__(
        self,
        url: str,
        cookie: Optional[str] = None,
        origin: Optional[str] = None,
        subprotocol: str = ACTIONCABLE_V1_JSON,
        timeout: float = 10.0,
    ):
        self.url = url
        self.cookie = cookie
        self.origin = origin
        self.subprotocol = subprotocol
        self.timeout = timeout
        self.sock: Optional[socket.socket] = None
        self.is_closed = False
        self.close_code: Optional[int] = None
        self.close_reason: str = ""
        self._buffer = bytearray()
        self.raw_frames_received: List[str] = []

    def connect(self) -> None:
        """Establishes TCP connection and performs WebSocket upgrade handshake."""
        parsed = urllib.parse.urlparse(self.url)
        scheme = parsed.scheme.lower()
        if scheme in ("ws", "http"):
            use_ssl = False
            default_port = 80
        elif scheme in ("wss", "https"):
            use_ssl = True
            default_port = 443
        else:
            raise ValueError(f"Unsupported URL scheme: {scheme}")

        host = parsed.hostname or "127.0.0.1"
        port = parsed.port or default_port
        path = parsed.path or "/cable"
        if parsed.query:
            path += "?" + parsed.query

        # Create socket
        raw_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        raw_sock.settimeout(self.timeout)
        raw_sock.connect((host, port))

        if use_ssl:
            import ssl
            context = ssl.create_default_context()
            self.sock = context.wrap_socket(raw_sock, server_hostname=host)
        else:
            self.sock = raw_sock

        # Generate WebSocket Key
        ws_key = base64.b64encode(os.urandom(16)).decode("ascii")

        # Build HTTP Upgrade request
        headers = [
            f"GET {path} HTTP/1.1",
            f"Host: {host}:{port}" if port != default_port else f"Host: {host}",
            "Upgrade: websocket",
            "Connection: Upgrade",
            f"Sec-WebSocket-Key: {ws_key}",
            "Sec-WebSocket-Version: 13",
        ]
        if self.subprotocol:
            headers.append(f"Sec-WebSocket-Protocol: {self.subprotocol}")
        if self.origin:
            headers.append(f"Origin: {self.origin}")
        else:
            default_origin = f"http://{host}:{port}" if port != default_port else f"http://{host}"
            headers.append(f"Origin: {default_origin}")
        if self.cookie:
            headers.append(f"Cookie: {self.cookie}")
        headers.append("\r\n")

        req_bytes = "\r\n".join(headers).encode("latin-1")
        self.sock.sendall(req_bytes)

        # Read response headers
        resp_data = bytearray()
        while b"\r\n\r\n" not in resp_data:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise WebSocketError("Connection closed before HTTP upgrade response received")
            resp_data.extend(chunk)

        header_end = resp_data.find(b"\r\n\r\n")
        header_text = resp_data[:header_end].decode("latin-1", errors="replace")
        self._buffer = bytearray(resp_data[header_end + 4:])

        lines = header_text.split("\r\n")
        status_line = lines[0]
        parts = status_line.split(" ", 2)
        if len(parts) < 2:
            raise WebSocketError(f"Invalid HTTP response status line: {status_line}")
        status_code = int(parts[1])

        if status_code != 101:
            self.sock.close()
            self.is_closed = True
            raise WebSocketError(f"Upgrade failed with HTTP status {status_code}: {status_line}")

        # Verify Sec-WebSocket-Accept
        expected_accept = base64.b64encode(
            hashlib.sha1((ws_key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()
        ).decode("ascii")

        resp_headers = {}
        for line in lines[1:]:
            if ":" in line:
                k, v = line.split(":", 1)
                resp_headers[k.strip().lower()] = v.strip()

        accept_header = resp_headers.get("sec-websocket-accept", "")
        if accept_header != expected_accept:
            raise WebSocketError(
                f"Sec-WebSocket-Accept mismatch: expected {expected_accept}, got {accept_header}"
            )

    def send_frame(self, opcode: int, payload: bytes) -> None:
        """Sends an RFC 6455 masked WebSocket frame."""
        if self.is_closed or not self.sock:
            raise WebSocketClosed("Cannot send on closed WebSocket")

        b1 = 0x80 | (opcode & 0x0F)  # FIN=1 + opcode
        length = len(payload)

        # Client-to-server frames MUST be masked (0x80 bit)
        if length < 126:
            header = bytearray([b1, 0x80 | length])
        elif length < 65536:
            header = bytearray([b1, 0x80 | 126]) + struct.pack("!H", length)
        else:
            header = bytearray([b1, 0x80 | 127]) + struct.pack("!Q", length)

        mask = os.urandom(4)
        header.extend(mask)

        # Mask payload
        masked_payload = bytearray(length)
        for i in range(length):
            masked_payload[i] = payload[i] ^ mask[i % 4]

        self.sock.sendall(header + masked_payload)

    def send_text(self, text: str) -> None:
        """Sends a text message (opcode 0x1)."""
        self.send_frame(0x1, text.encode("utf-8"))

    def send_json(self, data: Any) -> None:
        """Encodes object as JSON and sends text frame."""
        self.send_text(json.dumps(data, separators=(",", ":")))

    def subscribe(self, identifier: Union[str, Dict[str, Any]]) -> None:
        """Sends Action Cable subscribe command."""
        id_str = json.dumps(identifier, separators=(",", ":")) if isinstance(identifier, dict) else identifier
        self.send_json({"command": "subscribe", "identifier": id_str})

    def unsubscribe(self, identifier: Union[str, Dict[str, Any]]) -> None:
        """Sends Action Cable unsubscribe command."""
        id_str = json.dumps(identifier, separators=(",", ":")) if isinstance(identifier, dict) else identifier
        self.send_json({"command": "unsubscribe", "identifier": id_str})

    def perform(self, identifier: Union[str, Dict[str, Any]], data: Union[str, Dict[str, Any]]) -> None:
        """Sends Action Cable perform action message command."""
        id_str = json.dumps(identifier, separators=(",", ":")) if isinstance(identifier, dict) else identifier
        data_str = json.dumps(data, separators=(",", ":")) if isinstance(data, dict) else data
        self.send_json({"command": "message", "identifier": id_str, "data": data_str})

    def _recv_bytes(self, n: int) -> bytes:
        """Reads exactly n bytes from socket buffer or network."""
        while len(self._buffer) < n:
            chunk = self.sock.recv(max(4096, n - len(self._buffer)))
            if not chunk:
                raise WebSocketClosed("Socket disconnected while reading frame")
            self._buffer.extend(chunk)
        res = bytes(self._buffer[:n])
        self._buffer = self._buffer[n:]
        return res

    def _read_frame(self) -> Tuple[int, bytes]:
        """Reads and parses one RFC 6455 frame from the server."""
        header = self._recv_bytes(2)
        b1, b2 = header[0], header[1]
        fin = (b1 & 0x80) != 0
        opcode = b1 & 0x0F
        has_mask = (b2 & 0x80) != 0
        length = b2 & 0x7F

        if length == 126:
            length = struct.unpack("!H", self._recv_bytes(2))[0]
        elif length == 127:
            length = struct.unpack("!Q", self._recv_bytes(8))[0]

        if has_mask:
            mask = self._recv_bytes(4)
        else:
            mask = None

        payload = bytearray(self._recv_bytes(length))
        if has_mask and mask:
            for i in range(length):
                payload[i] ^= mask[i % 4]

        return opcode, bytes(payload)

    def recv_frame(self, timeout: Optional[float] = None, ignore_pings: bool = True) -> Optional[str]:
        """Reads next Action Cable text frame, handling pings and control frames."""
        if self.is_closed or not self.sock:
            return None

        if timeout is not None:
            self.sock.settimeout(timeout)
        else:
            self.sock.settimeout(self.timeout)

        fragments = bytearray()
        while True:
            try:
                opcode, payload = self._read_frame()
            except (socket.timeout, TimeoutError):
                return None
            except (WebSocketClosed, OSError):
                self.is_closed = True
                return None

            if opcode == 0x9:  # Ping
                # Reply with Pong
                try:
                    self.send_frame(0xA, payload)
                except Exception:
                    pass
                continue
            elif opcode == 0xA:  # Pong
                continue
            elif opcode == 0x8:  # Close
                self.is_closed = True
                if len(payload) >= 2:
                    self.close_code = struct.unpack("!H", payload[:2])[0]
                    self.close_reason = payload[2:].decode("utf-8", errors="replace")
                else:
                    self.close_code = 1000
                # Reply with close frame if not already sent
                try:
                    self.send_frame(0x8, payload)
                except Exception:
                    pass
                return None
            elif opcode in (0x1, 0x0):  # Text or continuation
                fragments.extend(payload)
                # Since fin=True was received in single frame or end of fragment:
                text = fragments.decode("utf-8", errors="replace")
                if ignore_pings:
                    try:
                        parsed = json.loads(text)
                        if isinstance(parsed, dict) and parsed.get("type") == "ping":
                            fragments.clear()
                            continue
                    except json.JSONDecodeError:
                        pass
                self.raw_frames_received.append(text)
                return text
            else:
                # Other binary opcodes
                continue

    def drain(self, timeout: float = 0.3, ignore_pings: bool = True) -> List[str]:
        """Collects all currently available frames until timeout."""
        frames = []
        while True:
            f = self.recv_frame(timeout=timeout, ignore_pings=ignore_pings)
            if f is None:
                break
            frames.append(f)
        return frames

    def close(self, code: int = 1000, reason: str = "") -> None:
        """Closes the WebSocket connection gracefully."""
        if self.is_closed:
            return
        self.is_closed = True
        try:
            if self.sock:
                payload = struct.pack("!H", code) + reason.encode("utf-8")
                self.send_frame(0x8, payload)
                self.sock.close()
        except Exception:
            pass
        finally:
            self.sock = None

    def __enter__(self):
        self.connect()
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()
