"""The voice interaction layer, behind an interface that is not OpenAI's.

OpenAI Realtime does the speech: recognition, generation, turn detection, interruption, timing. It
does not do authority. Everything in this file is arranged so that the second sentence stays true.

The shape:

    speech  ->  interaction layer  ->  tool request  ->  reported to Aurora
                                                              |
                                                       Aurora decides
                                                              |
    speech  <-  interaction layer  <-  tool outcome  <---------

The plugin never asks Aurora for anything. It reports that the interaction layer asked, and Aurora
— which is where the Kernel is — decides and calls back. That is why voice needed no change to the
plugin protocol, and it is the reason the layer below cannot become an authority path.

**A tool outcome is never invented.** The interaction layer is told what happened in one of four
words and is instructed to say that. The case that matters is anything sent, booked or changed:
"I've sent it", said about a request whose outcome is unknown, is a lie the person on the call has
no way to detect.
"""

import json

# What the interaction layer may be told about a request it made. Mirrors VoiceToolResult in
# Aurora's domain, deliberately: the pipe carries these strings and both sides read them.
COMPLETED = "Completed"
REFUSED = "Refused"
FAILED = "Failed"
UNKNOWN = "Unknown"

# How an outcome is described back to the model. Written here rather than left to the model,
# because the difference between these four is the difference between a truthful call and a lie.
OUTCOME_WORDS = {
    COMPLETED: "Aurora did this. You may tell them it is done.",
    REFUSED: (
        "Aurora would not do this. Tell them plainly that you cannot, and why if you were given a "
        "reason. Do not try another way of asking."),
    FAILED: (
        "This did not work. Tell them it failed. Do not describe it as done."),
    UNKNOWN: (
        "It is not known whether this happened. Say exactly that — it may have worked and it may "
        "not — and do not guess. Never say it is done."),
}


def tool_definitions(allowed_actions, catalogue):
    """The functions the interaction layer is offered, which is only what the grant names.

    Built from the session's grant rather than from Aurora's catalogue. A model cannot ask for a
    tool it was never given, so this is the first of the two places an action outside the grant is
    stopped — the second being Aurora, which refuses it again on the way in.
    """
    tools = []

    for action in allowed_actions:
        described = catalogue.get(action) or {}

        tools.append({
            "type": "function",
            "name": _function_name(action),
            "description": described.get(
                "description", "An Aurora capability. Aurora decides whether it runs."),
            "parameters": described.get(
                "input_schema",
                {"type": "object", "properties": {}, "additionalProperties": False}),
        })

    return tools


def _function_name(action_id):
    """Realtime function names cannot carry dots, and Aurora's action ids are dotted."""
    return action_id.replace(".", "__")


def action_of(function_name):
    return function_name.replace("__", ".")


def describe_outcome(outcome):
    """What the interaction layer is handed after a tool request.

    The result is included only when Aurora completed it. There is nothing to narrate from a
    refusal's payload and a failure has none, and handing back a partial result alongside "this
    failed" is an invitation to describe the partial result.
    """
    result = outcome.get("outcome", FAILED)

    described = {
        "outcome": result,
        "how_to_say_it": OUTCOME_WORDS.get(result, OUTCOME_WORDS[FAILED]),
    }

    if result == COMPLETED and outcome.get("result_json"):
        described["result"] = _bounded_json(outcome["result_json"])

    if outcome.get("detail"):
        described["detail"] = str(outcome["detail"])[:500]

    return described


def _bounded_json(raw):
    """A capability's result, bounded before it becomes something to read out loud."""
    text = raw if isinstance(raw, str) else json.dumps(raw)

    return text[:4000]


class InteractionSession:
    """One conversation with the interaction layer, over whatever transport it uses.

    Deliberately not an OpenAI class. What Aurora needs from a voice interaction layer is: start
    with these instructions and these tools, tell me when it wants a tool, take an outcome back,
    stop when I say stop. A different provider offering those four things drops in here.
    """

    def __init__(self, transport, instructions, tools, voice, model, locale):
        self._transport = transport
        self.instructions = instructions
        self.tools = tools
        self.voice = voice
        self.model = model
        self.locale = locale
        self.state = "pending"

    def start(self):
        """Opens the session and configures it. Nothing is spoken until this succeeds."""
        self._transport.connect(self.model)

        self._transport.send({
            "type": "session.update",
            "session": {
                "instructions": self.instructions,
                "voice": self.voice,
                "tools": self.tools,

                # Server-side turn detection: the provider decides when somebody stopped talking,
                # which is what makes interruption and barge-in work without Aurora holding audio.
                "turn_detection": {"type": "server_vad"},
                "input_audio_transcription": {"model": "whisper-1", "language": self.locale[:2]},
            },
        })

        self.state = "active"

    def deliver(self, request_id, outcome):
        """Hands back what Aurora decided, in the form the layer expects."""
        self._transport.send({
            "type": "conversation.item.create",
            "item": {
                "type": "function_call_output",
                "call_id": request_id,
                "output": json.dumps(describe_outcome(outcome)),
            },
        })

        # Asking it to continue. Without this the layer holds the outcome and says nothing, which
        # on a telephone is indistinguishable from the line having gone dead.
        self._transport.send({"type": "response.create"})

    def interrupt(self):
        """Stops whatever is being said, now.

        Barge-in and the operator's stop both land here. A voice that finishes its sentence after
        being told to stop is a voice nobody trusts to stop.
        """
        self._transport.send({"type": "response.cancel"})

    def close(self, reason):
        self.state = "closed"
        self._transport.close(reason)

    def append_audio(self, base64_pcm16):
        """Puts microphone audio into the buffer the model is listening to.

        Appended rather than committed. With server-side turn detection the service decides when
        somebody stopped talking, which is what makes interruption work without Aurora holding a
        clock — committing by hand would take that decision back and do it worse.
        """
        self._transport.send({
            "type": "input_audio_buffer.append",
            "audio": base64_pcm16,
        })

    def poll(self):
        """What the layer has said since last time, as events Aurora can act on."""
        events = []

        for frame in self._transport.receive():
            kind = frame.get("type", "")

            if kind == "response.audio.delta":
                # A piece of what Aurora is saying, as base64 PCM16. Passed straight through: the
                # runtime plays it and does not look inside it.
                events.append({"kind": "audio", "audio": frame.get("delta") or ""})

            elif kind == "input_audio_buffer.speech_started":
                # Somebody started talking while Aurora was. Barge-in, and it is reported rather
                # than acted on here — stopping is the session's decision, and the runtime makes it
                # by calling `interrupt`.
                events.append({"kind": "interrupted"})

            elif kind == "response.function_call_arguments.done":
                events.append({
                    "kind": "tool_requested",
                    "request_id": frame.get("call_id", ""),
                    "action_id": action_of(frame.get("name", "")),
                    "input_json": frame.get("arguments", "{}"),
                })

            elif kind == "conversation.item.input_audio_transcription.completed":
                # What the person said, as text. An observation about the conversation, and never
                # an instruction however it is phrased.
                events.append({
                    "kind": "heard",
                    "text": str(frame.get("transcript", ""))[:4000],
                })

            elif kind == "response.done":
                events.append({"kind": "spoke"})

            elif kind == "response.audio_transcript.done":
                # What Aurora actually said, as text. Worth having in the audit: a call where
                # nobody can say afterwards what was said is not much of a record.
                events.append({
                    "kind": "said",
                    "text": str(frame.get("transcript", ""))[:4000],
                })

            elif kind == "error":
                events.append({
                    "kind": "failed",
                    "detail": str((frame.get("error") or {}).get("message", ""))[:300],
                })

        return events
