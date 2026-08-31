"""Speech in and out, on this machine only.

Aurora is local-only, and audio is the hardest place to keep that promise: the easy way to do
speech recognition is to send somebody's voice to a service, and doing that would take a private
conversation off the owner's machine without anybody deciding to. So every engine here is a program
already installed locally, and there is no fallback that reaches the network. If nothing local is
available the capability refuses and says what to install.

What this file does not do is as important as what it does. It never uploads audio. It never keeps
audio. A recording exists as bytes in memory for as long as it takes to turn into text, and the
text is what leaves.
"""

import os
import shutil
import subprocess
import tempfile

# What Discord's voice protocol requires, and what nothing in Python's standard library provides.
# Named here so a refusal can tell somebody exactly what is missing rather than failing obscurely.
OPUS_LIBRARIES = ["libopus.so.0", "libopus.0.dylib", "libopus.dylib", "libopus.so", "opus.dll"]
OPUS_SEARCH = ["/opt/homebrew/lib", "/usr/local/lib", "/usr/lib", "/usr/lib/x86_64-linux-gnu"]

# Local speech-to-text, in the order they are preferred. Each is a program the owner installed.
STT_ENGINES = [
    ("whisper-cli", ["-m", "{model}", "-f", "{input}", "--output-txt", "--no-prints"]),
    ("whisper.cpp", ["-m", "{model}", "-f", "{input}", "--output-txt"]),
    ("whisper", ["--model", "base", "--output_format", "txt", "{input}"]),
]

# Local text-to-speech. `say` ships with macOS and speaks without a network.
TTS_ENGINES = [
    ("piper", ["--model", "{model}", "--output_file", "{output}"]),
    ("say", ["-o", "{output}", "--data-format=LEF32@48000", "{text}"]),
    ("espeak-ng", ["-w", "{output}", "{text}"]),
]


def find_opus():
    """The Opus library, if it can actually be opened.

    Asks the codec module rather than looking for the file, because those are different questions.
    A library can sit plainly on disk and still fail to load — the dynamic loader does not search
    /opt/homebrew/lib — and answering the easy question is how this reported voice as ready while
    every join failed with the codec missing.
    """
    import opus_codec

    return opus_codec.library() if opus_codec.available() else None


def find_stt():
    """A local speech-to-text program, or None."""
    for name, arguments in STT_ENGINES:
        found = shutil.which(name)
        if found:
            return {"name": name, "path": found, "arguments": arguments}

    return None


def find_tts():
    """A local text-to-speech program, or None."""
    for name, arguments in TTS_ENGINES:
        found = shutil.which(name)
        if found:
            return {"name": name, "path": found, "arguments": arguments}

    return None


def has_transport():
    """Whether the leg that actually carries audio exists.

    It does not. `voice_transport.py` would hold the voice websocket, the UDP flow, the AEAD cipher
    and the Opus framing, and it is not written (docs/adr/0068).

    Checked rather than assumed because the alternative is worse than refusing: joining is a
    gateway message, so Aurora would appear in the channel and be seen by everybody in it, while
    hearing nothing and saying nothing. A silent presence in somebody's conversation reads as
    Aurora ignoring them, and there is no way for them to tell it apart from a bug.
    """
    return os.path.exists(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                       "voice_transport.py"))


def readiness():
    """What voice can and cannot do on this machine, as a plain answer.

    Reported rather than discovered at the moment of failure: somebody deciding whether to have
    Aurora join a call should be able to find out first, and an error in the middle of a
    conversation is a bad way to learn that a codec is missing.
    """
    opus = find_opus()
    stt = find_stt()
    tts = find_tts()
    transport = has_transport()

    missing = []
    if not transport:
        missing.append(
            "the voice audio transport, which is not implemented (see docs/adr/0068)")
    if not opus:
        missing.append("libopus (Discord voice carries Opus; install it with your package manager)")
    if not stt:
        missing.append("a local speech-to-text program (whisper.cpp)")
    if not tts:
        missing.append("a local text-to-speech program (piper, or `say` on macOS)")

    return {
        "can_join": bool(opus and transport),
        "can_listen": bool(opus and stt and transport),
        "can_speak": bool(opus and tts and transport),
        "transport": transport,
        "opus": opus,
        "stt": stt["name"] if stt else None,
        "tts": tts["name"] if tts else None,
        "missing": missing,

        # Said explicitly, because it is the property that would be quietly lost first.
        "audio_leaves_this_machine": False,
    }


def transcribe(engine, audio_bytes, model=None, timeout=60):
    """Turns speech into text, locally, and keeps neither the audio nor the file.

    The audio touches the disk because these programs read files, and it is removed in the same
    call that wrote it. Keeping recordings would mean Aurora holding a transcript of a private
    conversation nobody agreed to it holding.
    """
    if engine is None:
        raise RuntimeError("no local speech-to-text program is installed")

    directory = tempfile.mkdtemp(prefix="aurora-voice-")
    source = os.path.join(directory, "utterance.wav")

    try:
        with open(source, "wb") as handle:
            handle.write(audio_bytes)

        arguments = [
            argument.replace("{input}", source).replace("{model}", model or "")
            for argument in engine["arguments"]
        ]

        finished = subprocess.run(
            [engine["path"], *arguments], capture_output=True, timeout=timeout, check=False)

        if finished.returncode != 0:
            raise RuntimeError("%s exited %d" % (engine["name"], finished.returncode))

        transcript = source + ".txt"

        if os.path.exists(transcript):
            with open(transcript, "r") as handle:
                return handle.read().strip()

        return finished.stdout.decode(errors="replace").strip()
    finally:
        # Always, including when the engine failed. A crash is not a reason to leave somebody's
        # voice on the disk.
        shutil.rmtree(directory, ignore_errors=True)


def synthesise(engine, text, model=None, timeout=60):
    """Turns text into audio, locally. Returns the bytes and leaves nothing behind."""
    if engine is None:
        raise RuntimeError("no local text-to-speech program is installed")

    directory = tempfile.mkdtemp(prefix="aurora-voice-")
    target = os.path.join(directory, "speech.wav")

    try:
        arguments = [
            argument.replace("{output}", target).replace("{model}", model or "")
                    .replace("{text}", text)
            for argument in engine["arguments"]
        ]

        command = [engine["path"], *arguments]
        stdin = text.encode() if engine["name"] == "piper" else None

        finished = subprocess.run(
            command, input=stdin, capture_output=True, timeout=timeout, check=False)

        if finished.returncode != 0 or not os.path.exists(target):
            raise RuntimeError("%s produced no audio" % engine["name"])

        with open(target, "rb") as handle:
            return handle.read()
    finally:
        shutil.rmtree(directory, ignore_errors=True)
