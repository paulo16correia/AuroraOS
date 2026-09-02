"""A Realtime service on loopback, so the real transport can be driven end to end.

The deterministic client stand-in in `fake_realtime` replaces the transport, which means anything
wrong *inside* the transport — the handshake, the framing, the threading, the queue — goes
untested. This replaces the far end instead: a real WebSocket server that speaks the documented
Realtime frames, so `RealtimeTransport` performs a real RFC 6455 handshake, sends real masked
frames, and reads real ones back.

It is not OpenAI. It answers the frames this repository knows about, in the shapes the
documentation gives, and it can be told to behave badly — refuse the upgrade, close mid-session,
answer with something unparseable.
"""

import base64
import hashlib
import json
import socket
import struct
import threading

GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


class FakeRealtimeServer:
    """A WebSocket server that answers like the Realtime service."""

    def __init__(self, refuse_upgrade=False, require_key=True):
        self.refuse_upgrade = refuse_upgrade
        self.require_key = require_key

        # What a test wants to know: the handshake headers, and every frame the client sent.
        self.handshake_headers = {}
        self.received = []

        # What the service will say once the client has configured its session, in order.
        self.script = []
        self.close_after_script = False

        self._listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._listener.bind(("127.0.0.1", 0))
        self._listener.listen(1)

        self.port = self._listener.getsockname()[1]
        self._client = None
        self._stopping = threading.Event()
        self._lock = threading.Lock()

        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._thread.start()

    @property
    def url(self):
        return "ws://127.0.0.1:%d/v1/realtime" % self.port

    def says(self, *frames):
        """Queues frames the service will send after the client configures its session."""
        self.script.extend(frames)
        return self

    def wants_tool(self, call_id, name, arguments="{}"):
        return self.says({
            "type": "response.function_call_arguments.done",
            "call_id": call_id, "name": name, "arguments": arguments,
        })

    def speaks(self, base64_audio):
        return self.says({"type": "response.audio.delta", "delta": base64_audio})

    def heard(self, text):
        return self.says({
            "type": "conversation.item.input_audio_transcription.completed",
            "transcript": text,
        })

    def barges_in(self):
        return self.says({"type": "input_audio_buffer.speech_started"})

    def frames_of_type(self, kind):
        with self._lock:
            return [f for f in self.received if f.get("type") == kind]

    def close(self):
        self._stopping.set()

        try:
            self._listener.close()
        except OSError:
            pass

        if self._client is not None:
            try:
                self._client.close()
            except OSError:
                pass

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        self.close()

    # ---- the wire ----

    def _serve(self):
        try:
            client, _ = self._listener.accept()
        except OSError:
            return

        self._client = client

        try:
            request = self._read_headers(client)
        except OSError:
            return

        self.handshake_headers = request

        if self.refuse_upgrade or (
                self.require_key and not request.get("authorization", "").startswith("Bearer ")):
            # What the service does to a request without a usable credential: refuses the upgrade
            # rather than accepting and then failing, which is a different failure to diagnose.
            client.sendall(b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n")
            client.close()
            return

        accept = base64.b64encode(
            hashlib.sha1((request.get("sec-websocket-key", "") + GUID).encode()).digest()).decode()

        client.sendall(
            ("HTTP/1.1 101 Switching Protocols\r\n"
             "Upgrade: websocket\r\nConnection: Upgrade\r\n"
             "Sec-WebSocket-Accept: %s\r\n\r\n" % accept).encode())

        self._converse(client)

    def _converse(self, client):
        while not self._stopping.is_set():
            try:
                frame = self._read_frame(client)
            except OSError:
                return

            if frame is None:
                return

            with self._lock:
                self.received.append(frame)

            # The service starts saying things once the client has told it what the session is.
            if frame.get("type") == "session.update":
                for scripted in self.script:
                    self._send(client, scripted)

                if self.close_after_script:
                    try:
                        client.close()
                    except OSError:
                        pass

                    return

    def _send(self, client, frame):
        payload = json.dumps(frame).encode()
        header = bytes([0x81])

        if len(payload) < 126:
            header += bytes([len(payload)])
        elif len(payload) < 65536:
            header += bytes([126]) + struct.pack(">H", len(payload))
        else:
            header += bytes([127]) + struct.pack(">Q", len(payload))

        # Server frames are never masked, which is the half of RFC 6455 a client gets wrong in the
        # other direction.
        client.sendall(header + payload)

    def _read_headers(self, client):
        buffer = b""

        while b"\r\n\r\n" not in buffer:
            chunk = client.recv(4096)

            if not chunk:
                return {}

            buffer += chunk

        headers = {}

        for line in buffer.decode("utf-8", "replace").split("\r\n")[1:]:
            if ":" in line:
                name, value = line.split(":", 1)
                headers[name.strip().lower()] = value.strip()

        return headers

    def _read_frame(self, client):
        first = self._exactly(client, 2)

        if first is None:
            return None

        opcode = first[0] & 0x0F
        masked = bool(first[1] & 0x80)
        length = first[1] & 0x7F

        if length == 126:
            length = struct.unpack(">H", self._exactly(client, 2))[0]
        elif length == 127:
            length = struct.unpack(">Q", self._exactly(client, 8))[0]

        mask = self._exactly(client, 4) if masked else None
        payload = self._exactly(client, length) if length else b""

        if payload is None:
            return None

        if mask:
            payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))

        if opcode == 0x8:
            return None

        try:
            return json.loads(payload.decode("utf-8", "replace"))
        except ValueError:
            return {"type": "unparseable"}

    def _exactly(self, client, count):
        buffer = b""

        while len(buffer) < count:
            chunk = client.recv(count - len(buffer))

            if not chunk:
                return None

            buffer += chunk

        return buffer
