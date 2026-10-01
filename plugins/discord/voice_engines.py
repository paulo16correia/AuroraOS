"""Speech in and out, and the one place where Aurora is not local.

**Listening is local and stays local.** The easy way to do speech recognition is to send somebody's
voice to a service, and doing that would take a private conversation off the owner's machine
without anybody deciding to. So recognition is a program already installed here, with no fallback
that reaches the network. This file never uploads audio and never keeps it: a recording exists as
bytes in memory for as long as it takes to become text, and then it is gone.

**Speaking is not local, and that was a decision.** Every local voice that could be had was
measured, and in the language this was built for none of them was good enough to be Aurora's — the
one European Portuguese voice in Piper's catalogue is male and band-limited, and the alternatives
were licence-blocked, Brazilian, or too heavy to sit on the same card as the language model. Rather
than ship a voice the owner does not want, this leg was given to ElevenLabs, and the cost is stated
instead of buried: **the sentence Aurora is about to say leaves the machine.**

The two facts are reported separately by `readiness` — `audio_leaves_this_machine` and
`text_leaves_this_machine` — because they are no longer the same answer, and a reader who assumes
one from the other would be wrong in a way that matters.

There is no local fallback. A voice the owner rejected is not a safety net, and falling back to it
mid-conversation would be worse than failing: Aurora has nothing to say without her language model
anyway, so a refusal that explains itself is the honest behaviour when speech cannot be made.
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


# ---- how much of the encoder's window an utterance needs ----
#
# Whisper's encoder always walks a thirty-second window — 1500 context units, fifty per second —
# however little was actually said. In a conversation that is almost all padding: a second and a
# half of "Aurora, estás a ouvir-me bem?" pays the same two seconds of encoding a full paragraph
# would, and encoding was 73% of the time recognition took.
#
# `--audio-ctx` shortens the window. Measured on this machine, three runs per setting: up to about
# three and a half seconds of speech the transcript is *character for character identical* at half
# the context, at a third of the wall time. Past that, cutting too close starts costing words —
# a six-second clip lost accuracy at 1.5x and recovered it at 2x — and cutting far too close makes
# the decoder thrash, taking longer than the full window would have.
#
# So: twice what the audio needs, floored well clear of the thrashing, capped at the whole window.
# Twice rather than 1.5 because the margin is what stops a long utterance losing words, and the
# difference costs a tenth of a second on the short ones that dominate.
#
# The floor is 384 rather than 256 because 256 was measurably worse on hard audio — a clip whisper
# struggles with went from an error of 21 characters to 9 by giving it half a second more window,
# while a clip it handles easily barely noticed. Two tenths of a second is a cheap price for the
# bad case, and the bad case is the one somebody in a call actually hears.
CONTEXT_UNITS_PER_SECOND = 50
CONTEXT_MARGIN = 2.0
CONTEXT_FLOOR = 384
CONTEXT_FULL = 1500


def audio_context_for(seconds):
    """The encoder window one utterance needs, or None to use the whole thing."""
    if not seconds or seconds <= 0:
        return None

    wanted = int(seconds * CONTEXT_UNITS_PER_SECOND * CONTEXT_MARGIN)

    if wanted >= CONTEXT_FULL:
        # Long enough to need the whole window. Asking for it explicitly and asking for nothing
        # are the same thing to whisper, and nothing is the setting it documents.
        return None

    return max(CONTEXT_FLOOR, wanted)


def wav_seconds(audio_bytes):
    """How long a WAV is, read from its header. Zero when it cannot be read."""
    try:
        with wave.open(io.BytesIO(audio_bytes)) as handle:
            return handle.getnframes() / float(handle.getframerate() or 1)
    except Exception:
        # A malformed header is not a reason to fail a transcription: the full window still works,
        # it is only slower.
        return 0


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
      "{audio_ctx}", "--prompt", "{prompt}", "--output-txt", "--no-prints", "{gpu}"]),
    ("whisper.cpp",
     ["-m", "{model}", "-f", "{input}", "-l", "{language}", "-bs", "1", "-t", "{threads}",
      "{audio_ctx}", "--prompt", "{prompt}", "--output-txt", "{gpu}"]),
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


# ---- text to speech, which is the one thing here that uses the network ----
#
# Every local voice that could be had in European Portuguese was measured and none was good enough
# to be Aurora's: the one voice in Piper's catalogue is male and band-limited, and the alternatives
# were either licence-blocked, Brazilian, or too heavy to sit beside the language model. So this
# leg was given up deliberately, and the trade is named rather than hidden: the **text** Aurora is
# about to say leaves the machine. Audio never does — recognition stays local, and `readiness`
# reports both facts separately so neither can be mistaken for the other.

ELEVENLABS_HOST = "api.elevenlabs.io"

# Flash: the low-latency model, and the one that honours an explicit language code rather than
# guessing from the text. Guessing is the failure that matters here — "no" is a word in several
# languages and a wrong guess reads the whole sentence in the wrong one.
ELEVENLABS_MODEL = "eleven_flash_v2_5"

# 48kHz because that is what Discord's Opus encoder takes. Asking for anything else would buy a
# resampling step and the artefacts that come with it, for nothing.
ELEVENLABS_RATE = 48000

ELEVENLABS_TIMEOUT = 30.0


def resolve_voice(setting, language=None):
    """Which voice to speak with, from whatever shape the installer wrote.

    Whoever installs Aurora chooses the voice, and there are two reasonable things to want. One
    voice for everything gives Aurora a single recognisable identity — at the cost of carrying that
    speaker's accent into every other language she speaks. One voice per language gives a native
    accent everywhere, at the cost of Aurora not having a voice of her own.

    Neither is wrong, so this does not choose: the setting is either a string, used whatever the
    language, or a mapping from language to voice with an optional "default" for the rest.
    """
    if not setting:
        return None

    if isinstance(setting, str):
        return setting.strip() or None

    if not isinstance(setting, dict):
        return None

    # "pt-PT" should find a voice filed under "pt", and "pt" should not be found by "pt-BR".
    candidates = []

    if language:
        candidates.append(str(language))

        if "-" in str(language):
            candidates.append(str(language).split("-")[0])

    candidates.append("default")

    for key in candidates:
        found = setting.get(key)

        if isinstance(found, str) and found.strip():
            return found.strip()

    return None


def cloud_tts(voice_setting, api_key, language=None):
    """The speaking engine, or None with nothing said about why.

    Both halves have to be present: a key without a voice cannot speak and a voice without a key
    cannot either. `readiness` is what explains which is missing.
    """
    voice_id = resolve_voice(voice_setting, language)

    if not voice_id or not api_key:
        return None

    return {"name": "elevenlabs", "voice_id": voice_id, "model": ELEVENLABS_MODEL,
            "rate": ELEVENLABS_RATE, "language": language}


def _to_stereo(mono):
    """Duplicates each sample into both channels.

    ElevenLabs returns one channel and Discord carries two. Done here rather than later because
    the framing downstream counts bytes, and handing it mono would silently halve every frame.
    """
    out = bytearray(len(mono) * 2)
    out[0::4] = mono[0::2]
    out[1::4] = mono[1::2]
    out[2::4] = mono[0::2]
    out[3::4] = mono[1::2]
    return bytes(out)


ELEVENLABS_BASE = "https://" + ELEVENLABS_HOST


def _speech_base(base):
    """The service to talk to, refusing any address that would send the key in clear.

    The override exists so the protocol can be tested without a network, a key or somebody's quota.
    It is deliberately narrow: plain HTTP is allowed only to loopback, because an API key on the
    wire in clear is exactly the kind of mistake a test helper quietly turns into production.
    """
    if not base:
        return ELEVENLABS_BASE

    base = base.rstrip("/")

    if "://" not in base:
        base = "https://" + base

    scheme, _, rest = base.partition("://")
    host = rest.split("/")[0].split(":")[0]

    if scheme != "https" and host not in ("127.0.0.1", "localhost", "::1", "[::1]"):
        raise RuntimeError(
            "refusing to send the speech key to %s over %s — only https, or loopback for tests"
            % (host, scheme))

    return base


def synthesise_stream(engine, text, api_key, timeout=ELEVENLABS_TIMEOUT, base=None):
    """Yields 48kHz stereo PCM as it arrives, so speaking can start before the sentence is made.

    The generator is the cancellation mechanism. Closing it — which is what the transport does when
    somebody interrupts — stops reading and closes the connection, and nothing keeps working in the
    background afterwards.

    Raises RuntimeError on anything that means no audio, rather than yielding silence: a refusal
    that says the quota ran out is useful, and half a second of nothing is not.
    """
    import json
    import urllib.error
    import urllib.request

    if not text or not text.strip():
        raise RuntimeError("nothing to say")

    body = {"text": text, "model_id": engine.get("model") or ELEVENLABS_MODEL}

    # Sent only when known. An empty language code is not the same as an absent one: the API
    # rejects the former and infers for the latter.
    if engine.get("language"):
        body["language_code"] = str(engine["language"])

    url = "%s/v1/text-to-speech/%s/stream?output_format=pcm_%d" % (
        _speech_base(base), engine["voice_id"], engine.get("rate") or ELEVENLABS_RATE)

    request = urllib.request.Request(
        url, data=json.dumps(body).encode("utf-8"), method="POST",
        headers={"xi-api-key": api_key, "Content-Type": "application/json",
                 "Accept": "audio/pcm"})

    try:
        response = urllib.request.urlopen(request, timeout=timeout)
    except urllib.error.HTTPError as error:
        # The body carries the reason — a bad voice id, an exhausted quota — and it is short.
        # Worth reading, because "HTTP 401" alone sends somebody looking in the wrong place.
        detail = ""

        try:
            detail = error.read()[:400].decode("utf-8", "replace")
        except Exception:
            pass

        raise RuntimeError("the speech service refused (HTTP %d) %s"
                           % (error.code, detail.strip())) from error
    except Exception as error:
        raise RuntimeError("the speech service could not be reached: %s: %s"
                           % (type(error).__name__, error)) from error

    pendente = b""
    entregou = False

    # `read(n)` is the wrong call here and it took a test to notice: it blocks until it has all n
    # bytes or the response ends, so the first audio would wait for a full buffer to accumulate
    # instead of going out as it arrived — which is the whole point of streaming. `read1` returns
    # whatever one underlying read produced, however little.
    ler = getattr(response, "read1", None) or response.read

    try:
        while True:
            pedaco = ler(4096)

            if not pedaco:
                break

            pendente += pedaco

            # A 16-bit sample must not be split across a channel duplication, so an odd trailing
            # byte waits for its other half.
            inteiro = len(pendente) - (len(pendente) % 2)

            if inteiro:
                entregou = True
                yield _to_stereo(pendente[:inteiro])
                pendente = pendente[inteiro:]
    finally:
        response.close()

    if not entregou:
        raise RuntimeError("the speech service answered with no audio")


def readiness(voice=None, api_key=None, language=None):
    """What voice can and cannot do on this machine, as a plain answer.

    Reported rather than discovered at the moment of failure: somebody deciding whether to have
    Aurora join a call should be able to find out first, and an error in the middle of a
    conversation is a bad way to learn that a codec is missing.
    """
    import dave

    opus = find_opus()
    stt = find_stt()
    tts = cloud_tts(voice, api_key, language)
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
        # Which half is missing, because the two are fixed in different places by different
        # people: the voice is a setting somebody chooses, the key is a secret somebody types.
        if not api_key:
            missing.append(
                "the elevenlabs_api_key secret (set it with `secret set plugin/discord "
                "elevenlabs_api_key`; it is never passed on a command line)")
        if not resolve_voice(voice, language):
            missing.append(
                "a voice to speak with — put a voice id in the tts_voice setting, either one id "
                "for every language or a mapping of language to id")

    return {
        "can_join": bool(opus and transport),
        "can_listen": bool(opus and stt and transport),
        "can_speak": bool(opus and tts and transport),
        "transport": transport,
        # Which voice, not only that there is one. The setting may hold several and which one
        # answers depends on the language being spoken; "tts: elevenlabs" says nothing useful.
        "voice": tts["voice_id"] if tts else None,
        "language": language,
        "e2ee": e2ee,
        "opus": opus,
        "stt": stt["name"] if stt else None,
        "tts": tts["name"] if tts else None,
        "missing": missing,

        # Said explicitly, because these are the properties that would be quietly lost first, and
        # they are no longer the same answer. Recognition is still local: nobody's voice is
        # uploaded, and a recording exists only as bytes in memory until it becomes text. Speaking
        # is not: the sentence Aurora is about to say is sent to ElevenLabs to be read aloud.
        "audio_leaves_this_machine": False,
        "text_leaves_this_machine": bool(tts),
        "speech_service": ELEVENLABS_HOST if tts else None,
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
               language="auto", prompt="", threads=None, audio_ctx=True):
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

        # `-ac N` is two tokens or none, so it is expanded rather than substituted. `audio_ctx`
        # False turns the sizing off entirely and asks for the whole window, which is what a caller
        # comparing against the old behaviour wants.
        window = audio_context_for(wav_seconds(audio_bytes)) if audio_ctx else None
        expanded = []

        for argument in arguments:
            if argument != "{audio_ctx}":
                expanded.append(argument)
            elif window:
                expanded += ["-ac", str(window)]

        arguments = expanded

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


