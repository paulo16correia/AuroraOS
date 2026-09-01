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
    # --no-gpu is not a performance choice. whisper.cpp loads its Metal backend by default, the
    # sandbox denies a plugin the GPU, and the result is a segmentation fault rather than a
    # refusal — exit -11 with a log line about loading Metal and nothing else. Recognition of a
    # few seconds of speech is quick enough on the CPU, and a plugin holding the GPU is not
    # something to grant for a transcript.
    # -l auto, because whisper.cpp defaults to English and transcribes everything else as
    # "(speaking in foreign language)" — which is not a failure it reports, it is the transcript.
    # A system that only understands its owner in one language is not one to ship by default.
    #
    # The GPU is used when Aurora granted it and refused when it did not: a large model takes
    # about eight seconds on the graphics processor and seventy on the processor alone, and
    # seventy seconds to hear one sentence is not listening.
    # -bs 1 is greedy decoding. Measured on this machine, a beam search costs ~15% more time and
    # changed no word of a short reply — and in a conversation the time is what is being spent.
    #
    # {language} is "auto" unless the owner says otherwise, and saying otherwise is worth about
    # 400ms on a four-second utterance: detecting the language is a pass over the audio that a
    # person who always speaks Portuguese pays for every sentence, forever.
    #
    # --prompt is the surprising one. It is context, not a command, and seeding it with Aurora's
    # own name is the difference between a small model hearing "Aurora" and hearing "A hora" —
    # measured, on this machine, with the same clip. A name is the word recognition gets wrong
    # most, and telling the recogniser the word exists costs nothing.
    ("whisper-cli",
     ["-m", "{model}", "-f", "{input}", "-l", "{language}", "-bs", "1", "--prompt", "{prompt}",
      "--output-txt", "--no-prints", "{gpu}"]),
    ("whisper.cpp",
     ["-m", "{model}", "-f", "{input}", "-l", "{language}", "-bs", "1", "--prompt", "{prompt}",
      "--output-txt", "{gpu}"]),
    ("whisper", ["--model", "base", "--output_format", "txt", "{input}"]),
]

# Where a whisper.cpp model is likely to be. It is a separate download from the program, and the
# program's default path is relative to wherever it was built — which is never where a package
# manager put it. A recogniser with no model fails on every utterance and says only that it
# exited non-zero.
MODEL_SEARCH = [
    # Beside the plugin, first and for the same reason its libraries are: the sandbox lets a
    # plugin read its own directory and nothing else of the owner's. A model in a home cache is
    # one the plugin cannot open — correctly. What a plugin needs to run ships with it.
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "models"),

    os.path.expanduser("~/.cache/whisper"),
    os.path.expanduser("~/Library/Application Support/Aurora/models"),
    "/opt/homebrew/share/whisper.cpp/models",
    "/usr/local/share/whisper.cpp/models",
]

# Multilingual first. An English-only model transcribes Portuguese into confident nonsense rather
# than failing, which is the worse of the two.
MODEL_NAMES = [
    "ggml-large-v3-turbo.bin", "ggml-medium.bin", "ggml-small.bin", "ggml-base.bin",
    "ggml-base.en.bin", "ggml-tiny.bin",
]

# Local text-to-speech. `say` ships with macOS and speaks without a network.
TTS_ENGINES = [
    ("piper", ["--model", "{model}", "--output_file", "{output}"]),
    # LEI16, not LEF32. `say` writes 32-bit float however it is asked, so the conversion happens
    # in the reader either way — but asking for what is wanted costs nothing and says what is
    # expected.
    #
    # The voice is a setting because it is a matter of taste and of who is listening. The default
    # is whatever the machine speaks with; a name in config.json overrides it.
    ("say", ["-o", "{output}", "--data-format=LEI16@48000", "{voice}", "{text}"]),
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


def find_model(preferred=None):
    """A whisper model file, or None. Separate from the program and separately absent.

    The owner may name one. The trade is speed against a proper noun: measured here, the large
    model takes about seven seconds for a four-second sentence and the base model four tenths of
    one — and the base model mishears the name, unless it is prompted with it (docs/adr/0071).
    """
    for directory in MODEL_SEARCH:
        for name in ([preferred] if preferred else []) + MODEL_NAMES:
            candidate = os.path.join(directory, name)

            if os.path.exists(candidate):
                return candidate

    return None


def find_stt(preferred_model=None):
    """A local speech-to-text program with a model to run, or None.

    Both, because either alone recognises nothing. whisper.cpp with no model exits non-zero on
    every utterance, which reads as speech that could not be understood rather than as a file that
    was never downloaded.
    """
    for name, arguments in STT_ENGINES:
        found = shutil.which(name)

        if not found:
            continue

        model = find_model(preferred_model)

        if "{model}" in " ".join(arguments) and model is None:
            return None

        return {"name": name, "path": found, "arguments": arguments, "model": model}

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
    import dave

    opus = find_opus()
    stt = find_stt()
    tts = find_tts()
    transport = has_transport()
    e2ee = dave.available()

    missing = []

    if not e2ee:
        # Not always fatal: Discord requires it only where a server has it enabled, and answers
        # 4017 when it does. Reported either way, because "the call was refused" is a bad way to
        # find out a library is missing.
        missing.append(
            "the davey library for end-to-end encrypted voice (required by some servers)")
    if not transport:
        missing.append(
            "the voice audio transport, which is not implemented (see docs/adr/0068)")
    if not opus:
        missing.append("libopus (Discord voice carries Opus; install it with your package manager)")
    if not stt:
        if shutil.which("whisper-cli") and not find_model():
            missing.append(
                "a whisper model file — the program is installed but has nothing to run; "
                "put a ggml-*.bin in the plugin's models/ directory, where the sandbox can "
                "reach it")
        else:
            missing.append("a local speech-to-text program (whisper.cpp)")
    if not tts:
        missing.append("a local text-to-speech program (piper, or `say` on macOS)")

    return {
        "can_join": bool(opus and transport),
        "can_listen": bool(opus and stt and transport),
        "can_speak": bool(opus and tts and transport),
        "transport": transport,
        "e2ee": e2ee,
        "opus": opus,
        "stt": stt["name"] if stt else None,
        "tts": tts["name"] if tts else None,
        "missing": missing,

        # Said explicitly, because it is the property that would be quietly lost first.
        "audio_leaves_this_machine": False,
    }


# What whisper says when there is nothing to hear. It does not return empty on silence — it
# returns the most common thing in its training data, which for hours of subtitled video is
# gratitude and sign-offs. Answering these is answering nobody.
HALLUCINATIONS = {
    "thank you.", "thanks for watching!", "thank you for watching!", "you", ".", "bye.",
    "thanks for watching.", "please subscribe.", "[blank_audio]", "(silence)", "so",
    "obrigado.", "obrigada.", "até à próxima.", "tchau.", "muito obrigado.",
}


def is_silence(pcm, threshold=350):
    """Whether this is quiet enough that anything heard in it would be invented.

    Cheaper and more honest than filtering the output: a recogniser handed near-silence produces
    confident sentences, and the only reliable way to not believe them is to not ask.
    """
    if len(pcm) < 4:
        return True

    total = 0
    count = 0

    # Every hundredth sample. Enough to tell speech from a quiet room, and cheap enough to run on
    # every utterance.
    for at in range(0, len(pcm) - 1, 200):
        sample = int.from_bytes(pcm[at:at + 2], "little", signed=True)
        total += abs(sample)
        count += 1

    return count == 0 or (total / count) < threshold


def is_hallucination(text):
    """Whether this is what a recogniser says when it heard nothing."""
    return text.strip().lower() in HALLUCINATIONS


def transcribe(engine, audio_bytes, model=None, timeout=180, gpu=True,
               language="auto", prompt=""):
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
            argument.replace("{input}", source)
                    .replace("{model}", model or engine.get("model") or "")
                    .replace("{language}", language or "auto")
                    .replace("{prompt}", prompt or "")
            for argument in engine["arguments"]
        ]

        # Without the grant the GPU is not merely slow, it is a segmentation fault: whisper.cpp
        # loads its Metal backend by default and the sandbox refuses it. --no-gpu is how the
        # refusal becomes a fallback instead of a crash.
        arguments = [a for a in (
            a.replace("{gpu}", "" if gpu else "--no-gpu") for a in arguments) if a]

        finished = subprocess.run(
            [engine["path"], *arguments], capture_output=True, timeout=timeout, check=False)

        if finished.returncode != 0:
            # What it said, not only that it failed. "exited 1" is true of a missing model, a
            # corrupt file and an unsupported sample rate alike, and they need different fixes.
            complaint = (finished.stderr or finished.stdout or b"").decode(errors="replace")

            raise RuntimeError(
                "%s exited %d: %s" % (
                    engine["name"], finished.returncode, complaint.strip()[-200:] or "no output"))

        transcript = source + ".txt"

        if os.path.exists(transcript):
            with open(transcript, "r") as handle:
                return handle.read().strip()

        return finished.stdout.decode(errors="replace").strip()
    finally:
        # Always, including when the engine failed. A crash is not a reason to leave somebody's
        # voice on the disk.
        shutil.rmtree(directory, ignore_errors=True)


def voices():
    """The voices this machine can speak with, as `say` reports them."""
    import subprocess

    found = shutil.which("say")

    if not found:
        return []

    listed = subprocess.run([found, "-v", "?"], capture_output=True, timeout=20)

    # `say -v ?` writes "Name  lang_REGION  # sample", and a name may carry a parenthesised
    # qualifier — "Eddy (Português (Brasil))" — so the language is found by shape rather than by
    # position. Splitting on whitespace and taking the second field finds half the voices.
    import re

    found_voices = []

    for line in listed.stdout.decode(errors="replace").splitlines():
        match = re.match(r"^(.+?)\s+([a-z]{2}_[A-Z]{2})\s", line)

        if match:
            found_voices.append({
                "name": match.group(1).split(" (")[0].strip(),
                "language": match.group(2),
            })

    return found_voices


def synthesise(engine, text, model=None, timeout=60, voice=None):
    """Turns text into audio, locally. Returns the bytes and leaves nothing behind."""
    if engine is None:
        raise RuntimeError("no local text-to-speech program is installed")

    directory = tempfile.mkdtemp(prefix="aurora-voice-")
    target = os.path.join(directory, "speech.wav")

    try:
        arguments = [
            argument.replace("{output}", target).replace("{model}", model or "")
                    .replace("{text}", text)
                    .replace("{voice}", "-v" + voice if voice else "")
            for argument in engine["arguments"]
        ]

        # An empty placeholder is not an empty argument. `say ""` reads the empty string aloud.
        arguments = [a for a in arguments if a]

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
