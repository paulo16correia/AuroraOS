#!/usr/bin/env python3
"""Aurora's voice plugin.

Holds the connection Aurora's own process may not: the one to whatever turns a sentence into audio.
There were two, while a telephone company carried the call; there is one now, and it goes to a
speech service. Speaks Aurora's service protocol on stdin/stdout, one JSON object per line.

**It decides nothing.** When the interaction layer asks for a capability this program does not run
it, look it up, or guess at it — it puts the request in a queue and says so. Aurora reads the queue,
puts the request through the session's grant and then through the Kernel, and hands back an outcome.
The plugin's whole part is carrying words in both directions.

That is why voice needed no change to the plugin protocol. A plugin cannot ask Aurora for anything;
it reports, and Aurora acts. The queue below is the reporting, and `voice.tool_result` is Aurora
acting.
"""

import json
import sys
import threading

import local_provider

E_UNSUPPORTED = "voice_unsupported_capability"
E_NO_SESSION = "voice_no_session"
E_ALREADY = "voice_session_exists"
E_SCHEMA = "voice_schema"
E_PROVIDER = "voice_failed"

# What a capability request came back as, in Aurora's words rather than this plugin's. The four
# live in Aurora and arrive through voice.tool_result; this is the one to assume when the field is
# missing, because a request whose outcome nobody stated did not succeed. It was interaction.FAILED
# until that module went with the telephone, and the reference outlived the file.
FAILED = "Failed"


class Refused(Exception):
    """A refusal with a code Aurora can act on, rather than a stack trace it cannot.

    It used to live in provider.py with the telephone code. That file is gone and this was the
    only part of it anything else used, so it moved here rather than keeping a module alive to
    hold one exception.
    """

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def say(frame):
    sys.stdout.write(json.dumps(frame) + "\n")
    sys.stdout.flush()


def report(kind, payload):
    """An event frame. What Aurora publishes as an external observation, never a request."""
    say({"kind": "event", "type": kind, "payload": payload})


def _settings():
    """Configuration beside this program. Absent in a shipped install, which is the point."""
    import os

    here = os.path.dirname(os.path.abspath(__file__))

    try:
        # UTF-8 named, not inferred. JSON is UTF-8 by specification, and Python on
        # Windows reads text as cp1252 unless told otherwise, so a config holding a
        # name, a channel title or an accented word would be read wrong or not at all.
        with open(os.path.join(here, "config.json"), "r", encoding="utf-8") as handle:
            return json.load(handle) or {}
    except (OSError, ValueError):
        return {}


def _session_for(settings, api_key, instructions, tools):
    """The conversation this installation can hold.

    There used to be two providers behind this — the local stack and OpenAI Realtime — chosen by
    configuration. There is one now. Every engine behind a choice is one more thing to install,
    keep working and reason about when an answer comes back wrong, and the local stack is the one
    that needs nobody's permission.

    What it is made of: whisper for hearing, a local model for thinking, and ElevenLabs for
    speaking. Only the last of those leaves the machine.
    """
    return local_provider.build(
        settings.get("local") or {},
        identity=instructions,
        action_ids=[t.get("name", "").replace("__", ".") for t in tools],
        api_key=api_key)


class Session:
    """One conversation this process is carrying."""

    def __init__(self, session_id, participant):
        self.session_id = session_id
        self.participant = participant
        self.interaction = None
        self.state = "created"

        # What the interaction layer has asked for and Aurora has not yet answered. Drained by
        # `voice.poll`, which is how a request reaches Aurora at all.
        self.pending = []
        self.heard = []

        # What Aurora is saying, as base64 PCM16, waiting for whoever is playing it. Drained by
        # the same poll that drains tool requests, so one round trip carries both.
        self.audio = []
        self.lock = threading.Lock()


def status(state, args):
    """What voice can and cannot do, answered without starting anything.

    An owner finds out what is missing before approving something that would find out by failing,
    which is a much worse way to learn that a model file was never downloaded.
    """
    import speech

    settings = _settings()
    local = settings.get("local") or {}
    heard = speech.recogniser(local.get("stt"))
    spoken = speech.speaker(local.get("tts"), state.get("api_key"),
                            settings.get("locale") or "en")

    missing = ([] if heard else
               ["a local speech recogniser (whisper.cpp, with a ggml model beside it)"])
    missing += spoken.missing() if hasattr(spoken, "missing") else []

    return {
        "sessions": len(state["sessions"]),
        "recogniser": getattr(heard, "name", None),
        "speaker": getattr(spoken, "name", None) if spoken and spoken.available() else None,
        "model": (local.get("llm") or {}).get("model") or "llama3.1:8b",
        "locale": settings.get("locale") or "en",
        "can_hold_a_conversation": bool(heard) and bool(spoken) and spoken.available(),
        "missing": missing,

        # The two halves, reported apart, because they stopped being the same answer. Nobody's
        # recorded voice is uploaded — recognition runs here and a recording exists only as bytes
        # in memory until it becomes text. The sentence Aurora is about to say does leave.
        "audio_leaves_this_machine": False,
        "text_leaves_this_machine": bool(spoken) and spoken.available(),
        "speech_service": speech.ElevenLabsSpeaker.HOST if spoken and spoken.available() else None,
    }


def session_start(state, args):
    """Begins the interaction for a session Aurora has already authorised.

    The instructions and the tool list arrive from Aurora, composed there from its own personality
    and its own grant. This program writes neither, which is what keeps there being one Aurora
    rather than a second one living in a voice adapter.
    """
    session_id = str(args["session_id"])[:128]

    if session_id in state["sessions"]:
        raise Refused(E_ALREADY, "that session is already running")

    settings = _settings()

    try:
        conversation = _session_for(
            settings, state.get("api_key"),
            instructions=str(args["instructions"]),
            tools=args.get("tools") or [])
    except local_provider.LocalUnavailable as incomplete:
        # Named engines rather than a shrug. "Nothing can carry a conversation" sends somebody
        # looking in the wrong place; "no speech recogniser is installed" does not.
        raise Refused(incomplete.code, incomplete.message)

    if conversation is None:
        raise Refused(
            E_UNSUPPORTED,
            "no interaction provider is configured, so nothing can carry a conversation")

    # The conversation went in twice — once as the third positional, which used to be the
    # telephone transport, and once as .interaction. There is no transport now and there never
    # were two things here.
    session = Session(session_id, args.get("participant") or {})
    session.interaction = conversation

    try:
        session.interaction.start()
    except Exception as unreachable:
        # A layer that will not connect is not a conversation. Reported as a failure rather than a
        # session, because a session that exists and cannot speak is worse than none.
        raise Refused(
            E_PROVIDER,
            "the interaction layer could not be reached (%s)" % type(unreachable).__name__)

    session.state = "active"
    state["sessions"][session_id] = session

    report("voice.session_started", {"session_id": session_id})

    return {"session_id": session_id, "state": session.state}


def poll(state, args):
    """Drains what the interaction layer has said since the last time Aurora asked.

    A queue rather than a callback, for the same reason the Discord plugin reports pending turns
    that way: the plugin has no way to call Aurora, so anything Aurora needs to act on has to be
    waiting when Aurora looks.
    """
    session = _session(state, args)

    for event in session.interaction.poll():
        kind = event["kind"]

        if kind == "tool_requested":
            with session.lock:
                session.pending.append(event)

        elif kind == "heard":
            # Speech, as text. An observation about the conversation and never an instruction,
            # whatever the words are.
            report("voice.heard", {
                "session_id": session.session_id,
                "text": event["text"],
            })

        elif kind == "audio":
            with session.lock:
                session.audio.append(event["audio"])

        elif kind == "interrupted":
            # Somebody talked over Aurora. Stopping is the session's decision and this is where it
            # is made: a voice that finishes its sentence while being interrupted is broadcasting.
            session.interaction.interrupt()
            report("voice.interrupted", {"session_id": session.session_id})

        elif kind == "said":
            report("voice.said", {
                "session_id": session.session_id, "text": event["text"]})

        elif kind == "failed":
            session.state = "failed"
            report("voice.failed", {
                "session_id": session.session_id, "detail": event["detail"]})

    with session.lock:
        waiting, session.pending = session.pending, []
        spoken, session.audio = session.audio, []

    answer = {
        "session_id": session.session_id,
        "state": session.state,
        "tool_requests": waiting,
        "audio": spoken,
    }

    # What the conversation has spent: turns, tokens, and the latency of each stage of each turn.
    # Read from the interaction layer, which is the only thing a session is made of now — it was
    # `session.transport` while there was a telephone, and that attribute went with it.
    if hasattr(session.interaction, "telemetry"):
        answer["telemetry"] = session.interaction.telemetry()

    return answer


def listen(state, args):
    """Puts a slice of microphone audio into the buffer the model is listening to.

    Base64 PCM16 at 24 kHz mono, which is what the service documents. Appended rather than
    committed: with server-side turn detection the service decides when somebody stopped talking,
    and taking that decision back would do it worse.
    """
    session = _session(state, args)
    audio = str(args["audio"])

    session.interaction.append_audio(audio)

    return {"session_id": session.session_id, "bytes": len(audio)}


def tool_result(state, args):
    """Aurora's answer to something the interaction layer asked for.

    Handed on exactly as given. This program does not decide what an outcome means, does not retry
    a refusal, and does not turn an unknown into anything else — it carries four words and the
    sentence that goes with each.
    """
    session = _session(state, args)

    outcome = {
        "outcome": str(args.get("outcome") or FAILED),
        "result_json": args.get("result_json"),
        "detail": args.get("detail"),
    }

    session.interaction.deliver(str(args["request_id"])[:128], outcome)

    return {"session_id": session.session_id, "delivered": outcome["outcome"]}


def interrupt(state, args):
    """Stops whatever is being said, now."""
    session = _session(state, args)
    session.interaction.interrupt()

    return {"session_id": session.session_id, "interrupted": True}


def hangup(state, args):
    """Ends a session and lets go of its transport.

    Only ever reduces what is happening, which is why it asks nobody. Being unable to hang up is
    worse than hanging up unexpectedly.
    """
    session_id = str(args["session_id"])[:128]
    session = state["sessions"].pop(session_id, None)

    if session is None:
        return {"session_id": session_id, "state": "unknown"}

    reason = str(args.get("reason") or "ended")

    try:
        session.interaction.close(reason)
    except Exception:
        # Already gone. The outcome that was wanted.
        pass

    session.state = "ended"
    report("voice.session_ended", {"session_id": session_id, "reason": reason})

    return {"session_id": session_id, "state": "ended", "reason": reason}


def _session(state, args):
    session_id = str(args["session_id"])[:128]
    session = state["sessions"].get(session_id)

    if session is None:
        raise Refused(E_NO_SESSION, "no such voice session is running here")

    return session


READS = {
    "voice.status": status,
    "voice.poll": poll,
}

WRITES = {
    "voice.listen": listen,
    "voice.session.start": session_start,
    "voice.tool_result": tool_result,
    "voice.interrupt": interrupt,
    "voice.hangup": hangup,
}


def handle(state, frame):
    capability = frame.get("capability", "")
    args = frame.get("input") or {}

    if capability in READS:
        return READS[capability](state, args)

    if capability in WRITES:
        return WRITES[capability](state, args)

    raise Refused(
        E_UNSUPPORTED, "this plugin does not offer '%s'" % capability)


def main():
    state = {"sessions": {}}

    for line in sys.stdin:
        try:
            frame = json.loads(line)
        except ValueError:
            continue

        kind = frame.get("kind")

        if kind == "hello":
            secrets = frame.get("secrets") or {}

            # The speech key, held in memory for the life of the process and put in exactly one
            # header. Optional: without it the plugin still answers `voice.status`, which is how an
            # owner finds out it is missing rather than by a sentence that never gets spoken.
            state["api_key"] = secrets.get("elevenlabs_api_key", "")

            say({"kind": "ready", "degraded": not state["api_key"]})
            continue

        if kind == "shutdown":
            for session_id in list(state["sessions"]):
                hangup(state, {"session_id": session_id, "reason": "aurora is stopping"})

            return

        if kind != "call":
            continue

        answer = {"kind": "result", "id": frame.get("id")}

        try:
            answer.update({"ok": True, "output": handle(state, frame)})

        except Refused as refused:
            answer.update({"ok": False, "refusal": refused.code, "detail": refused.message})

        except KeyError as missing:
            answer.update({
                "ok": False, "refusal": E_SCHEMA,
                "detail": "the call is missing %s" % missing})

        except Exception as unexpected:
            # The type, not the text. A message from an unexpected exception is written by whatever
            # threw it and could carry anything.
            answer.update({
                "ok": False, "refusal": E_PROVIDER,
                "detail": "the plugin failed unexpectedly (%s)" % type(unexpected).__name__})

        say(answer)


if __name__ == "__main__":
    main()
