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

**The work does not happen where the audio arrives.** `append_audio` buffers, notices that somebody
stopped talking, hands the turn to a worker and returns; the worker does recognition, thinking and
synthesis and leaves what it produced on the same queue `poll` already drains. This is the shape the
Discord plugin has used since it was the only voice Aurora had — for the reason its own comment
gives, that recognition takes as long as it takes and doing it on the arrival path stops the
audio being drained while somebody is still speaking.

Locally the consequence is sharper than a stalled socket. Every capability declares a timeout, and
`voice.listen` declares ten seconds because appending audio to a remote service is a forwarding
operation. Recognition and an 8B model on this machine are not, and doing them inside that call
made Aurora abandon a turn it had already been told about, four times over, while the plugin was
still working on it.
"""

import base64
import queue
import threading
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

# How long a hang-up waits for the worker to notice. Short: the thing being waited on may be a
# model mid-sentence, and a session that cannot end is worse than a thread that outlives it by a
# moment. The generation check is what makes anything it produces after this harmless.
WORKER_JOIN_MS = 2000


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

        # Written by the worker, drained by whoever is polling. Two threads, so a lock.
        self._events = []
        self._events_lock = threading.Lock()

        # Turns waiting to be thought about, and what does the thinking. One worker, so a session
        # can only ever be working on one turn — which is what makes a turn happen exactly once.
        self._work = queue.Queue()
        self._worker = None
        self._stopping = threading.Event()

        # Bumped when somebody interrupts or the session ends. Work carrying an older number is
        # dropped rather than spoken: cancelling has to mean the sentence does not arrive later.
        self._generation = 0
        self._lock = threading.Lock()

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

        # A daemon, so a plugin that is killed does not wait on it. `close` still joins it, which
        # is the path that actually runs.
        self._worker = threading.Thread(
            target=self._work_loop, name="aurora-voice-turn", daemon=True)
        self._worker.start()

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

        # Everything under here is arithmetic on a buffer. No engine is touched, nothing is waited
        # on, and the call returns in the time it takes to add up some samples.
        with self._lock:
            if loud:
                if self._spoken_ms == 0.0 and self._speaking:
                    # Somebody started while Aurora was talking. Reported now, so the runtime can
                    # stop the audio before the rest of the sentence is produced.
                    self._emit({"kind": "interrupted"})

                self._quiet_ms = 0.0
                self._spoken_ms += lasted
                self._buffer += chunk

                if self._spoken_ms >= MAX_TURN_MS:
                    # A microphone left open. Cut the turn rather than buffering until memory
                    # runs out.
                    self._hand_over_turn()

                return

            if self._spoken_ms == 0.0:
                # Silence before anybody spoke. Nothing to keep.
                return

            self._buffer += chunk
            self._quiet_ms += lasted

            if self._quiet_ms >= SILENCE_MS:
                self._hand_over_turn()

    def poll(self):
        with self._events_lock:
            drained, self._events = self._events, []

        return drained

    def deliver(self, request_id, outcome):
        """What Aurora decided, handed to the worker.

        Answering the model and saying the answer out loud is another model call and another pass
        of the synthesiser, which is the same reason `append_audio` does not do its own work. This
        records the outcome and returns; the sentence arrives on the queue when it exists.
        """
        with self._lock:
            if self._turn is not None:
                self._turn["tool_returned"] = self._now()

            self._work.put(("tool", self._generation, request_id, outcome))

    def interrupt(self):
        """Stop talking. The audio already handed over is the runtime's to drop.

        Bumping the generation is what makes this mean something now that the sentence is produced
        somewhere else: work already queued, and a turn the worker is part way through, belong to
        the old number and are dropped rather than spoken after the interruption.
        """
        with self._lock:
            self._generation += 1
            self._speaking = False

        self._emit({"kind": "cancelled"})

    def close(self, reason):
        """Ends the session and the work it owns.

        A session that returned while its worker was still thinking would speak into a call that
        had ended, so this waits — briefly, because the thing being waited for is a model that may
        be slow, and a hang-up that cannot complete is worse than one that leaves a thread to die
        with the process.
        """
        with self._lock:
            self._generation += 1
            self._buffer = bytearray()

        self.state = "closed"
        self._stopping.set()
        self._work.put(None)

        worker, self._worker = self._worker, None

        if worker is not None and worker is not threading.current_thread():
            worker.join(timeout=WORKER_JOIN_MS / 1000.0)

    # ---- the loop ----

    def _emit(self, event):
        with self._events_lock:
            self._events.append(event)

    def _hand_over_turn(self):
        """Closes the buffer and gives the turn to the worker. Called holding the lock."""
        audio, self._buffer = bytes(self._buffer), bytearray()
        spoken_ms, self._spoken_ms, self._quiet_ms = self._spoken_ms, 0.0, 0.0

        if spoken_ms < MIN_TURN_MS:
            # A cough, a chair, a keystroke. Transcribing it produces words nobody said.
            return

        # Handed over once, with the number it was handed over under. The buffer is emptied in the
        # same breath, so the same audio cannot become a second turn however often this is called.
        self._work.put(("turn", self._generation, audio, spoken_ms, self._now()))

    def _work_loop(self):
        """Everything that takes time, off the path the audio arrives on."""
        while not self._stopping.is_set():
            try:
                item = self._work.get(timeout=0.05)
            except queue.Empty:
                continue

            if item is None:
                return

            kind, generation = item[0], item[1]

            with self._lock:
                current = self._generation

            if generation != current:
                # Interrupted, or the session ended, between being queued and being reached.
                continue

            try:
                if kind == "turn":
                    self._process_turn(item[2], item[3], item[4], generation)
                elif kind == "tool":
                    self._process_tool(item[2], item[3], generation)
            except Exception as broken:
                # A worker that dies takes the conversation with it and says nothing about why.
                self._emit({
                    "kind": "failed",
                    "detail": "the turn could not be completed (%s)" % type(broken).__name__,
                })

    def _still(self, generation):
        """Whether the work in hand is still wanted."""
        if self._stopping.is_set():
            return False

        with self._lock:
            return generation == self._generation

    def _process_turn(self, audio, spoken_ms, ended_at, generation):
        self._turn = {
            "speech_ended": ended_at,
            "spoken_ms": round(spoken_ms),
            "queued_ms": round(self._now() - ended_at),
        }
        self._turn["stt_started"] = self._now()

        try:
            heard = self._recogniser.transcribe(audio)
        except Exception as unheard:
            self._emit({
                "kind": "failed",
                "detail": "the recogniser failed (%s)" % type(unheard).__name__,
            })
            return

        self._turn["stt_completed"] = self._now()

        # Checked after every stage that takes time, because each of them is long enough for a
        # hang-up to arrive. What was said into a call that has ended is not said.
        if not self._still(generation):
            return

        text = (heard.get("text") or "").strip()

        if not text:
            # Heard nothing. Asking a model to answer silence produces the sentence a model says
            # when it has nothing — which is worse than saying nothing.
            return

        self._emit({
            "kind": "heard",
            "text": text[:4000],
            "confidence": heard.get("confidence"),
            "engine": heard.get("engine"),
        })

        self._brain.heard(text)
        self._think(generation)

    def _process_tool(self, request_id, outcome, generation):
        """Aurora's answer, back to the model and out as speech."""
        self._brain.tool_answered(
            (self._turn or {}).get("tool_name", "unknown"), outcome)

        self._think(generation)

    def _think(self, generation):
        """One pass of the language layer: either a request for Aurora, or something to say."""
        self._turn = self._turn or {}
        self._turn["llm_started"] = self._now()

        try:
            decided = self._brain.respond()
        except thinking.ThinkingUnavailable as unavailable:
            self._emit({"kind": "failed", "detail": unavailable.message})
            return

        self._turn["llm_completed"] = self._now()
        self._turn.update(self._brain.last_call())

        if not self._still(generation):
            return

        if decided["kind"] == "tool":
            action = thinking.action_of(decided["name"])
            self._turn["tool_name"] = decided["name"]
            self._turn["tool_requested"] = self._now()

            # Reported, never executed. Aurora decides and calls back through `deliver`.
            self._emit({
                "kind": "tool_requested",
                "request_id": "local-%d" % int(self._now()),
                "action_id": action,
                "input_json": decided["arguments"],
            })
            return

        self._say(decided["text"], generation)

    def _say(self, text, generation):
        if not text:
            return

        self._turn = self._turn or {}
        self._turn["tts_started"] = self._now()

        try:
            audio = self._speaker.speak(text)
        except Exception as unspeakable:
            self._emit({
                "kind": "failed",
                "detail": "the synthesiser failed (%s)" % type(unspeakable).__name__,
            })
            return

        self._turn["tts_completed"] = self._now()
        self._turn["turn_completed"] = self._now()

        if not self._still(generation):
            # Synthesised into a call that ended while it was being synthesised. The measurement
            # is still worth keeping; the audio is not.
            self.turns.append(_latency(self._turn))
            self._turn = None
            return

        self._speaking = True

        self._emit({"kind": "said", "text": text[:4000]})
        self._emit({"kind": "audio", "audio": base64.b64encode(audio).decode()})

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
        "queued_ms": turn.get("queued_ms"),
        "llm_load_ms": turn.get("llm_load_ms"),
        "llm_prompt_ms": turn.get("llm_prompt_ms"),
        "llm_generate_ms": turn.get("llm_generate_ms"),
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
