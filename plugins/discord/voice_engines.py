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

from vendor.aurora_voice import speech as _speech

# What Discord's voice protocol requires, and what nothing in Python's standard library provides.
# Named here so a refusal can tell somebody exactly what is missing rather than failing obscurely.
OPUS_LIBRARIES = ["libopus.so.0", "libopus.0.dylib", "libopus.dylib", "libopus.so", "opus.dll"]
OPUS_SEARCH = ["/opt/homebrew/lib", "/usr/local/lib", "/usr/lib", "/usr/lib/x86_64-linux-gnu"]

# ---- hearing, which is the voice plugin's ----
#
# All of it used to be written out here: how many threads to ask for, how much of the encoder's
# window an utterance needs, which programs count as a recogniser, where a model hides, and what a
# recogniser says when it heard nothing. It was the better of the two implementations, so it moved
# into plugins/voice/speech.py rather than being replaced by the weaker one, and this plugin now
# borrows it back through the copy in vendor/.
#
# The names below are the ones this plugin and its tests already used, kept exactly, so what proves
# the move is behaviour rather than a promise. What they add is this plugin's own directory: where
# a plugin keeps its models and its bundled programs is the one thing that cannot be shared,
# because each is granted its own and nothing else.

_AQUI = os.path.dirname(os.path.abspath(__file__))

CONTEXT_UNITS_PER_SECOND = _speech.CONTEXT_UNITS_PER_SECOND
CONTEXT_MARGIN = _speech.CONTEXT_MARGIN
CONTEXT_FLOOR = _speech.CONTEXT_FLOOR
CONTEXT_FULL = _speech.CONTEXT_FULL

STT_ENGINES = _speech.STT_ENGINES
MODEL_NAMES = _speech.MODEL_NAMES
MODEL_SEARCH = _speech.model_search(_AQUI)
HALLUCINATIONS = _speech.HALLUCINATIONS

_default_threads = _speech.default_threads
audio_context_for = _speech.audio_context_for
wav_seconds = _speech.wav_seconds
_workspace = _speech.workspace
is_silence = _speech.is_silence
is_hallucination = _speech.is_hallucination


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
    """A whisper model file, or None. Separate from the program and separately absent."""
    return _speech.find_model(preferred, home=_AQUI)


def _engine_dirs():
    """The plugin's own directories, which the sandbox grants it — where a bundled engine lives."""
    return _speech.engine_dirs(_AQUI)


def _find_program(name):
    """An engine executable, preferring one shipped with the plugin over one on PATH.

    The directories are passed in rather than left to the shared code to work out, because they are
    this plugin's and because a test that wants to point discovery at a folder it controls does it
    by replacing `_engine_dirs`.
    """
    return _speech.find_program(name, _engine_dirs())


def find_stt(preferred_model=None):
    """A local speech-to-text program with a model to run, or None.

    Both, because either alone recognises nothing. whisper.cpp with no model exits non-zero on
    every utterance, which reads as speech that could not be understood rather than as a file that
    was never downloaded.
    """
    return _speech.find_stt(preferred_model, home=_AQUI, dirs=_engine_dirs())


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


# One voice in Aurora, and this is where this plugin borrows it. The client used to be written out
# again here — the same request to the same host, with its own idea of which model to ask for and
# its own words for a missing key. Two implementations are two places for an answer to come back
# wrong, so the names below are the voice plugin's, and only what Discord needs on top of them is
# written here.
#
# A copy rather than an import across plugins, because plugins do not share a process, a sandbox or
# a directory. `vendor/aurora_voice/METADATA` says where it came from, and a test fails the build
# if the two drift apart.

ELEVENLABS_HOST = _speech.ElevenLabsSpeaker.HOST
ELEVENLABS_BASE = _speech.ElevenLabsSpeaker.BASE
ELEVENLABS_MODEL = _speech.ElevenLabsSpeaker.MODEL
ELEVENLABS_TIMEOUT = _speech.ElevenLabsSpeaker.TIMEOUT

# 48kHz because that is what Discord's Opus encoder takes. Asking for anything else would buy a
# resampling step and the artefacts that come with it, for nothing. The only number here that is
# Discord's rather than the speech service's.
ELEVENLABS_RATE = 48000

resolve_voice = _speech.resolve_voice
_speech_base = _speech._speech_base


def cloud_tts(voice_setting, api_key, language=None):
    """The speaking engine, or None with nothing said about why.

    Both halves have to be present: a key without a voice cannot speak and a voice without a key
    cannot either. `readiness` is what explains which is missing.
    """
    falante = _speech.ElevenLabsSpeaker(
        voice=voice_setting, api_key=api_key, locale=language or "", rate=ELEVENLABS_RATE)

    return falante if falante.available() else None


def _to_stereo(mono):
    """Duplicates each sample into both channels.

    The speech service returns one channel and Discord carries two. Done here rather than later
    because the framing downstream counts bytes, and handing it mono would silently halve every
    frame. This is the one thing in the speaking path that is Discord's own.
    """
    out = bytearray(len(mono) * 2)
    out[0::4] = mono[0::2]
    out[1::4] = mono[1::2]
    out[2::4] = mono[0::2]
    out[3::4] = mono[1::2]
    return bytes(out)


def synthesise_stream(engine, text, api_key, timeout=ELEVENLABS_TIMEOUT, base=None):
    """Yields 48kHz stereo PCM as it arrives, so speaking can start before the sentence is made.

    The generator is the cancellation mechanism. Closing it — which is what the transport does when
    somebody interrupts — closes the one underneath, which stops reading and drops the connection.

    Raises RuntimeError on anything that means no audio, rather than yielding silence: a refusal
    that says the quota ran out is useful, and half a second of nothing is not. The speech client
    raises its own exception for that, translated here because this plugin's callers give the floor
    back on RuntimeError and a sentence that fails has to reach them.
    """
    falante = _speech.ElevenLabsSpeaker(
        voice=engine.voice_id, api_key=api_key or engine.api_key,
        locale=engine.locale, rate=engine.rate or ELEVENLABS_RATE, base=base)
    falante.TIMEOUT = timeout

    pendente = b""

    try:
        for pedaco in falante.stream(text):
            pendente += pedaco

            # A 16-bit sample must not be split across a channel duplication, so an odd trailing
            # byte waits for its other half.
            inteiro = len(pendente) - (len(pendente) % 2)

            if inteiro:
                yield _to_stereo(pendente[:inteiro])
                pendente = pendente[inteiro:]
    except _speech.SpeechUnavailable as mudo:
        raise RuntimeError(str(mudo)) from mudo


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
            "voice_transport.py, which this plugin needs to carry audio and which is not "
            "beside it — an install missing a file rather than a feature nobody wrote")
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
        "voice": tts.voice_id if tts else None,
        "language": language,
        "e2ee": e2ee,
        "opus": opus,
        "stt": stt["name"] if stt else None,
        "tts": tts.name if tts else None,
        "missing": missing,

        # Said explicitly, because these are the properties that would be quietly lost first, and
        # they are no longer the same answer. Recognition is still local: nobody's voice is
        # uploaded, and a recording exists only as bytes in memory until it becomes text. Speaking
        # is not: the sentence Aurora is about to say is sent to ElevenLabs to be read aloud.
        "audio_leaves_this_machine": False,
        "text_leaves_this_machine": bool(tts),
        "speech_service": ELEVENLABS_HOST if tts else None,
    }


def transcribe(engine, audio_bytes, model=None, timeout=180, gpu=True,
               language="auto", prompt="", threads=None, audio_ctx=True):
    """Turns speech into text, locally, and keeps neither the audio nor the file."""
    return _speech.transcribe(
        engine, audio_bytes, model=model, timeout=timeout, gpu=gpu, language=language,
        prompt=prompt, threads=threads, audio_ctx=audio_ctx)
