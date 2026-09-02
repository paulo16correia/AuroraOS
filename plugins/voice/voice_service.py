#!/usr/bin/env python3
"""Aurora's voice plugin.

Holds the two connections Aurora's own process may not: the transport to whoever is carrying the
call, and the one to whatever turns speech into words and back. Speaks Aurora's service protocol on
stdin/stdout, one JSON object per line.

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
import time

import interaction
import provider
import realtime
from fake_realtime import FakeTransport

E_UNSUPPORTED = "voice_unsupported_capability"

# The model, overridable by configuration. Named here rather than assumed in three places, and
# read from settings so an installation can move without an edit.
DEFAULT_MODEL = "gpt-realtime"
E_NO_SESSION = "voice_no_session"
E_ALREADY = "voice_session_exists"


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
        with open(os.path.join(here, "config.json"), "r") as handle:
            return json.load(handle) or {}
    except (OSError, ValueError):
        return {}


def _transport_for(settings, api_key):
    """The interaction transport: the real service, or the deterministic stand-in.

    The stand-in is selected only by configuration a shipped installation does not have. It exists
    because the failures worth testing — a layer that will not connect, one that disconnects
    mid-call, one that returns a tool name nobody offered — cannot be asked for on demand from a
    real service.
    <br>
    With no key there is no transport and no session. Refusing is the honest answer: a plugin that
    quietly fell back to a stand-in would let a call appear to happen.
    """
    config = settings.get("realtime") or {}

    if config.get("transport") == "fake":
        return FakeTransport(script=config.get("script") or [])

    if not api_key:
        return None

    return realtime.RealtimeTransport(
        api_key,
        url=config.get("url") or realtime.REALTIME_URL,
        timeout=int(config.get("timeout_seconds") or 30))


class Session:
    """One conversation this process is carrying."""

    def __init__(self, session_id, participant, transport):
        self.session_id = session_id
        self.participant = participant
        self.transport = transport
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
    settings = _settings()
    config = settings.get("realtime") or {}
    transport = _transport_for(settings, state.get("api_key"))

    return {
        "sessions": len(state["sessions"]),
        "transport": config.get("transport") or ("openai" if state.get("api_key") else "none"),
        "model": config.get("model") or DEFAULT_MODEL,
        "provider": (settings.get("provider") or {}).get("kind") or "none",

        # Said plainly, and without contacting anybody: a caller deserves to know there is no
        # speech layer before approving something that would find out by failing.
        "can_hold_a_conversation": transport is not None,
        "missing": [] if transport is not None else ["openai_api_key"],
    }


def inbound(state, args):
    """A provider event arrived. Validate it and report who is calling.

    Creates nothing. Aurora decides whether there is to be a session at all — this only says the
    event is real and what it claims.
    """
    settings = _settings()
    guard = state["guard"]

    form = args.get("form") or {}
    signature = args.get("signature") or ""
    url = args.get("url") or ""

    guard.check(url, form, signature, event_id=args.get("event_id"),
                timestamp=args.get("timestamp"))

    call = provider.parse_call_event(form)

    report("voice.call_received", {
        "external_ref": call["external_ref"],
        "claimed_from": call["claimed_from"],
        "status": call["status"],
    })

    return {
        "external_ref": call["external_ref"],

        # Named for what it is. The telephone network carries whatever the originating carrier
        # says, so this is somebody's claim about who is calling and never evidence of it.
        "claimed_from": call["claimed_from"],
        "to": call["to"],
        "status": call["status"],
        "verification": "channel_asserted",
    }


def session_start(state, args):
    """Begins the interaction for a session Aurora has already authorised.

    The instructions and the tool list arrive from Aurora, composed there from its own personality
    and its own grant. This program writes neither, which is what keeps there being one Aurora
    rather than a second one living in a voice adapter.
    """
    session_id = str(args["session_id"])[:128]

    if session_id in state["sessions"]:
        raise provider.ProviderRefused(E_ALREADY, "that session is already running")

    settings = _settings()
    transport = _transport_for(settings, state.get("api_key"))

    if transport is None:
        raise provider.ProviderRefused(
            provider.E_UNSUPPORTED,
            "no interaction transport is configured, so nothing can carry a conversation")

    session = Session(session_id, args.get("participant") or {}, transport)

    session.interaction = interaction.InteractionSession(
        transport,
        instructions=str(args["instructions"]),
        tools=args.get("tools") or [],
        voice=str(args.get("voice") or "alloy"),
        model=str(args.get("model") or (settings.get("realtime") or {}).get("model")
                  or DEFAULT_MODEL),
        locale=str(args.get("locale") or "pt-PT"),
    )

    try:
        session.interaction.start()
    except Exception as unreachable:
        # A layer that will not connect is not a conversation. Reported as a failure rather than a
        # session, because a session that exists and cannot speak is worse than none.
        raise provider.ProviderRefused(
            provider.E_PROVIDER,
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

    if isinstance(session.transport, realtime.RealtimeTransport):
        # What the conversation has moved, which is the one number that predicts what it cost.
        answer["telemetry"] = session.transport.telemetry()

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
        "outcome": str(args.get("outcome") or interaction.FAILED),
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


def outbound(state, args):
    """Places a call, having been told by Aurora that it may.

    Everything that decides whether it may — the purpose, the approval, the destination policy, the
    expiry — was checked in Aurora before this was called. What arrives here is a decision, and this
    program's part is to dial.
    """
    settings = _settings()
    kind = (settings.get("provider") or {}).get("kind")

    if kind != "fake":
        raise provider.ProviderRefused(
            provider.E_UNSUPPORTED,
            "no telephone provider is configured; outbound calling is not available")

    to = provider.e164(str(args["to"]))
    from_number = provider.e164(str(args["from"]))

    placed = state["provider"].place_call(to, from_number, str(args["session_id"])[:128])

    report("voice.call_placed", {
        "session_id": args["session_id"], "external_ref": placed["external_ref"]})

    return {"external_ref": placed["external_ref"], "status": placed["status"]}


def _session(state, args):
    session_id = str(args["session_id"])[:128]
    session = state["sessions"].get(session_id)

    if session is None:
        raise provider.ProviderRefused(E_NO_SESSION, "no such voice session is running here")

    return session


READS = {
    "voice.status": status,
    "voice.poll": poll,
}

WRITES = {
    "voice.listen": listen,
    "voice.inbound": inbound,
    "voice.session.start": session_start,
    "voice.tool_result": tool_result,
    "voice.interrupt": interrupt,
    "voice.hangup": hangup,
    "voice.outbound": outbound,
}


def handle(state, frame):
    capability = frame.get("capability", "")
    args = frame.get("input") or {}

    if capability in READS:
        return READS[capability](state, args)

    if capability in WRITES:
        return WRITES[capability](state, args)

    raise provider.ProviderRefused(
        E_UNSUPPORTED, "this plugin does not offer '%s'" % capability)


def main():
    state = {"sessions": {}, "guard": None, "provider": None}

    for line in sys.stdin:
        try:
            frame = json.loads(line)
        except ValueError:
            continue

        kind = frame.get("kind")

        if kind == "hello":
            secrets = frame.get("secrets") or {}

            # The provider's token, from Aurora's vault over the pipe. Never written down here and
            # never given to the interaction layer, which has no use for one.
            state["guard"] = provider.WebhookGuard(secrets.get("provider_auth_token", ""))
            state["provider"] = provider.FakePhoneProvider(
                secrets.get("provider_auth_token", ""))

            # The speech key, held in memory for the life of the process and put in exactly one
            # Authorization header. Optional: without it the plugin still answers `voice.status`,
            # which is how an owner finds out it is missing rather than by a failed call.
            state["api_key"] = secrets.get("openai_api_key", "")

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

        except provider.ProviderRefused as refused:
            answer.update({"ok": False, "refusal": refused.code, "detail": refused.message})

        except KeyError as missing:
            answer.update({
                "ok": False, "refusal": provider.E_SCHEMA,
                "detail": "the call is missing %s" % missing})

        except Exception as unexpected:
            # The type, not the text. A message from an unexpected exception is written by whatever
            # threw it and could carry anything.
            answer.update({
                "ok": False, "refusal": provider.E_PROVIDER,
                "detail": "the plugin failed unexpectedly (%s)" % type(unexpected).__name__})

        say(answer)


if __name__ == "__main__":
    main()
