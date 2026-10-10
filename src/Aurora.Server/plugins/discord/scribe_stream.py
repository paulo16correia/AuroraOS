"""Transcription while somebody is still talking, instead of after they stop.

The batch recogniser is asked a question once a turn has ended, so its cost lands entirely inside
the pause before Aurora answers: measured in a real channel, 600ms waiting out the silence and
another 700ms being told what was said, before the language model had seen a word. This is the same
service's realtime model over a WebSocket. Audio goes up as it arrives and the transcript is already
there when the speaker stops, so that second number becomes roughly zero.

Two things fall out of it that are worth more than the latency.

The server decides where a turn ends. `commit_strategy=vad` means ElevenLabs' own voice activity
detection draws the boundary and sends a committed transcript, rather than this plugin guessing from
a silence timer — and guessing is what produced fragments of a fifth of a second that came back as
empty strings and cost a request each.

And the audio goes up at the rate it arrived. `pcm_48000` is exactly what Discord carries, so for
the first time nothing in this path resamples anything: no decimation to suit whisper, no box filter,
no conversion at all beyond folding two identical channels into one.

**One stream per speaker.** The service transcribes one voice per connection, and the plugin knows
who every packet belongs to, so mixing the room into a single socket would throw away the one thing
it already knew — who said it. A stream is opened when somebody first speaks and closed when they
have been quiet for a while, because a session has a ten-minute ceiling and an idle one costs
money.
"""

import base64
import json
import queue
import threading
import urllib.parse

import websocket

HOST = "api.elevenlabs.io"
MODEL = "scribe_v2_realtime"

# Discord's rate, sent as Discord sends it.
SAMPLE_RATE = 48000

# How long a committed transcript is waited for after the audio stops. Generous, because the thing
# being waited on is a service deciding a sentence has ended, and the cost of giving up early is a
# question nobody answers.
DRAIN_SECONDS = 4.0

# How long the server waits for silence before it decides a turn is over.
#
# Six hundred milliseconds, which is what the plugin's own silence threshold had been tuned to by
# hand. Handing the number to the service rather than keeping it here is the point: it has the audio
# and the model, and it is deciding while the person is still speaking rather than afterwards.
VAD_SILENCE_SECONDS = 0.6


class ScribeStream:
    """One speaker's audio going up, and their committed transcripts coming back.

    Not thread-safe for sending; the audio thread is the only sender and the reader is its own
    thread, which is the arrangement this is built for.
    """

    def __init__(self, api_key, language=None, keyterms=None, host=HOST):
        query = {
            "model_id": MODEL,
            "audio_format": "pcm_%d" % SAMPLE_RATE,
            "commit_strategy": "vad",
            "vad_silence_threshold_secs": VAD_SILENCE_SECONDS,
        }

        if language:
            query["language_code"] = language

        # Up to fifty on the realtime model, against a thousand on the batch one. Passed through
        # rather than decided here: whether to bias the recogniser at all is the owner's choice and
        # it costs more per request.
        for term in (keyterms or [])[:50]:
            query.setdefault("keyterms", []).append(term)

        self._url = "wss://%s/v1/speech-to-text/realtime?%s" % (
            host, urllib.parse.urlencode(query, doseq=True))

        # The key travels as a header, which is the server-side half of this API. The other half is
        # a single-use token in the query string, for a browser that must never hold a key — not
        # this, which is a plugin on the owner's own machine.
        self._headers = {"xi-api-key": api_key}

        self._socket = None
        self._said = queue.Queue()
        self._reader = None
        self._stop = threading.Event()
        self._failure = None

    # ---- opening and closing ----

    def open(self):
        """Connects and starts reading. Raises if the service will not have us."""
        self._socket = websocket.WebSocket(self._url, timeout=30, headers=self._headers)
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()
        return self

    def close(self):
        self._stop.set()

        if self._socket is not None:
            try:
                self._socket.close()
            except Exception:
                # Closing a socket that is already gone is not a failure worth reporting to
                # somebody who asked for silence.
                pass

    @property
    def failure(self):
        """What the service said, if it refused. None while things are working."""
        return self._failure

    # ---- audio going up ----

    def send(self, pcm, commit=False):
        """One chunk of 48kHz mono signed 16-bit PCM."""
        if self._socket is None or self._stop.is_set():
            return False

        try:
            self._socket.send_json({
                "message_type": "input_audio_chunk",
                "audio_base_64": base64.b64encode(pcm).decode("ascii"),
                "commit": bool(commit),
                "sample_rate": SAMPLE_RATE,
            })
            return True
        except Exception as broke:
            self._failure = "%s: %s" % (type(broke).__name__, broke)
            self._stop.set()
            return False

    # ---- sentences coming back ----

    def heard(self):
        """Every committed transcript that has arrived since this was last asked."""
        said = []

        while True:
            try:
                said.append(self._said.get_nowait())
            except queue.Empty:
                return said

    def _read(self):
        """Drains the socket, keeping only the sentences the service has committed to.

        Partial transcripts are deliberately dropped. They are what makes this fast — the service
        is transcribing while somebody talks — but they change as it listens, and a plugin that
        answered one would be answering a guess the service itself had not finished making.
        """
        while not self._stop.is_set():
            try:
                frame = self._socket.receive()
            except Exception as broke:
                if not self._stop.is_set():
                    self._failure = "%s: %s" % (type(broke).__name__, broke)
                return

            if frame is None:
                return

            try:
                message = json.loads(frame)
            except (TypeError, ValueError):
                continue

            kind = message.get("message_type")

            if kind == "committed_transcript":
                text = (message.get("text") or "").strip()

                if text:
                    self._said.put(text)
            elif kind and kind.endswith("error") or kind in (
                    "quota_exceeded", "rate_limited", "unaccepted_terms",
                    "session_time_limit_exceeded", "resource_exhausted", "queue_overflow"):
                # Said once and kept, because these are the ones an owner can act on — a quota, a
                # key, a licence — and they are indistinguishable from silence otherwise.
                self._failure = "%s: %s" % (kind, message.get("error") or message.get("warning"))
                self._stop.set()
                return


def mono(pcm):
    """Discord's stereo PCM folded to one channel, at the rate it arrived.

    An average of the two, not a decimation: no rate changes, so no band is lost. Discord sends the
    same voice in both channels and a recogniser has no use for the second copy.
    """
    import struct

    frames = len(pcm) // 4
    out = bytearray()

    for frame in range(frames):
        left, right = struct.unpack_from("<hh", pcm, frame * 4)
        out += struct.pack("<h", (left + right) // 2)

    return bytes(out)
