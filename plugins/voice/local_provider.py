"""The local voice provider: Faster-Whisper, Ollama, XTTS — behind the contract that already exists.

`InteractionSession` wraps OpenAI Realtime and offers six methods: start, append_audio, poll,
deliver, interrupt, close. This offers exactly the same six, and assembles them out of three local
engines instead of one remote service.

That is why nothing above this file changes. `voice_service.py` picks one or the other,
`VoiceRuntime` pumps whichever it got, and the tool request lands on `VoiceToolBridge` and the
Kernel by the same path either way — which is what the provider abstraction was for.

    append_audio → buffer → silence? → STT → transcript
                                              ↓
                                           Ollama
                                          ↙      ↘
                                   tool request   sentence
                                        ↓            ↓
                                  reported to      XTTS
                                    Aurora           ↓
                                        ↓          audio
                                   deliver() ────────┘

**No engine here decides anything.** The recogniser turns air into words, the model turns words
into a request or a sentence, the speaker turns a sentence into air. Whether the request is allowed
is decided in Aurora, by the Kernel, after this file has reported it.
"""

import base64
import time

import speech
import thinking

# How much silence ends a turn. Every millisecond is paid on every answer, and it is the one part
# of the delay that is pure waiting — long enough not to cut somebody off mid-thought, short enough
# not to feel like a form.
SILENCE_MS = 700

# Below this a slice is a room rather than a voice. Measured as mean absolute amplitude; a quiet
# room sits well under a hundred and speech runs into the thousands.
SPEECH_FLOOR = 300

# A turn nobody would want transcribed: a cough, a chair, a keystroke.
MIN_TURN_MS = 400

# And a ceiling, so a stuck microphone does not buffer until memory runs out.
MAX_TURN_MS = 30000


class LocalUnavailable(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


class LocalSession:
    """One local conversation, presenting the interaction contract the runtime already pumps."""

    def __init__(self, recogniser, speaker, brain, clock=None):
        self._recogniser = recogniser
        self._speaker = speaker
        self._brain = brain
        self._now = clock or (lambda: time.monotonic() * 1000)

        self._buffer = bytearray()

        # Both measured in audio, not in wall clock. See `append_audio`.
        self._spoken_ms = 0.0
        self._quiet_ms = 0.0

        self._events = []
        self.state = "pending"

        # Set while Aurora is speaking, so a new turn arriving can cut it off rather than queueing
        # behind it. Barge-in is the difference between a conversation and a broadcast.
        self._speaking = False

        self.turns = []
        self._turn = None

    # ---- the contract ----

    def start(self):
        if self._recogniser is None:
            raise LocalUnavailable(
                "voice_no_recogniser", "no local speech recogniser is installed")

        if self._speaker is None:
            raise LocalUnavailable("voice_no_speaker", "no local speech synthesiser is installed")

        self.state = "active"

    def append_audio(self, base64_pcm16):
        """A slice of microphone audio. Turn detection happens here, in the plugin.

        Realtime does this server-side; locally there is nobody else to do it, so it is done on
        amplitude and a silence window. Crude, and enough to tell a sentence from a pause.

        The windows are counted in audio rather than against the clock. Chunks arrive over a
        network and a pipe, so they stall and then catch up in a burst; a caller who has not
        stopped talking should not be treated as having finished because the transport hiccuped,
        and a rush of buffered silence should not need real seconds to be recognised as a pause.
        """
        chunk = base64.b64decode(base64_pcm16)
        lasted = speech.duration_ms(chunk)
        loud = speech.energy(chunk) >= SPEECH_FLOOR

        if loud:
            if self._spoken_ms == 0.0 and self._speaking:
                # Somebody started while Aurora was talking. Reported now, so the runtime can stop
                # the audio before the rest of the sentence is produced.
                self._events.append({"kind": "interrupted"})

            self._quiet_ms = 0.0
            self._spoken_ms += lasted
            self._buffer += chunk

            if self._spoken_ms >= MAX_TURN_MS:
                # A microphone left open. Cut the turn rather than buffering until memory runs out.
                self._end_turn()

            return

        if self._spoken_ms == 0.0:
            # Silence before anybody spoke. Nothing to keep.
            return

        self._buffer += chunk
        self._quiet_ms += lasted

        if self._quiet_ms >= SILENCE_MS:
            self._end_turn()

    def poll(self):
        drained, self._events = self._events, []
        return drained

    def deliver(self, request_id, outcome):
        """What Aurora decided. Back to the model, then out as speech."""
        if self._turn is not None:
            self._turn["tool_returned"] = self._now()

        self._brain.tool_answered(
            self._turn["tool_name"] if self._turn else "unknown", outcome)

        self._think()

    def interrupt(self):
        """Stop talking. The audio already handed over is the runtime's to drop."""
        self._speaking = False
        self._events.append({"kind": "cancelled"})

    def close(self, reason):
        self.state = "closed"
        self._buffer = bytearray()

    # ---- the loop ----

    def _end_turn(self):
        audio, self._buffer = bytes(self._buffer), bytearray()
        spoken_ms, self._spoken_ms, self._quiet_ms = self._spoken_ms, 0.0, 0.0

        if spoken_ms < MIN_TURN_MS:
            # A cough, a chair, a keystroke. Transcribing it produces words nobody said.
            return

        self._turn = {"speech_ended": self._now(), "spoken_ms": round(spoken_ms)}
        self._turn["stt_started"] = self._now()

        try:
            heard = self._recogniser.transcribe(audio)
        except Exception as unheard:
            self._events.append({
                "kind": "failed",
                "detail": "the recogniser failed (%s)" % type(unheard).__name__,
            })
            return

        self._turn["stt_completed"] = self._now()

        text = (heard.get("text") or "").strip()

        if not text:
            # Heard nothing. Asking a model to answer silence produces the sentence a model says
            # when it has nothing — which is worse than saying nothing.
            return

        self._events.append({
            "kind": "heard",
            "text": text[:4000],
            "confidence": heard.get("confidence"),
            "engine": heard.get("engine"),
        })

        self._brain.heard(text)
        self._think()

    def _think(self):
        """One pass of the language layer: either a request for Aurora, or something to say."""
        self._turn = self._turn or {}
        self._turn["llm_started"] = self._now()

        try:
            decided = self._brain.respond()
        except thinking.ThinkingUnavailable as unavailable:
            self._events.append({"kind": "failed", "detail": unavailable.message})
            return

        self._turn["llm_completed"] = self._now()

        if decided["kind"] == "tool":
            action = thinking.action_of(decided["name"])
            self._turn["tool_name"] = decided["name"]
            self._turn["tool_requested"] = self._now()

            # Reported, never executed. Aurora decides and calls back through `deliver`.
            self._events.append({
                "kind": "tool_requested",
                "request_id": "local-%d" % int(self._now()),
                "action_id": action,
                "input_json": decided["arguments"],
            })
            return

        self._say(decided["text"])

    def _say(self, text):
        if not text:
            return

        self._turn = self._turn or {}
        self._turn["tts_started"] = self._now()

        try:
            audio = self._speaker.speak(text)
        except Exception as unspeakable:
            self._events.append({
                "kind": "failed",
                "detail": "the synthesiser failed (%s)" % type(unspeakable).__name__,
            })
            return

        self._turn["tts_completed"] = self._now()
        self._turn["turn_completed"] = self._now()
        self._speaking = True

        self._events.append({"kind": "said", "text": text[:4000]})
        self._events.append({"kind": "audio", "audio": base64.b64encode(audio).decode()})

        self.turns.append(_latency(self._turn))
        self._turn = None

    # ---- what it cost ----

    def telemetry(self):
        spent = dict(self._brain.telemetry())

        spent.update({
            "provider": "local",
            "recogniser": getattr(self._recogniser, "name", None),
            "speaker": getattr(self._speaker, "name", None),
            "turns": len(self.turns),
        })

        if self.turns:
            spent["last_turn"] = self.turns[-1]
            spent["median_turn_ms"] = sorted(
                t["total_ms"] for t in self.turns if t.get("total_ms"))[len(self.turns) // 2]

        return spent


def _latency(turn):
    """The measurements the turn actually produced. Nothing estimated, nothing filled in."""
    def span(start, end):
        if turn.get(start) is None or turn.get(end) is None:
            return None

        return round(turn[end] - turn[start])

    measured = {
        "spoken_ms": turn.get("spoken_ms"),
        "stt_ms": span("stt_started", "stt_completed"),
        "llm_ms": span("llm_started", "llm_completed"),
        "tts_ms": span("tts_started", "tts_completed"),
        "tool_ms": span("tool_requested", "tool_returned"),
        "total_ms": span("speech_ended", "turn_completed"),
    }

    return {name: value for name, value in measured.items() if value is not None}


def build(settings, identity, action_ids, opener=None):
    """A local session from configuration, or a refusal naming what is missing.

    Refusing is the honest answer. A provider that quietly fell back to something else would let a
    conversation appear to happen with an engine nobody chose.
    """
    recogniser = speech.best_recogniser(settings.get("stt"))
    speaker = speech.best_speaker(settings.get("tts"))

    missing = []

    if recogniser is None:
        missing.append("a speech recogniser (faster-whisper, or whisper.cpp with a model)")

    if speaker is None:
        missing.append("a speech synthesiser (Coqui XTTS, or `say` on macOS)")

    if missing:
        raise LocalUnavailable("voice_local_incomplete", "; ".join(missing))

    brain = thinking.Thinking(
        identity=identity,
        tools=thinking.tools_from(action_ids),
        settings=settings.get("llm"),
        opener=opener)

    return LocalSession(recogniser, speaker, brain)
