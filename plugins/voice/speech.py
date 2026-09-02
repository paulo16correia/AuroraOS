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


class FasterWhisperRecogniser:
    """Faster-Whisper, in-process.

    The one this task asked for. Imported lazily so a machine without it can still start the plugin
    and be told what is missing, rather than failing at import and taking the whole thing down.
    """

    name = "faster-whisper"

    def __init__(self, model="turbo", device="auto", compute_type="auto", language="pt"):
        self.model_name = model
        self.language = language
        self._device = device
        self._compute_type = compute_type
        self._model = None

    @staticmethod
    def available():
        try:
            import faster_whisper  # noqa: F401
            return True
        except ImportError:
            return False

    def _load(self):
        if self._model is None:
            from faster_whisper import WhisperModel

            self._model = WhisperModel(
                self.model_name, device=self._device, compute_type=self._compute_type)

        return self._model

    def transcribe(self, pcm16):
        import io

        model = self._load()

        # Faster-Whisper resamples internally, but handing it the rate it wants avoids a second
        # conversion and keeps this the only place a rate is decided.
        audio = io.BytesIO(wav(resample_to(pcm16, SAMPLE_RATE, WHISPER_RATE), WHISPER_RATE))

        segments, info = model.transcribe(
            audio,
            language=self.language,

            # Voice activity detection inside the recogniser, so silence does not become the
            # phrase a model says when it heard nothing.
            vad_filter=True,
            beam_size=1)

        parts = list(segments)
        text = "".join(part.text for part in parts).strip()

        return {
            "text": text,
            # avg_logprob is per-segment log probability. Averaged and exponentiated it is a rough
            # confidence, and it is the only one this engine offers.
            "confidence": round(
                min(1.0, max(0.0, 2 ** (sum(p.avg_logprob for p in parts) / len(parts))))
                if parts else 0.0, 3),
            "seconds": round(getattr(info, "duration", 0.0), 2),
            "engine": self.name,
        }


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

    def __init__(self, model=None, language="pt", vocabulary=None):
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


class XttsSpeaker:
    """Coqui XTTS v2.

    The one this task asked for. Its licence is CPML — non-commercial — and the company that made
    it is gone; the package is community-maintained. Both are worth knowing before it becomes the
    default voice of something.
    """

    name = "xtts-v2"

    def __init__(self, model="tts_models/multilingual/multi-dataset/xtts_v2",
                 language="pt", speaker_wav=None):
        self.model_name = model
        self.language = language
        self.speaker_wav = speaker_wav
        self._tts = None

    @staticmethod
    def available():
        try:
            import TTS  # noqa: F401
            return True
        except ImportError:
            return False

    def _load(self):
        if self._tts is None:
            from TTS.api import TTS as CoquiTTS

            self._tts = CoquiTTS(self.model_name)

        return self._tts

    def speak(self, text):
        tts = self._load()
        directory = tempfile.mkdtemp(prefix="aurora-voice-")
        target = os.path.join(directory, "said.wav")

        try:
            tts.tts_to_file(
                text=text, file_path=target,
                language=self.language, speaker_wav=self.speaker_wav)

            return _pcm_from_wav(open(target, "rb").read())
        finally:
            shutil.rmtree(directory, ignore_errors=True)


class SaySpeaker:
    """macOS `say`.

    Already present, already carrying a European Portuguese voice this owner chose and heard. The
    same reasoning as whisper.cpp above: a local speaker that exists beats a better one that does
    not.
    """

    name = "say"

    def __init__(self, voice="Joana"):
        self.voice = voice

    @staticmethod
    def available():
        return shutil.which("say") is not None

    def speak(self, text):
        if not self.available():
            raise SpeechUnavailable("`say` is not on this machine")

        directory = tempfile.mkdtemp(prefix="aurora-voice-")
        target = os.path.join(directory, "said.wav")

        try:
            # LEI16 explicitly: `say` writes 32-bit float however it is asked otherwise, and the
            # right-length noise that produces is indistinguishable from a working encoder.
            finished = subprocess.run(
                [shutil.which("say"), "-o", target,
                 "--data-format=LEI16@%d" % SAMPLE_RATE, "-v", self.voice, text],
                capture_output=True, timeout=60, check=False)

            if finished.returncode != 0:
                raise SpeechUnavailable("`say` exited %d" % finished.returncode)

            return _pcm_from_wav(open(target, "rb").read())
        finally:
            shutil.rmtree(directory, ignore_errors=True)


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

    def __init__(self, transcripts):
        self._transcripts = list(transcripts)

    def transcribe(self, pcm16):
        text = self._transcripts.pop(0) if self._transcripts else ""

        return {
            "text": text,
            "confidence": 1.0,
            "seconds": round(duration_ms(pcm16) / 1000.0, 2),
            "engine": self.name,
        }


class ScriptedSpeaker:
    """A synthesiser that produces the right amount of silence. Chosen the same way."""

    name = "scripted"

    def speak(self, text):
        # A tenth of a second per word, which is roughly speech and is entirely arbitrary. What
        # matters downstream is that audio of a plausible length arrives.
        samples = int(SAMPLE_RATE * 0.1 * max(1, len(text.split())))

        return b"\x00\x00" * samples


def best_recogniser(settings):
    """The recogniser this machine can actually run, preferring what was configured."""
    wanted = (settings or {}).get("engine")

    if wanted == "scripted":
        return ScriptedRecogniser((settings or {}).get("transcripts") or [])

    if wanted == "faster-whisper" or (wanted is None and FasterWhisperRecogniser.available()):
        if FasterWhisperRecogniser.available():
            return FasterWhisperRecogniser(
                model=(settings or {}).get("model", "turbo"),
                language=(settings or {}).get("language", "pt"))

    if wanted in (None, "whisper.cpp") and WhisperCppRecogniser.available():
        return WhisperCppRecogniser(
            model=(settings or {}).get("model"),
            language=(settings or {}).get("language", "pt"))

    return None


def best_speaker(settings):
    """The speaker this machine can actually run, preferring what was configured."""
    wanted = (settings or {}).get("engine")

    if wanted == "scripted":
        return ScriptedSpeaker()

    if wanted in (None, "xtts") and XttsSpeaker.available():
        return XttsSpeaker(language=(settings or {}).get("language", "pt"))

    if wanted in (None, "say") and SaySpeaker.available():
        return SaySpeaker(voice=(settings or {}).get("voice", "Joana"))

    return None
