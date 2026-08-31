"""A WebSocket client, in the standard library.

Python ships no WebSocket client, and a plugin that needs `pip install` needs a network before it
has been granted one. So this is RFC 6455 over a plain socket: the handshake, the framing, and the
client-side masking that servers reject connections for omitting.

Deliberately small. It speaks text frames, ping/pong and close, and nothing else — no extensions,
no compression, no continuation of fragmented control frames — because every feature here is one
more thing to get subtly wrong in a file nobody will read again.
"""

import base64
import hashlib
import json
import os
import socket
import ssl
import struct
import urllib.parse

TEXT = 0x1
BINARY = 0x2
CLOSE = 0x8
PING = 0x9
PONG = 0xA

GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


class WebSocketError(Exception):
    pass


class WebSocket:
    """One connection. Not thread-safe for sending; the caller holds a lock."""

    def __init__(self, url, timeout=30, headers=None):
        parts = urllib.parse.urlparse(url)
        secure = parts.scheme in ("wss", "https")
        host = parts.hostname
        port = parts.port or (443 if secure else 80)
        path = parts.path or "/"
        if parts.query:
            path += "?" + parts.query

        if host is None:
            raise WebSocketError("no host in %s" % url)

        raw = socket.create_connection((host, port), timeout=timeout)

        if secure:
            context = ssl.create_default_context()
            raw = context.wrap_socket(raw, server_hostname=host)

        self._socket = raw
        self._buffer = b""
        self._closed = False

        # Why the peer closed, when it said. Kept because a close code is often the only
        # explanation a service gives: Discord answers "your token is wrong" and "you asked for
        # intents you were not granted" this way and no other.
        self.close_code = None
        self.close_reason = None

        key = base64.b64encode(os.urandom(16)).decode()

        # Extra headers matter more than they look. Services behind a CDN reject an upgrade with
        # no User-Agent, and the refusal arrives as a closed socket rather than as an explanation.
        extra = "".join(
            "%s: %s\r\n" % (name, value) for name, value in (headers or {}).items())

        request = (
            "GET %s HTTP/1.1\r\n"
            "Host: %s\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            "Sec-WebSocket-Key: %s\r\n"
            "Sec-WebSocket-Version: 13\r\n"
            "%s"
            "\r\n" % (path, host, key, extra)
        )
        raw.sendall(request.encode())

        header = self._read_until(b"\r\n\r\n")
        status = header.split(b"\r\n", 1)[0]

        if b"101" not in status:
            raise WebSocketError("the server refused the upgrade: %s" % status.decode(errors="replace"))

        # The server proves it understood the handshake rather than merely answering 101, which is
        # what stops a plain HTTP endpoint from looking like a websocket.
        expected = base64.b64encode(hashlib.sha1((key + GUID).encode()).digest()).decode()

        if expected.lower().encode() not in header.lower():
            raise WebSocketError("the server's accept key did not match")

    def _read_until(self, marker):
        while marker not in self._buffer:
            chunk = self._socket.recv(4096)
            if not chunk:
                raise WebSocketError("the connection closed during the handshake")
            self._buffer += chunk

        head, self._buffer = self._buffer.split(marker, 1)
        return head + marker

    def _read_exactly(self, count):
        while len(self._buffer) < count:
            chunk = self._socket.recv(max(4096, count - len(self._buffer)))
            if not chunk:
                raise WebSocketError("the connection closed mid-frame")
            self._buffer += chunk

        taken, self._buffer = self._buffer[:count], self._buffer[count:]
        return taken

    @property
    def closed(self):
        """Whether this connection is finished with. Cheap to ask, unlike touching the socket."""
        return self._closed

    def send(self, text):
        """Sends one text frame, masked, as a client must."""
        if self._closed:
            raise WebSocketError("the connection is closed")

        payload = text.encode()
        header = bytearray([0x80 | TEXT])
        length = len(payload)

        # The mask bit is set on every client frame. A server is required to fail the connection
        # for an unmasked one, which is a confusing way to discover this was forgotten.
        if length < 126:
            header.append(0x80 | length)
        elif length < 65536:
            header.append(0x80 | 126)
            header += struct.pack("!H", length)
        else:
            header.append(0x80 | 127)
            header += struct.pack("!Q", length)

        mask = os.urandom(4)
        masked = bytes(byte ^ mask[i % 4] for i, byte in enumerate(payload))

        self._socket.sendall(bytes(header) + mask + masked)

    def receive(self):
        """The next text frame's payload, or None when the peer closed.

        Answers pings itself, because a gateway that stops hearing pongs drops the connection and
        the reason looks like a network fault.
        """
        while True:
            first, second = self._read_exactly(2)
            opcode = first & 0x0F
            masked = bool(second & 0x80)
            length = second & 0x7F

            if length == 126:
                (length,) = struct.unpack("!H", self._read_exactly(2))
            elif length == 127:
                (length,) = struct.unpack("!Q", self._read_exactly(8))

            # A server must not mask. Reading a masked frame anyway keeps this from being a
            # protocol argument in the middle of a working connection.
            mask = self._read_exactly(4) if masked else None
            payload = self._read_exactly(length) if length else b""

            if mask:
                payload = bytes(byte ^ mask[i % 4] for i, byte in enumerate(payload))

            if opcode == CLOSE:
                self._closed = True

                if len(payload) >= 2:
                    self.close_code = struct.unpack("!H", payload[:2])[0]
                    self.close_reason = payload[2:].decode(errors="replace")[:200]

                return None

            if opcode == PING:
                self._send_control(PONG, payload)
                continue

            if opcode == PONG:
                continue

            if opcode in (TEXT, BINARY):
                return payload.decode(errors="replace")

    def _send_control(self, opcode, payload=b""):
        mask = os.urandom(4)
        masked = bytes(byte ^ mask[i % 4] for i, byte in enumerate(payload[:125]))
        header = bytes([0x80 | opcode, 0x80 | len(masked)])
        self._socket.sendall(header + mask + masked)

    def send_json(self, value):
        self.send(json.dumps(value))

    def close(self):
        if self._closed:
            return

        self._closed = True

        try:
            self._send_control(CLOSE, struct.pack("!H", 1000))
        except OSError:
            pass

        try:
            self._socket.close()
        except OSError:
            pass
