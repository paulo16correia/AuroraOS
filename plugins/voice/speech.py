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

import json
import os
import shutil
import struct
import subprocess
import tempfile
import time

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


class WhisperCppRecogniser:
    """whisper.cpp through its command-line program.

    Already installed on this machine, with models, from the Discord voice work — which is why it
    is here: a local recogniser that exists is worth more than a better one that does not.
    """

    name = "whisper.cpp"

    SEARCH = [
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "models"),
        os.path.expanduser("~/Developer/Aurora/plugins/discord/models"),
        "/opt/homebrew/share/whisper.cpp/models",
    ]

    # Multilingual first. An English-only model transcribes Portuguese into confident nonsense
    # rather than failing, which is the worse of the two.
    MODELS = ["ggml-large-v3-turbo.bin", "ggml-medium.bin", "ggml-small.bin", "ggml-base.bin"]

    def __init__(self, model=None, language="auto", vocabulary=None):
        self.language = language
        self.model = model or self.find_model()

        # Told to the recogniser as context. A name is the word recognition gets wrong most, and
        # saying it exists costs nothing (docs/adr/0071).
        self.vocabulary = vocabulary or ["Aurora"]

    @classmethod
    def find_model(cls):
        for directory in cls.SEARCH:
            for name in cls.MODELS:
                candidate = os.path.join(directory, name)

                if os.path.exists(candidate):
                    return candidate

        return None

    @classmethod
    def available(cls):
        return shutil.which("whisper-cli") is not None and cls.find_model() is not None

    def transcribe(self, pcm16):
        if not self.available():
            raise SpeechUnavailable("whisper.cpp or its model is not on this machine")

        directory = tempfile.mkdtemp(prefix="aurora-voice-")
        source = os.path.join(directory, "turn.wav")

        try:
            with open(source, "wb") as handle:
                handle.write(wav(resample_to(pcm16, SAMPLE_RATE, WHISPER_RATE), WHISPER_RATE))

            finished = subprocess.run(
                [shutil.which("whisper-cli"), "-m", self.model, "-f", source,
                 "-l", self.language, "-bs", "1", "-nt", "--no-prints",
                 "--prompt", ", ".join(self.vocabulary)],
                capture_output=True, timeout=120, check=False)

            if finished.returncode != 0:
                raise SpeechUnavailable(
                    "whisper.cpp exited %d" % finished.returncode)

            text = finished.stdout.decode("utf-8", "replace").strip()

            return {
                "text": text,
                # This engine reports none, and inventing one would be worse than saying so.
                "confidence": None,
                "seconds": round(len(pcm16) / (SAMPLE_RATE * SAMPLE_WIDTH), 2),
                "engine": self.name,
            }
        finally:
            shutil.rmtree(directory, ignore_errors=True)


# ---------------------------------------------------------------------------
# speaking
# ---------------------------------------------------------------------------


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
        if self.locale:
            body["language_code"] = str(self.locale)

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
        return WhisperCppRecogniser(
            model=(settings or {}).get("model"),
            language=(settings or {}).get("language", "auto"))

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
