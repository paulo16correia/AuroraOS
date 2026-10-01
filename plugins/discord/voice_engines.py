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

import array
import io
import os
import secrets
import shutil
import subprocess
import tempfile
import wave

# What Discord's voice protocol requires, and what nothing in Python's standard library provides.
# Named here so a refusal can tell somebody exactly what is missing rather than failing obscurely.
OPUS_LIBRARIES = ["libopus.so.0", "libopus.0.dylib", "libopus.dylib", "libopus.so", "opus.dll"]
OPUS_SEARCH = ["/opt/homebrew/lib", "/usr/local/lib", "/usr/lib", "/usr/lib/x86_64-linux-gnu"]

# How many threads recognition gets. whisper.cpp defaults to four however many the machine has,
# which on a six-core processor leaves most of it idle while somebody waits to be answered — 28%
# of the time on a four-second utterance, measured here, for a number it already knew. Half the
# logical processors is the physical core count on anything with SMT, and using every one measured
# *slower* than eight: recognition is not the only thread in this plugin, and the audio it is
# transcribing keeps arriving while it runs.
def _default_threads():
    return max(1, (os.cpu_count() or 4) // 2)


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
     ["-m", "{model}", "-f", "{input}", "-l", "{language}", "-bs", "1", "-t", "{threads}",
      "--prompt", "{prompt}", "--output-txt", "--no-prints", "{gpu}"]),
    ("whisper.cpp",
     ["-m", "{model}", "-f", "{input}", "-l", "{language}", "-bs", "1", "-t", "{threads}",
      "--prompt", "{prompt}", "--output-txt", "{gpu}"]),
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
    # --length_scale is here for the pitch setting, not for speed: see _shift_pitch. At 1.0 it is
    # piper's own default and changes nothing.
    ("piper", ["--model", "{model}", "--length_scale", "{length_scale}", "--output_file", "{output}"]),
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


def _engine_dirs():
    """The plugin's own directories, which the sandbox grants it — where a bundled engine lives.

    A confined plugin's PATH is the system directories alone, so an engine installed elsewhere is
    unreachable to it. Its own directory is not: the sandbox lets it read and execute what ships
    beside it, which is the same reason the whisper model is looked for here first. So a `bin/`
    folder beside the plugin, and the plugin's own folder, are searched before PATH.
    """
    here = os.path.dirname(os.path.abspath(__file__))
    return [os.path.join(here, "bin"), here]


def _find_program(name):
    """An engine executable, preferring one shipped with the plugin over one on PATH.

    Bundled first so a confined plugin can use engines that ship with it; PATH second so an
    unconfined install, and macOS's built-in `say`, keep working exactly as before.
    """
    extensions = ["", ".exe", ".bat", ".cmd"] if os.name == "nt" else [""]

    for directory in _engine_dirs():
        for extension in extensions:
            candidate = os.path.join(directory, name + extension)
            if os.path.isfile(candidate):
                return candidate

    return shutil.which(name)


def find_voice_model(preferred=None):
    """A piper voice (an .onnx beside its .json), or None.

    Piper is the one text-to-speech engine here that cannot speak without a model, the same way
    whisper cannot listen without one — and nothing supplies it a path, so without this it is found
    and then fails on every sentence. Looked for where the whisper model is looked for, so what a
    plugin needs to run keeps shipping beside the plugin, where the sandbox can reach it.

    `preferred` is a voice named in the owner's settings, with or without the .onnx. It is worth
    naming: which language a voice speaks is not a detail, and taking whichever file sorts first
    means a Brazilian voice reads European Portuguese to somebody who installed both. A named
    voice that is not installed falls back to one that is rather than going silent — being unable
    to answer is worse than answering in the wrong accent, and readiness reports which is in use.
    """
    installed = []

    for directory in MODEL_SEARCH:
        if not os.path.isdir(directory):
            continue

        for name in sorted(n for n in os.listdir(directory) if n.endswith(".onnx")):
            path = os.path.join(directory, name)

            if preferred in (name, os.path.splitext(name)[0]):
                return path

            installed.append(path)

    return installed[0] if installed else None


def find_stt(preferred_model=None):
    """A local speech-to-text program with a model to run, or None.

    Both, because either alone recognises nothing. whisper.cpp with no model exits non-zero on
    every utterance, which reads as speech that could not be understood rather than as a file that
    was never downloaded.
    """
    for name, arguments in STT_ENGINES:
        found = _find_program(name)

        if not found:
            continue

        model = find_model(preferred_model)

        if "{model}" in " ".join(arguments) and model is None:
            return None

        return {"name": name, "path": found, "arguments": arguments, "model": model}

    return None


def _workspace():
    """A directory for one utterance's scratch files, that the plugin can actually write in.

    Not `tempfile.mkdtemp`. Since Python 3.13 that applies its 0700 as a real Windows ACL, which
    replaces the inherited one and blocks further inheritance — so a confined plugin creates the
    directory successfully and then cannot open a single file inside it. Every utterance came back
    as `PermissionError: utterance.wav`, which reads like a broken recogniser and is not one.

    Inheriting is not the weaker choice here. TEMP under the sandbox is the plugin's own working
    directory: already the owner's, already granted to this one container and to no other, and
    already unreachable by anything else. What 0700 defends against is a *shared* temp directory,
    which is what POSIX still gets below.
    """
    directory = os.path.join(tempfile.gettempdir(), "aurora-voice-" + secrets.token_hex(6))

    if os.name == "nt":
        os.mkdir(directory)
    else:
        os.mkdir(directory, 0o700)

    return directory


def find_tts(voice=None):
    """A local text-to-speech program, or None. `voice` names which one to speak with."""
    for name, arguments in TTS_ENGINES:
        found = _find_program(name)

        if not found:
            continue

        model = find_voice_model(voice) if "{model}" in " ".join(arguments) else None

        if "{model}" in " ".join(arguments) and model is None:
            # Installed but with nothing to speak with. Skipped rather than returned, so a later
            # engine that needs no model still gets its turn.
            continue

        return {"name": name, "path": found, "arguments": arguments, "model": model}

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


def readiness(voice=None):
    """What voice can and cannot do on this machine, as a plain answer.

    Reported rather than discovered at the moment of failure: somebody deciding whether to have
    Aurora join a call should be able to find out first, and an error in the middle of a
    conversation is a bad way to learn that a codec is missing.
    """
    import dave

    opus = find_opus()
    stt = find_stt()
    tts = find_tts(voice)
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
        if _find_program("whisper-cli") and not find_model():
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
        # Which voice, not only that there is one. Two are commonly installed and they are
        # different languages; "tts: piper" does not tell anybody which one is answering.
        "voice": os.path.basename(tts["model"]) if tts and tts.get("model") else None,
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
               language="auto", prompt="", threads=None):
    """Turns speech into text, locally, and keeps neither the audio nor the file.

    The audio touches the disk because these programs read files, and it is removed in the same
    call that wrote it. Keeping recordings would mean Aurora holding a transcript of a private
    conversation nobody agreed to it holding.
    """
    if engine is None:
        raise RuntimeError("no local speech-to-text program is installed")

    directory = _workspace()
    source = os.path.join(directory, "utterance.wav")

    try:
        with open(source, "wb") as handle:
            handle.write(audio_bytes)

        arguments = [
            argument.replace("{input}", source)
                    .replace("{model}", model or engine.get("model") or "")
                    .replace("{language}", language or "auto")
                    .replace("{prompt}", prompt or "")
                    .replace("{threads}", str(threads or _default_threads()))
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
            # UTF-8 named: a transcript is speech, so it is exactly the file most likely
            # to hold a character Python on Windows would otherwise fail to decode.
            with open(transcript, "r", encoding="utf-8") as handle:
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


def _length_scale_for(pitch):
    """How much to slow piper down so that compressing by `pitch` lands back at normal speed.

    Not `pitch` itself, which is the obvious guess and is wrong by half. `--length_scale` stretches
    phoneme length, and the sentence silence around it does not stretch with it, so the duration a
    given scale actually buys is about half of what it asks for. Measured on this machine, three
    runs each because piper's noise makes any single one unreliable:

        length_scale  1.0 -> 1.01x    1.2 -> 1.08x    1.4 -> 1.20x

    which is a duration ratio of 1 + (scale - 1) / 2, so the scale needed for a ratio of `pitch` is
    twice the distance from one. At 1.4 that predicts 1.20 and measured 1.20.
    """
    return 1.0 + (pitch - 1.0) * 2.0


def _shift_pitch(wav_bytes, factor):
    """Raises a voice by `factor` without changing how fast it talks.

    Piper has no pitch control, so this is the two-step every vocoder-less engine uses: ask it to
    speak *slower* by the factor, then resample the result *shorter* by the same factor. The two
    cancel in duration and compound in pitch.

    Needed because there is no other lever. Piper's entire Portuguese catalogue is five voices and
    the highest of them measures 181 Hz — the same as a voice Windows ships as female — so "use a
    different one" is not an option that exists. Kept as a setting, at 1.0 by default, because a
    shifted voice is a processed voice and whether it sounds better is the owner's ear, not a fact.
    """
    source = wave.open(io.BytesIO(wav_bytes))
    channels, width, rate, frames = (
        source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getnframes())
    raw = source.readframes(frames)

    if width != 2:
        # Only 16-bit is resampled here. Anything else is handed back untouched rather than
        # mangled: a wrong-width "shift" is silence or noise, and both are worse than a low voice.
        return wav_bytes

    samples = array.array("h")
    samples.frombytes(raw)

    total = len(samples) // channels
    kept = int(total / factor)
    shifted = array.array("h")

    for i in range(kept):
        # Linear interpolation between the two neighbouring frames. Nearest-neighbour is audible
        # as a rasp on sibilants; this is one multiply more and does not have it.
        position = i * factor
        left = int(position)
        right = min(left + 1, total - 1)
        weight = position - left

        for channel in range(channels):
            a = samples[left * channels + channel]
            b = samples[right * channels + channel]
            shifted.append(int(a + (b - a) * weight))

    out = io.BytesIO()
    written = wave.open(out, "wb")
    written.setnchannels(channels)
    written.setsampwidth(width)
    written.setframerate(rate)
    written.writeframes(shifted.tobytes())
    written.close()

    return out.getvalue()


def synthesise(engine, text, model=None, timeout=60, voice=None, pitch=1.0):
    """Turns text into audio, locally. Returns the bytes and leaves nothing behind."""
    if engine is None:
        raise RuntimeError("no local text-to-speech program is installed")

    directory = _workspace()
    target = os.path.join(directory, "speech.wav")

    # The caller may name one; otherwise the engine carries whatever it was found with.
    model = model or engine.get("model")

    try:
        arguments = [
            argument.replace("{output}", target)
                    .replace("{length_scale}", "%.3f" % _length_scale_for(pitch)).replace("{model}", model or "")
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
            spoken = handle.read()

        # Only where the engine was actually asked to slow down. Compressing audio that was
        # rendered at normal speed would raise the pitch and speed the voice up with it.
        if pitch != 1.0 and "{length_scale}" in " ".join(engine["arguments"]):
            spoken = _shift_pitch(spoken, pitch)

        return spoken
    finally:
        shutil.rmtree(directory, ignore_errors=True)
