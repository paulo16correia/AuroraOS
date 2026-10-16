"""Hearing and speaking, locally.

Two jobs, two contracts, and both are small on purpose. A recogniser turns audio into words; a
speaker turns words into audio. Neither decides anything — that is the whole reason they are
separate from the thinking layer and from Aurora.

    recogniser.transcribe(pcm16) -> {"text", "confidence", "seconds"}
    speaker.speak(text)          -> pcm16 bytes

Each has two implementations: the one this task asked for, and the one already installed on this
machine from the Discord voice work. They sit behind the same contract so the choice is
configuration rather than an edit — and so that a machine with neither says so instead of failing
somewhere further in.

**Audio is 24 kHz mono signed 16-bit little-endian throughout.** One format, chosen because it is
what the whole chain already speaks; converting in three places is how a rate error becomes a
transcript in the wrong language.
"""

import io
import json
import os
import re
import secrets
import shutil
import struct
import subprocess
import tempfile
import time
import unicodedata
import wave

SAMPLE_RATE = 24000
SAMPLE_WIDTH = 2
CHANNELS = 1

# What whisper wants, which is not what the rest of the chain uses. Named rather than inlined
# because getting it wrong does not fail — it transcribes, confidently, into words nobody said.
WHISPER_RATE = 16000


class SpeechUnavailable(Exception):
    """No engine on this machine can do this. Said plainly rather than failing obscurely."""


def wav(pcm16, rate=SAMPLE_RATE, channels=CHANNELS):
    """A RIFF header around raw samples, because every local engine reads files."""
    block = channels * SAMPLE_WIDTH

    return (
        b"RIFF" + struct.pack("<I", 36 + len(pcm16)) + b"WAVEfmt "
        + struct.pack("<IHHIIHH", 16, 1, channels, rate, rate * block, block, SAMPLE_WIDTH * 8)
        + b"data" + struct.pack("<I", len(pcm16)) + pcm16)


def resample_to(pcm16, source_rate, target_rate):
    """Rate conversion with a box filter, not decimation.

    Dropping samples is not resampling: the source carries content above the new Nyquist limit and
    throwing samples away folds it back into the speech band as noise. Whisper answers that with
    repetition loops and by detecting the wrong language, which reads as a broken model rather than
    a wrong number. Averaging each group first is crude and removes it.
    """
    if source_rate == target_rate:
        return pcm16

    step = source_rate / target_rate
    samples = len(pcm16) // SAMPLE_WIDTH
    out = bytearray()
    position = 0.0

    while True:
        start = int(position)
        end = int(position + step)

        if end > samples:
            break

        total = 0

        for index in range(start, end):
            total += struct.unpack_from("<h", pcm16, index * SAMPLE_WIDTH)[0]

        out += struct.pack("<h", max(-32768, min(32767, total // max(1, end - start))))
        position += step

    return bytes(out)


def duration_ms(pcm16, rate=SAMPLE_RATE):
    """How long a slice of audio lasts.

    Turn detection measures this rather than the clock. Audio arriving in a burst after a stall is
    still the same number of milliseconds of somebody talking, and a caller does not become silent
    because the network paused.
    """
    return len(pcm16) * 1000.0 / (rate * SAMPLE_WIDTH)


def energy(pcm16):
    """Mean absolute amplitude. How loud a slice is, for deciding whether anybody is talking."""
    samples = len(pcm16) // SAMPLE_WIDTH

    if samples == 0:
        return 0.0

    total = 0

    for index in range(samples):
        total += abs(struct.unpack_from("<h", pcm16, index * SAMPLE_WIDTH)[0])

    return total / samples


# ---------------------------------------------------------------------------
# hearing
# ---------------------------------------------------------------------------
#
# All of this came from the Discord plugin, which had it first and had it better: it is the one
# that has been run against real models, on this machine, inside the sandbox. It lives here now
# because there is one voice in Aurora, and a plugin that listens should not have to rediscover
# that --no-gpu is the difference between a refusal and a segmentation fault.
#
# Where a plugin keeps its models and its bundled programs is the one thing that cannot be shared,
# because each plugin is granted its own directory and nothing else. So it is a variable, and a
# plugin that vendors this file sets it to its own directory.

PLUGIN_HOME = os.path.dirname(os.path.abspath(__file__))


# ---- how hard to work at it ----
#
# whisper.cpp defaults to four threads, which on a six-core processor leaves most of it idle while
# somebody waits to be answered — 28% of the time on a four-second utterance, measured, for a
# number it already knew. Half the logical processors is the physical core count on anything with
# SMT, and using every one measured *slower* than eight: recognition is not the only thread in a
# plugin, and the audio it is transcribing keeps arriving while it runs.
def default_threads():
    return max(1, (os.cpu_count() or 4) // 2)


# ---- how much of the encoder's window an utterance needs ----
#
# Whisper's encoder always walks a thirty-second window — 1500 context units, fifty per second —
# however little was actually said. In a conversation that is almost all padding: a second and a
# half of "Aurora, estas a ouvir-me bem?" pays the same two seconds of encoding a full paragraph
# would, and encoding was 73% of the time recognition took.
#
# `--audio-ctx` shortens the window. Measured, three runs per setting: up to about three and a half
# seconds of speech the transcript is *character for character identical* at half the context, at a
# third of the wall time. Past that, cutting too close starts costing words — a six-second clip lost
# accuracy at 1.5x and recovered it at 2x — and cutting far too close makes the decoder thrash,
# taking longer than the full window would have.
#
# So: twice what the audio needs, floored well clear of the thrashing, capped at the whole window.
# Twice rather than 1.5 because the margin is what stops a long utterance losing words, and the
# difference costs a tenth of a second on the short ones that dominate.
#
# The floor is 384 rather than 256 because 256 was measurably worse on hard audio — a clip whisper
# struggles with went from an error of 21 characters to 9 by giving it half a second more window,
# while a clip it handles easily barely noticed. Two tenths of a second is a cheap price for the bad
# case, and the bad case is the one somebody in a conversation actually hears.
CONTEXT_UNITS_PER_SECOND = 50
CONTEXT_MARGIN = 2.0
CONTEXT_FLOOR = 384
CONTEXT_FULL = 1500

# Past this, shortening the window stops paying and starts costing.
#
# docs/adr/0085 measured the gain on clips of up to about three and a half seconds, where it was
# large: the transcript came back character for character identical at half the context and a third
# of the time. Turns used to be whatever length somebody spoke for, so most of them were short.
# They are capped at eight seconds now, and at that length the gain is gone — measured here, an
# eight-second turn takes 3.2s at a window of 800 and 3.1s at the full 1500. For a bigger model it
# is worse than gone: large-v3-turbo took 21.9s at 800 and 12.1s at the full window, which is the
# decoder thrashing exactly as 0085 warned it would when the window is cut too close.
#
# So the sizing still applies where it was measured, and stops where the measurement stops.
CONTEXT_WORTH_SHORTENING_SECONDS = 4.0


def audio_context_for(seconds):
    """The encoder window one utterance needs, or None to use the whole thing.

    Off by default, and the default is the measurement rather than the intention. whisper encodes a
    fixed thirty-second window whatever it is given, so shortening it for a short utterance looks
    like free speed. It is not free: measured against sixteen known Portuguese sentences through the
    production audio path, shortening the window cost seven points of word error clean (33.7% to
    40.8%) and nine with noise, for about a second a turn. A turn Aurora mishears is a turn she
    answers wrongly, which costs the exchange rather than a second of it.

    Kept, rather than deleted, because the trade is real on slower hardware than this — an owner
    whose machine takes ten seconds a turn may want it. It is `stt_audio_ctx` and it is off.
    """
    if not seconds or seconds <= 0:
        return None

    if seconds > CONTEXT_WORTH_SHORTENING_SECONDS:
        # Long enough that the whole window is the cheaper answer as well as the better one.
        return None

    wanted = int(seconds * CONTEXT_UNITS_PER_SECOND * CONTEXT_MARGIN)

    if wanted >= CONTEXT_FULL:
        # Asking for the whole window explicitly and asking for nothing are the same thing to
        # whisper, and nothing is the setting it documents.
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


# ---- which program, and which model ----
#
# Each is a program the owner installed, in the order they are preferred.
STT_ENGINES = [
    # --no-gpu is not a performance choice. whisper.cpp loads its GPU backend by default, the
    # sandbox denies a plugin the graphics processor, and the result is a segmentation fault rather
    # than a refusal — exit -11 with a log line about loading Metal and nothing else. The GPU is
    # used when Aurora granted it and refused when she did not: a large model takes about eight
    # seconds on the graphics processor and seventy on the processor alone, and seventy seconds to
    # hear one sentence is not listening.
    #
    # -l auto, because whisper.cpp defaults to English and transcribes everything else as
    # "(speaking in foreign language)" — which is not a failure it reports, it is the transcript. A
    # system that only understands its owner in one language is not one to ship by default.
    #
    # -bs 5 is a beam search, and this used to say 1 with "a beam search costs ~15% more time and
    # changed no word of a short reply" beside it. Measured again on a turn the length turns actually
    # are now — eight seconds of Discord audio rather than a short clean clip — a beam of five took
    # 2.4s against 3.2s greedy. Not slower, and greedy decoding is what produces the confident
    # nonsense this is meant to avoid: a word in the wrong alphabet, a place name turned into
    # something that sounds like it. Where a beam costs nothing, taking the more careful search is
    # free accuracy.
    #
    # --prompt is the surprising one. It is context, not a command, and seeding it with Aurora's own
    # name is the difference between a small model hearing "Aurora" and hearing "A hora" — measured,
    # with the same clip. A name is the word recognition gets wrong most, and telling the recogniser
    # the word exists costs nothing (docs/adr/0071).
    # -nf turns off temperature fallback. When a segment fails whisper's own confidence checks it
    # re-decodes at rising temperature, and a decoder sampling at temperature one on audio it could
    # not read does not produce a worse transcript — it produces a confident invention, in whatever
    # language the sampled tokens happened to spell. That is where "советар" came from. Measured
    # against known speech it changed no word of the transcripts that were already right, and it
    # removes the re-decodes, which is most of the time this used to spend.
    #
    # -sns suppresses the non-speech tokens, the ones that produce "[MÚSICA]" and "(música)" from a
    # fan or a breath. They are not transcripts and nothing downstream should have to recognise
    # them as noise.
    ("whisper-cli",
     ["-m", "{model}", "-f", "{input}", "-l", "{language}", "-bs", "5", "-t", "{threads}",
      "-nf", "-sns", "{audio_ctx}", "--prompt", "{prompt}", "--output-txt", "--no-prints",
      "{gpu}"]),
    ("whisper.cpp",
     ["-m", "{model}", "-f", "{input}", "-l", "{language}", "-bs", "5", "-t", "{threads}",
      "-nf", "-sns", "{audio_ctx}", "--prompt", "{prompt}", "--output-txt", "{gpu}"]),
    ("whisper", ["--model", "base", "--output_format", "txt", "{input}"]),
]

# Multilingual first. An English-only model transcribes Portuguese into confident nonsense rather
# than failing, which is the worse of the two.
MODEL_NAMES = [
    "ggml-large-v3-turbo.bin", "ggml-medium.bin", "ggml-small.bin", "ggml-base.bin",
    "ggml-base.en.bin", "ggml-tiny.bin",
]


def model_search(home=None):
    """Where a whisper model is likely to be, the plugin's own directory first.

    The model is a separate download from the program, and the program's default path is relative
    to wherever it was built — which is never where a package manager put it. A recogniser with no
    model fails on every utterance and says only that it exited non-zero.

    The plugin's own `models/` comes first for the same reason its libraries do: the sandbox lets a
    plugin read its own directory and nothing else of the owner's. A model in a home cache is one
    the plugin cannot open — correctly. What a plugin needs to run ships with it.
    """
    return [
        os.path.join(home or PLUGIN_HOME, "models"),
        os.path.expanduser("~/.cache/whisper"),
        os.path.expanduser("~/Library/Application Support/Aurora/models"),
        "/opt/homebrew/share/whisper.cpp/models",
        "/usr/local/share/whisper.cpp/models",
    ]


def find_model(preferred=None, home=None):
    """A whisper model file, or None. Separate from the program and separately absent.

    The owner may name one. The trade is speed against a proper noun: measured, the large model
    takes about seven seconds for a four-second sentence and the base model four tenths of one —
    and the base model mishears the name, unless it is prompted with it (docs/adr/0071).
    """
    for directory in model_search(home):
        for name in ([preferred] if preferred else []) + MODEL_NAMES:
            candidate = os.path.join(directory, name)

            if os.path.exists(candidate):
                return candidate

    return None


def engine_dirs(home=None):
    """The plugin's own directories, which the sandbox grants it — where a bundled engine lives.

    A confined plugin's PATH is the system directories alone, so an engine installed elsewhere is
    unreachable to it. Its own directory is not: the sandbox lets it read and execute what ships
    beside it, which is the same reason the model is looked for there first.
    """
    here = home or PLUGIN_HOME
    return [os.path.join(here, "bin"), here]


def find_program(name, dirs=None):
    """An engine executable, preferring one shipped with the plugin over one on PATH.

    Bundled first so a confined plugin can use engines that ship with it; PATH second so an
    unconfined install keeps working exactly as before.
    """
    extensions = ["", ".exe", ".bat", ".cmd"] if os.name == "nt" else [""]

    for directory in (engine_dirs() if dirs is None else dirs):
        for extension in extensions:
            candidate = os.path.join(directory, name + extension)

            if os.path.isfile(candidate):
                return candidate

    return shutil.which(name)


def find_stt(preferred_model=None, home=None, dirs=None):
    """A local speech-to-text program with a model to run, or None.

    Both, because either alone recognises nothing. whisper.cpp with no model exits non-zero on
    every utterance, which reads as speech that could not be understood rather than as a file that
    was never downloaded.
    """
    for name, arguments in STT_ENGINES:
        found = find_program(name, dirs)

        if not found:
            continue

        model = find_model(preferred_model, home)

        if "{model}" in " ".join(arguments) and model is None:
            return None

        return {"name": name, "path": found, "arguments": arguments, "model": model}

    return None


def workspace():
    """A directory for one utterance's scratch files, that a plugin can actually write in.

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


# ---- not believing a recogniser that heard nothing ----
#
# What whisper says when there is nothing to hear. It does not return empty on silence — it returns
# the most common thing in its training data, which for hours of subtitled video is gratitude and
# sign-offs. Answering these is answering nobody.
HALLUCINATIONS = {
    "thank you.", "thanks for watching!", "thank you for watching!", "you", ".", "bye.",
    "thanks for watching.", "please subscribe.", "[blank_audio]", "(silence)", "so",
    "obrigado.", "obrigada.", "ate a proxima.", "tchau.", "muito obrigado.",
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


# How much of a transcript may come from the prompt before it stops being a transcript.
#
# The prompt is handed to whisper as text that came immediately before the audio, so when the audio
# is unclear the likeliest continuation is more of the prompt — and what comes back is the prompt
# itself, spelled as though somebody had said it. Measured against known speech: with a prompt
# reading "Uma conversa com Aurora. Falamos de Sobral da Abelheira, Ericeira.", the sentence
# "Aurora, estás a ouvir?" came back as "Falamos de Sobral da Abelheira." Answering that is
# answering a question nobody asked, which is worse than not hearing one.
ECHO_FOREIGN_WORDS = 1

# Below this many words, a transcript made of prompt words is likelier to be true than echoed. The
# prompt holds the words that actually get said — a name, a town — and somebody saying only
# "Aurora", or only "Sobral da Abelheira", still has to be heard.
ECHO_SHORTEST = 4


def is_prompt_echo(text, prompt):
    """Whether this transcript is the prompt coming back rather than anything that was said.

    True when the transcript is long enough to be a sentence and all but one of its words came from
    the prompt. The threshold counts the words the prompt does *not* contain, rather than a
    percentage, because what makes a transcript real is that it says something the prompt could not
    have supplied — and one such word is enough to believe it.
    """
    if not prompt:
        return False

    said = _echo_words(text)

    if len(said) < ECHO_SHORTEST:
        return False

    from_prompt = set(_echo_words(prompt))

    return sum(1 for word in said if word not in from_prompt) <= ECHO_FOREIGN_WORDS


# How much of a transcript may be written in an alphabet the conversation does not use.
#
# A third, because a stray character is not evidence of anything — a recogniser that emits one
# Greek letter in a Portuguese sentence has made a spelling mistake, not changed language — while a
# transcript a third of whose letters are from another script is not that sentence at all.
FOREIGN_SCRIPT_SHARE = 0.34


def is_foreign_script(text, prompt):
    """Whether this transcript is written in an alphabet this conversation does not use.

    The alphabet is taken from the prompt rather than from a table of languages, because the prompt
    is already written in the owner's language and a table would be one more thing to keep true.

    This is the second line and not the first. What produced "советар" in a Portuguese channel was
    whisper's temperature fallback, re-decoding audio it could not read until it sampled something
    — and that is off (`-nf` in `STT_ENGINES`). This catches what a decoder spells in another
    alphabet anyway, which is rare and is never speech.
    """
    ours = _scripts(prompt)

    if not ours:
        return False

    letters = [letter for letter in unicodedata.normalize("NFC", text or "") if letter.isalpha()]

    if not letters:
        return False

    foreign = sum(1 for letter in letters if _script_of(letter) not in ours)

    return foreign / float(len(letters)) >= FOREIGN_SCRIPT_SHARE


def _scripts(text):
    """The alphabets a piece of text is written in."""
    return {_script_of(letter)
            for letter in unicodedata.normalize("NFC", text or "")
            if letter.isalpha()}


def _script_of(letter):
    """The alphabet one letter belongs to: LATIN, CYRILLIC, GREEK, and so on.

    Taken from the character's own name, which is where Unicode puts it, rather than from a range
    table that would have to be maintained.
    """
    return unicodedata.name(letter, "UNKNOWN").split(" ")[0]


def _echo_words(text):
    """Lowercase words, accents kept: in Portuguese they are part of the word."""
    normalised = unicodedata.normalize("NFC", (text or "").lower())
    return re.findall(r"[^\W\d_]+", normalised, re.UNICODE)


def transcribe(engine, audio_bytes, model=None, timeout=180, gpu=True,
               language="auto", prompt="", threads=None, audio_ctx=False):
    """Turns speech into text, locally, and keeps neither the audio nor the file.

    The audio touches the disk because these programs read files, and it is removed in the same
    call that wrote it. Keeping recordings would mean Aurora holding a transcript of a private
    conversation nobody agreed to her holding.
    """
    if engine is None:
        raise RuntimeError("no local speech-to-text program is installed")

    directory = workspace()
    source = os.path.join(directory, "utterance.wav")

    try:
        with open(source, "wb") as handle:
            handle.write(audio_bytes)

        arguments = [
            argument.replace("{input}", source)
                    .replace("{model}", model or engine.get("model") or "")
                    .replace("{language}", language or "auto")
                    .replace("{prompt}", prompt or "")
                    .replace("{threads}", str(threads or default_threads()))
            for argument in engine["arguments"]
        ]

        # Without the grant the GPU is not merely slow, it is a segmentation fault: whisper.cpp
        # loads its GPU backend by default and the sandbox refuses it. --no-gpu is how the refusal
        # becomes a fallback instead of a crash.
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
            # UTF-8 named: a transcript is speech, so it is exactly the file most likely to hold a
            # character Python on Windows would otherwise fail to decode.
            with open(transcript, "r", encoding="utf-8") as handle:
                return handle.read().strip()

        return finished.stdout.decode(errors="replace").strip()
    finally:
        # Always, including when the engine failed. A crash is not a reason to leave somebody's
        # voice on the disk.
        shutil.rmtree(directory, ignore_errors=True)


class WhisperCppRecogniser:
    """whisper.cpp through its command-line program, for a caller that wants an object.

    The work is in the functions above, which the Discord plugin calls directly because it holds
    its audio differently. This is the same recognition with the conversation layer's shape around
    it: PCM in at the rate the rest of the chain uses, and a dict out.
    """

    name = "whisper.cpp"

    def __init__(self, model=None, language="auto", vocabulary=None, home=None):
        self.language = language
        self.home = home
        self.engine = find_stt(model, home)
        self.model = model or (self.engine or {}).get("model")

        # Told to the recogniser as context. A name is the word recognition gets wrong most, and
        # saying it exists costs nothing (docs/adr/0071).
        self.vocabulary = vocabulary or ["Aurora"]

    @classmethod
    def available(cls, home=None):
        return find_stt(home=home) is not None

    def transcribe(self, pcm16):
        if self.engine is None:
            raise SpeechUnavailable("whisper.cpp or its model is not on this machine")

        seconds = round(len(pcm16) / (SAMPLE_RATE * SAMPLE_WIDTH), 2)

        # Not asked at all rather than asked and disbelieved: a recogniser handed a quiet room
        # answers with whatever its training data had most of.
        if is_silence(pcm16):
            return {"text": "", "confidence": None, "seconds": seconds, "engine": self.name}

        text = transcribe(
            self.engine, wav(resample_to(pcm16, SAMPLE_RATE, WHISPER_RATE), WHISPER_RATE),
            model=self.model, language=self.language, prompt=", ".join(self.vocabulary))

        return {
            "text": "" if is_hallucination(text) else text,
            # This engine reports none, and inventing one would be worse than saying so.
            "confidence": None,
            "seconds": seconds,
            "engine": self.name,
        }


# ---------------------------------------------------------------------------
# speaking
# ---------------------------------------------------------------------------


def _form(fields, file_field, filename, content, content_type):
    """A multipart/form-data body, built by hand, and the boundary to declare with it.

    By hand because the standard library has no encoder for it and this file takes no dependencies:
    whatever reaches a speech service from here is built in code somebody can read.
    """
    boundary = "----aurora" + secrets.token_hex(16)
    body = bytearray()

    for name, value in fields.items():
        # A list becomes one part per item under the same name, which is how multipart says "more
        # than one of these" and how this service wants its keyterms. Sending the list as a single
        # JSON string instead came back HTTP 400, "All keywords must be less than 50 characters":
        # it had read the whole array as one very long keyword.
        for one in (value if isinstance(value, (list, tuple)) else [value]):
            body += ('--%s\r\nContent-Disposition: form-data; name="%s"\r\n\r\n%s\r\n'
                     % (boundary, name, one)).encode("utf-8")

    body += ('--%s\r\nContent-Disposition: form-data; name="%s"; filename="%s"\r\n'
             'Content-Type: %s\r\n\r\n'
             % (boundary, file_field, filename, content_type)).encode("utf-8")
    body += content
    body += ('\r\n--%s--\r\n' % boundary).encode("utf-8")

    return bytes(body), "multipart/form-data; boundary=" + boundary


class ElevenLabsListener:
    """Recognition that is not local, and the reason this file no longer promises that it is.

    **A recording of somebody's voice leaves the machine when this is the recogniser.** That is the
    whole cost and it is not hidden: `readiness` reports `audio_leaves_this_machine` from which
    listener is in use, the secret's stated purpose says it, and the owner has to choose this
    explicitly because the default is the local one.

    Why it exists anyway. Measured against sixteen known European Portuguese sentences through the
    production audio path (`docs/adr/0091`), whisper-small gets a third of the words wrong and
    `large-v3-turbo` — which needs the graphics processor to run in a conversation at all — gets a
    quarter. ElevenLabs publishes 3.1% on FLEURS Portuguese and 5.5% on Common Voice. Five times
    fewer errors is not a tuning difference; it is the difference between Aurora answering the
    question and Aurora answering a question nobody asked.

    The audio never touches the disk here. It arrives as bytes, becomes a request body, and is
    gone — the local path writes a file because the programs it runs read files, and this one has
    no such excuse.
    """

    name = "elevenlabs"

    # The same host the speaker uses. One host, so there is one thing to approve and one place
    # audio can go, rather than two.
    HOST = "api.elevenlabs.io"
    BASE = "https://" + HOST

    # Scribe v2. The batch model rather than the realtime one: this is called once per finished
    # turn, so there is no partial transcript to stream and nothing to gain from holding a socket
    # open between utterances.
    MODEL = "scribe_v2"

    # Longer than the speaker's thirty. A refusal because the network was slow is a turn lost, and
    # this is the leg where the whole recording has to be uploaded before anything comes back.
    TIMEOUT = 60.0

    def __init__(self, api_key=None, locale="", base=None):
        self.api_key = (api_key or "").strip()
        self.locale = locale
        self.base = base

    def available(self):
        return bool(self.api_key)

    def missing(self):
        """What is absent, named where the person has to go and fix it."""
        if self.api_key:
            return []

        return ["the elevenlabs_api_key secret (set it with `secret set plugin/discord "
                "elevenlabs_api_key`; it is never passed on a command line)"]

    def listen(self, audio_bytes):
        """Turns speech into text, over the network. Returns the words, or raises.

        Raises rather than returning an empty string. Silence and "the quota ran out" need
        completely different fixes, and a caller handed "" cannot tell them apart — the local path
        learned that the same way.
        """
        import urllib.error
        import urllib.request

        if not self.available():
            raise SpeechUnavailable("; ".join(self.missing()))

        if not audio_bytes:
            raise SpeechUnavailable("nothing was heard")

        # The model and the language, and nothing else. Biasing the recogniser towards a list of
        # expected words is a real feature of this service and it is deliberately not used here:
        # it was written to paper over transcripts that were bad because the *audio* was bad, and
        # once that was fixed the honest thing was to find out what the recogniser does on its own.
        # It costs a fifth more per request, and it is one line to add back with a measurement.
        fields = {"model_id": self.MODEL}

        # The language, not the variety, and only when known — same rule as the speaker, for the
        # same reason. The service infers when it is absent and refuses an empty one, and inferring
        # is the failure that matters: a wrong guess reads a whole turn as another language.
        if self.locale:
            fields["language_code"] = language_of(self.locale)

        body, content_type = _form(
            fields, "file", "utterance.wav", audio_bytes, "audio/wav")

        request = urllib.request.Request(
            "%s/v1/speech-to-text" % _speech_base(self.base),
            data=body, method="POST",
            headers={"xi-api-key": self.api_key, "Content-Type": content_type,
                     "Accept": "application/json"})

        try:
            with urllib.request.urlopen(request, timeout=self.TIMEOUT) as response:
                heard = json.loads(response.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as error:
            detail = ""

            try:
                detail = error.read()[:400].decode("utf-8", "replace")
            except Exception:
                pass

            raise SpeechUnavailable("the recogniser refused (HTTP %d) %s"
                                    % (error.code, detail.strip())) from error
        except Exception as error:
            raise SpeechUnavailable("the recogniser could not be reached: %s: %s"
                                    % (type(error).__name__, error)) from error

        return str(heard.get("text") or "").strip()


class ElevenLabsSpeaker:
    """The voice Aurora speaks with, and the one thing in this file that uses the network.

    Every local voice that could be had was measured and none was good enough: the single European
    Portuguese voice in Piper's catalogue is male and band-limited, and the alternatives were
    licence-blocked, Brazilian, or too heavy to sit on the same card as the language model. So this
    leg was given up deliberately, and the cost is stated rather than hidden — **the sentence
    Aurora is about to say leaves the machine.** Audio never does; recognition is local and stays
    local.

    Two ways to ask for the same thing. `speak` returns the whole sentence, for callers that need
    it in one piece. `stream` yields it as it arrives, for callers that can start playing before it
    is finished — which is the difference between answering in a quarter of a second and answering
    in two.
    """

    name = "elevenlabs"

    HOST = "api.elevenlabs.io"
    BASE = "https://" + HOST

    # Flash: the low-latency model, and the one that honours an explicit language code instead of
    # guessing from the text. Guessing is the failure that matters — "no" is a word in several
    # languages, and a wrong guess reads the whole sentence in the wrong one.
    MODEL = "eleven_flash_v2_5"

    TIMEOUT = 30.0

    def __init__(self, voice=None, api_key=None, locale="en", rate=SAMPLE_RATE, base=None):
        self.voice_id = resolve_voice(voice, locale)
        self.api_key = (api_key or "").strip()
        self.locale = locale
        self.rate = rate
        self.base = base

    def available(self):
        """Both halves, because one without the other cannot say anything."""
        return bool(self.voice_id and self.api_key)

    def missing(self):
        """Which half is absent, named so the right person looks in the right place.

        They are fixed differently: the voice is a setting somebody edits, the key is a secret
        somebody types into a prompt. "Voice unavailable" would send one of them hunting in the
        wrong file.
        """
        absent = []

        if not self.api_key:
            absent.append(
                "the elevenlabs_api_key secret (set it with `secret set plugin/voice "
                "elevenlabs_api_key`; it is never passed on a command line)")
        if not self.voice_id:
            absent.append(
                "a voice to speak with — put a voice id in the tts_voice setting, either one id "
                "for every language or a mapping of language to id")

        return absent

    def speak(self, text):
        return b"".join(self.stream(text))

    def stream(self, text):
        """Yields PCM as it arrives: signed 16-bit little-endian mono, at `self.rate`.

        The generator is the cancellation mechanism. Closing it — which is what an interrupted
        sentence does — stops reading and closes the connection, and nothing goes on working in the
        background afterwards.

        Raises rather than yielding silence. A refusal that says the quota ran out is useful; half
        a second of nothing is indistinguishable from a quiet room.
        """
        import urllib.error
        import urllib.request

        if not self.available():
            raise SpeechUnavailable("; ".join(self.missing()))

        if not text or not text.strip():
            raise SpeechUnavailable("nothing to say")

        body = {"text": text, "model_id": self.MODEL}

        # Sent only when known: an empty language code is not the same as an absent one. The API
        # rejects the first and infers for the second.
        #
        # The language, not the variety. Measured against the real service: a request carrying
        # `pt-PT` comes back 400, "Model 'eleven_flash_v2_5' does not support language_code
        # 'pt-PT'". This model takes the primary subtag and nothing else, so the region is dropped
        # here rather than at the caller — an owner writing `pt-PT` in their settings is saying
        # something true about which Portuguese they speak, and the voice setting still honours it
        # when choosing who speaks. What is lost is only a distinction the service could not have
        # acted on.
        if self.locale:
            body["language_code"] = language_of(self.locale)

        url = "%s/v1/text-to-speech/%s/stream?output_format=pcm_%d" % (
            _speech_base(self.base), self.voice_id, self.rate)

        request = urllib.request.Request(
            url, data=json.dumps(body).encode("utf-8"), method="POST",
            headers={"xi-api-key": self.api_key, "Content-Type": "application/json",
                     "Accept": "audio/pcm"})

        try:
            response = urllib.request.urlopen(request, timeout=self.TIMEOUT)
        except urllib.error.HTTPError as error:
            # The body carries the reason — a bad voice id, an exhausted quota — and it is short.
            # Worth reading: "HTTP 401" alone sends somebody looking in the wrong place.
            detail = ""

            try:
                detail = error.read()[:400].decode("utf-8", "replace")
            except Exception:
                pass

            raise SpeechUnavailable("the speech service refused (HTTP %d) %s"
                                    % (error.code, detail.strip())) from error
        except Exception as error:
            raise SpeechUnavailable("the speech service could not be reached: %s: %s"
                                    % (type(error).__name__, error)) from error

        # `read(n)` is the wrong call and it took a test to notice: it blocks until it has all n
        # bytes or the response ends, so the first audio would wait for a whole buffer to
        # accumulate instead of going out as it arrived. `read1` returns whatever one underlying
        # read produced, however little.
        read = getattr(response, "read1", None) or response.read
        delivered = False

        try:
            while True:
                piece = read(4096)

                if not piece:
                    break

                delivered = True
                yield piece
        finally:
            response.close()

        if not delivered:
            raise SpeechUnavailable("the speech service answered with no audio")


def language_of(locale):
    """The primary subtag of a locale: `pt-PT` becomes `pt`, `en` stays `en`.

    Speech services name languages and owners name varieties. Keeping the two apart here means a
    setting can say `pt-PT` — which is true, and which `resolve_voice` uses to pick a voice — without
    the request carrying a code the model will refuse.
    """
    return str(locale or "").strip().replace("_", "-").split("-")[0].lower()


def resolve_voice(setting, language=None):
    """Which voice to speak with, from whatever shape the installer wrote.

    Whoever installs Aurora chooses, and there are two reasonable things to want. One voice for
    everything gives Aurora a single recognisable identity, at the cost of carrying that speaker's
    accent into every other language. One voice per language gives a native accent everywhere, at
    the cost of Aurora not having a voice of her own. Neither is wrong, so this does not choose:
    the setting is a string, or a mapping from language to voice with an optional "default".
    """
    if not setting:
        return None

    if isinstance(setting, str):
        return setting.strip() or None

    if not isinstance(setting, dict):
        return None

    # "pt-PT" should find a voice filed under "pt"; "pt" should not find one filed under "pt-PT".
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


def _speech_base(base):
    """The service to talk to, refusing any address that would send the key in clear.

    The override exists so the protocol can be tested without a network, a key or somebody's quota.
    It is deliberately narrow: plain HTTP only to loopback, because an API key on the wire in clear
    is exactly the mistake a test helper quietly turns into production.
    """
    if not base:
        return ElevenLabsSpeaker.BASE

    base = base.rstrip("/")

    if "://" not in base:
        base = "https://" + base

    scheme, _, rest = base.partition("://")
    host = rest.split("/")[0].split(":")[0]

    if scheme != "https" and host not in ("127.0.0.1", "localhost", "::1", "[::1]"):
        raise SpeechUnavailable(
            "refusing to send the speech key to %s over %s — only https, or loopback for tests"
            % (host, scheme))

    return base


def _pcm_from_wav(raw):
    """The samples out of a RIFF file, whatever chunks precede them."""
    if len(raw) < 12 or raw[:4] != b"RIFF":
        raise SpeechUnavailable("that is not a WAV file")

    position = 12

    while position + 8 <= len(raw):
        name = raw[position:position + 4]
        size = struct.unpack_from("<I", raw, position + 4)[0]
        body = position + 8

        if name == b"data":
            return raw[body:body + size]

        position = body + size + (size % 2)

    raise SpeechUnavailable("the WAV file has no audio in it")


class ScriptedRecogniser:
    """A recogniser that returns what a test said it would hear.

    Chosen only by naming it — `engine: "scripted"` — which no shipped installation does, the same
    way the interaction layer's stand-in is chosen. It exists because the end-to-end path is worth
    proving on a machine with no models on it, and because "what happens when somebody says this
    exact sentence" cannot be asked of a real recogniser.
    """

    name = "scripted"

    def __init__(self, transcripts, delay_ms=0):
        self._transcripts = list(transcripts)

        # A recogniser that takes a known amount of time. Real ones take seconds and no two runs
        # agree; a test about what happens *while* recognition is running needs a number it chose.
        self._delay_ms = int(delay_ms or 0)

    def transcribe(self, pcm16):
        if self._delay_ms:
            time.sleep(self._delay_ms / 1000.0)

        text = self._transcripts.pop(0) if self._transcripts else ""

        return {
            "text": text,
            "confidence": 1.0,
            "seconds": round(duration_ms(pcm16) / 1000.0, 2),
            "engine": self.name,
        }


class ScriptedSpeaker:
    """A synthesiser that produces the right amount of silence. Chosen the same way.

    It answers `available` and `missing` because the real speaker does, and a double that does not
    implement the interface is not a double — it is a different object that happens to work until
    somebody calls the method it never had. That is exactly how this broke: `status` asked whether
    the speaker was available and got an AttributeError the plugin reported as "failed
    unexpectedly", which is true and useless.
    """

    name = "scripted"

    def __init__(self, delay_ms=0):
        self._delay_ms = int(delay_ms or 0)

    @staticmethod
    def available():
        return True

    @staticmethod
    def missing():
        return []

    def stream(self, text):
        """The same two ways to ask as the real one, so a caller cannot tell them apart."""
        yield self.speak(text)

    def speak(self, text):
        if self._delay_ms:
            time.sleep(self._delay_ms / 1000.0)

        # A tenth of a second per word, which is roughly speech and is entirely arbitrary. What
        # matters downstream is that audio of a plausible length arrives.
        samples = int(SAMPLE_RATE * 0.1 * max(1, len(text.split())))

        return b"\x00\x00" * samples


def recogniser(settings):
    """Whisper, or the scripted double. There is deliberately nothing to choose between.

    This used to pick the best of several engines. It does not any more, and the reason is worth
    keeping: every engine in a list is one somebody has to install, keep working, and reason about
    when a transcript comes back wrong. One recogniser means one set of failures, and whisper is
    the one that was measured here.
    """
    wanted = (settings or {}).get("engine")

    if wanted == "scripted":
        return ScriptedRecogniser(
            (settings or {}).get("transcripts") or [], (settings or {}).get("delay_ms"))

    if wanted in (None, "whisper", "whisper.cpp") and WhisperCppRecogniser.available():
        # The language, not the variety. `language_of` is here because the speech service refuses a
        # tag; whisper refuses one too, and more quietly — `-l pt-PT` makes it print its usage and
        # transcribe nothing, which reads as a dead microphone rather than as a wrong setting. The
        # owner who had set their locale correctly would have been the one to hit it.
        #
        # `auto` has no hyphen, so it passes through as itself, which is what it should do.
        return WhisperCppRecogniser(
            model=(settings or {}).get("model"),
            language=language_of((settings or {}).get("language", "auto")) or "auto")

    return None


def speaker(settings, api_key=None, locale="en"):
    """ElevenLabs, or the scripted double. The same argument as above, for the same reason.

    The key is passed in rather than read here: secrets reach a plugin in its hello frame and have
    no business being looked up from inside an engine.
    """
    wanted = (settings or {}).get("engine")

    if wanted == "scripted":
        return ScriptedSpeaker((settings or {}).get("delay_ms"))

    built = ElevenLabsSpeaker(
        voice=(settings or {}).get("voice"), api_key=api_key,
        locale=(settings or {}).get("locale") or locale,
        base=(settings or {}).get("base"))

    # Returned even when it cannot speak, so the caller can ask it *why* rather than being handed
    # None and having to guess which half is missing.
    return built
