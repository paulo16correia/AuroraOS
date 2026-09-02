#!/usr/bin/env python3
"""Run the voice slice locally: your microphone, Aurora, your speakers.

**Run this yourself.** It is a development harness, not a capability — it opens your microphone,
and a program that opens a microphone on somebody's behalf should be one they started.

    python3 plugins/voice/local_slice.py

It needs an OpenAI key in the environment (OPENAI_API_KEY) and `ffmpeg` for audio. Neither is
bundled, and it says which is missing rather than failing obscurely.

**What this does and does not prove.** It drives the real transport against the real service and
plays what comes back, which is the only way to find out how the conversation actually sounds. It
does *not* go through Aurora's Kernel: a tool the model asks for here is reported and refused,
because the governed path runs inside Aurora and this harness is outside it. Use it to hear the
voice; use the test suite to prove the governance.

The reason it exists at all: microphone capture from inside the plugin sandbox has never been
tried, and this separates "does the audio work" from "does the sandbox permit it".
"""

import base64
import json
import os
import shutil
import subprocess
import sys
import time

import interaction
from realtime import CHANNELS, SAMPLE_RATE, RealtimeTransport

# A tenth of a second. Small enough that interruption feels immediate, and the same floor Aurora's
# rate limit puts under a governed audio capability.
CHUNK_MS = 100
CHUNK_BYTES = int(SAMPLE_RATE * 2 * CHANNELS * CHUNK_MS / 1000)


def missing():
    """What this machine has not got, said before anything is opened."""
    absent = []

    if not os.environ.get("OPENAI_API_KEY"):
        absent.append("OPENAI_API_KEY in the environment")

    if shutil.which("ffmpeg") is None:
        absent.append("ffmpeg (brew install ffmpeg) — for the microphone")

    if shutil.which("ffplay") is None and shutil.which("afplay") is None:
        absent.append("ffplay or afplay — for the speakers")

    return absent


def microphone():
    """Raw PCM16 from the default input, at the rate the service wants.

    avfoundation is macOS's capture device. The first run prompts for microphone permission, which
    is granted to the terminal running this rather than to Aurora — one of the reasons this is a
    harness rather than a capability.
    """
    return subprocess.Popen(
        ["ffmpeg", "-hide_banner", "-loglevel", "error",
         "-f", "avfoundation", "-i", ":default",
         "-ar", str(SAMPLE_RATE), "-ac", str(CHANNELS), "-f", "s16le", "-"],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)


def speakers():
    """Somewhere to write PCM16 that comes out of the machine."""
    if shutil.which("ffplay"):
        return subprocess.Popen(
            ["ffplay", "-hide_banner", "-loglevel", "error", "-nodisp", "-autoexit",
             "-f", "s16le", "-ar", str(SAMPLE_RATE), "-ac", str(CHANNELS), "-"],
            stdin=subprocess.PIPE)

    return None


def main():
    absent = missing()

    if absent:
        print("This machine is missing:")

        for item in absent:
            print("  -", item)

        return 2

    instructions = (
        "You are Aurora. You are a persistent entity in the Aurora system, not a separate "
        "assistant for this channel. Speak the way a person speaks out loud, in sentences. Do not "
        "describe yourself as an AI assistant or a language model. Never claim to be a particular "
        "person.\n\n"
        "This harness is outside Aurora's governance: you can ask for nothing here, and if "
        "somebody asks you to do something, say that you cannot do it on this connection.")

    transport = RealtimeTransport(os.environ["OPENAI_API_KEY"])
    session = interaction.InteractionSession(
        transport, instructions, tools=[], voice="alloy",
        model=os.environ.get("AURORA_REALTIME_MODEL", "gpt-realtime"), locale="pt-PT")

    print("Connecting...")
    session.start()
    print("Connected. Speak — ctrl-C to stop.\n")

    capture = microphone()
    playback = speakers()
    started = time.monotonic()

    try:
        while True:
            chunk = capture.stdout.read(CHUNK_BYTES)

            if not chunk:
                break

            session.append_audio(base64.b64encode(chunk).decode())

            for event in session.poll():
                kind = event["kind"]

                if kind == "audio" and playback is not None:
                    playback.stdin.write(base64.b64decode(event["audio"]))
                    playback.stdin.flush()

                elif kind == "heard":
                    print("  you:", event["text"])

                elif kind == "said":
                    print("aurora:", event["text"])

                elif kind == "interrupted":
                    # Barge-in. Stop talking rather than finishing the sentence, which is the
                    # difference between a conversation and a broadcast.
                    session.interrupt()

                elif kind == "tool_requested":
                    # Outside Aurora, so there is nothing to ask. Answered as a refusal rather
                    # than left hanging, and never as a result this harness made up.
                    session.deliver(event["request_id"], {
                        "outcome": interaction.REFUSED,
                        "detail": "this harness has no connection to Aurora",
                    })

                elif kind == "failed":
                    print("failed:", event["detail"])
                    return 1

    except KeyboardInterrupt:
        print("\nStopping.")

    finally:
        capture.terminate()

        if playback is not None:
            try:
                playback.stdin.close()
            except OSError:
                pass

        session.close("the harness stopped")

        spent = transport.telemetry()
        spent["wall_seconds"] = round(time.monotonic() - started, 1)

        print("\n" + json.dumps(spent, indent=2))

    return 0


if __name__ == "__main__":
    sys.exit(main())
