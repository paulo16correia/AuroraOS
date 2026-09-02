"""The real OpenAI Realtime transport.

Speaks the actual protocol over a real WebSocket: the session is configured once, audio goes up as
base64 PCM16, audio comes down the same way, and the model asks for a capability by emitting a
function call. Nothing here decides anything — it carries frames, and
:mod:`interaction` turns them into the events Aurora acts on.

**Read on a thread, drained on demand.** The pump that drives a conversation asks what has happened
since last time, and a socket read blocks until something arrives. So a reader thread does the
blocking and puts frames in a queue; `receive` empties the queue and returns. The same shape the
Discord gateway uses, for the same reason.

**The key never leaves this file.** It arrives from Aurora's vault over the plugin's pipe, is put
in one Authorization header, and is not logged, not echoed into an event, and not put in a URL.
"""

import json
import queue
import threading

from websocket import WebSocket, WebSocketError

# The published endpoint. A query parameter names the model, which is the one piece of the URL that
# is configuration rather than protocol.
REALTIME_URL = "wss://api.openai.com/v1/realtime"

# What the audio fields carry, both directions. 24 kHz mono signed 16-bit little-endian is what the
# service documents for pcm16, and getting the rate wrong does not fail — it transcribes and speaks
# at the wrong speed, which reads as a broken model rather than a wrong number.
SAMPLE_RATE = 24000
SAMPLE_WIDTH = 2
CHANNELS = 1


class RealtimeTransport:
    """One connection to the Realtime service.

    The same four methods the deterministic stand-in offers — connect, send, receive, close — so
    the interaction layer above cannot tell which one it is holding. That is what makes the
    end-to-end tests meaningful: they drive this class against a stand-in server rather than a
    stand-in client.
    """

    def __init__(self, api_key, url=REALTIME_URL, timeout=30, connect=None):
        self._key = api_key
        self._url = url
        self._timeout = timeout

        # How a socket is opened. Injected only so a test can point this at a loopback server
        # without a certificate; the shipped path passes nothing and gets the real one.
        self._connect = connect or (lambda url, headers: WebSocket(
            url, timeout=timeout, headers=headers))

        self._socket = None
        self._reader = None
        self._frames = queue.Queue()
        self._sending = threading.Lock()
        self._stopping = threading.Event()

        # Counted rather than logged, and reported as telemetry. How much audio a conversation
        # moved is the one number that predicts what it cost.
        self.audio_bytes_sent = 0
        self.audio_bytes_received = 0
        self.frames_sent = 0
        self.frames_received = 0
        self.close_code = None

    @property
    def connected(self):
        return self._socket is not None and not self._stopping.is_set()

    def connect(self, model):
        """Opens the connection. Raises rather than degrading: a session that did not connect is
        not a quiet one, it is not a session."""
        if not self._key:
            raise WebSocketError("no API key was supplied, so nothing can be connected to")

        url = "%s?model=%s" % (self._url, model)

        self._socket = self._connect(url, {
            "Authorization": "Bearer " + self._key,

            # The service refuses the upgrade without it, and the refusal arrives as a closed
            # socket rather than as an explanation.
            "OpenAI-Beta": "realtime=v1",
        })

        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()

    def send(self, frame):
        if not self.connected:
            raise WebSocketError("not connected")

        payload = json.dumps(frame)

        if frame.get("type") == "input_audio_buffer.append":
            self.audio_bytes_sent += len(frame.get("audio") or "")

        with self._sending:
            self._socket.send(payload)

        self.frames_sent += 1

    def receive(self):
        """Everything that arrived since the last call. Never blocks."""
        drained = []

        while True:
            try:
                drained.append(self._frames.get_nowait())
            except queue.Empty:
                break

        return drained

    def close(self, reason):
        self._stopping.set()

        if self._socket is not None:
            try:
                self._socket.close()
            except Exception:
                # Already gone, which is the outcome that was wanted.
                pass

        self._socket = None

    def _read(self):
        """Blocks on the socket so the pump does not have to."""
        while not self._stopping.is_set():
            try:
                text = self._socket.receive()
            except Exception as broken:
                if not self._stopping.is_set():
                    # The far end went away mid-conversation. Reported as a frame the interaction
                    # layer already knows how to read, so a disconnect and a service error reach
                    # Aurora by the same path.
                    self._frames.put({
                        "type": "error",
                        "error": {"message": "the connection failed (%s)"
                                             % type(broken).__name__},
                    })
                break

            if text is None:
                self.close_code = getattr(self._socket, "close_code", None)

                self._frames.put({
                    "type": "error",
                    "error": {"message": "the service closed the connection (%s)"
                                         % (self.close_code or "no code")},
                })
                break

            try:
                frame = json.loads(text)
            except ValueError:
                continue

            if frame.get("type") == "response.audio.delta":
                self.audio_bytes_received += len(frame.get("delta") or "")

            self.frames_received += 1
            self._frames.put(frame)

    def telemetry(self):
        """What a conversation cost, in the terms that are actually knowable here.

        Not money. The service prices audio by the minute and text by the token, and neither is
        reported on the wire — so this is the raw quantity, and the conversion belongs wherever
        somebody is holding a price list.
        """
        return {
            "frames_sent": self.frames_sent,
            "frames_received": self.frames_received,
            "audio_bytes_sent": self.audio_bytes_sent,
            "audio_bytes_received": self.audio_bytes_received,

            # Base64 of PCM16 at 24 kHz: four characters carry three bytes, and two bytes are one
            # sample. Approximate on purpose — it is a scale, not an invoice.
            "audio_seconds_sent": round(
                (self.audio_bytes_sent * 3 / 4) / (SAMPLE_RATE * SAMPLE_WIDTH), 2),
            "audio_seconds_received": round(
                (self.audio_bytes_received * 3 / 4) / (SAMPLE_RATE * SAMPLE_WIDTH), 2),
        }
