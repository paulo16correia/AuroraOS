"""A deterministic stand-in for the voice interaction layer.

Behaves like a transport that has been told what to say, including badly: a tool request with
arguments that are not JSON, an error frame, a disconnect. Tests use this so none of them need an
OpenAI key, and so the failure paths are reachable at all — a real provider cannot be asked to
disconnect on cue.

What it does not do is speak. Audio is the one part of the real thing that has no deterministic
stand-in, and nothing in this file pretends otherwise.
"""


class FakeTransport:
    """Records what was sent and replays what it was told to receive."""

    def __init__(self, script=None):
        self.sent = []
        self.script = list(script or [])
        self.connected = False
        self.closed_because = None
        self.fail_on_connect = None

    def connect(self, model):
        if self.fail_on_connect:
            raise ConnectionError(self.fail_on_connect)

        self.connected = True
        self.model = model

    def send(self, frame):
        if not self.connected:
            # A layer that accepted frames before connecting would let a test pass against a
            # session that never established, which is the failure being guarded against.
            raise ConnectionError("not connected")

        self.sent.append(frame)

    def receive(self):
        frames, self.script = self.script, []
        return frames

    def close(self, reason):
        self.connected = False
        self.closed_because = reason

    # ---- what a test makes happen ----

    def says(self, *frames):
        self.script.extend(frames)
        return self

    def wants_tool(self, call_id, name, arguments="{}"):
        return self.says({
            "type": "response.function_call_arguments.done",
            "call_id": call_id, "name": name, "arguments": arguments,
        })

    def heard(self, text):
        return self.says({
            "type": "conversation.item.input_audio_transcription.completed",
            "transcript": text,
        })

    def errors(self, message):
        return self.says({"type": "error", "error": {"message": message}})

    def sent_of_type(self, kind):
        return [f for f in self.sent if f.get("type") == kind]
