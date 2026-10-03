"""Opus, through the system library.

Discord's voice protocol carries Opus and nothing else. A codec is not something a plugin can
implement, so this binds to `libopus` with ctypes and refuses clearly when it is not installed —
which is better than joining a call and transmitting silence.

Discord's parameters are fixed and not negotiable: 48kHz, stereo, 20 millisecond frames. That is
960 samples per channel per frame, and everything here assumes it.
"""

import ctypes
import ctypes.util
import os

SAMPLE_RATE = 48000
CHANNELS = 2
FRAME_MS = 20
SAMPLES_PER_FRAME = SAMPLE_RATE // 1000 * FRAME_MS      # 960
BYTES_PER_FRAME = SAMPLES_PER_FRAME * CHANNELS * 2      # 16-bit samples

APPLICATION_VOIP = 2048
MAX_PACKET = 4000

# Opus error codes worth naming. The rest are reported by number.
_ERRORS = {
    -1: "bad argument",
    -2: "the buffer was too small",
    -3: "an internal error",
    -4: "the packet was malformed",
    -5: "an unimplemented request",
    -6: "an invalid state",
    -7: "out of memory",
}


class OpusUnavailable(RuntimeError):
    """libopus is not installed. Said plainly, with what to do about it."""


# Where package managers put it. The dynamic loader does not search these — /opt/homebrew/lib is
# not a standard path on macOS — so a library that is plainly present on disk still fails to open
# when asked for by name alone.
SEARCH = ["/opt/homebrew/lib", "/usr/local/lib", "/usr/lib", "/usr/lib/x86_64-linux-gnu"]

NAMES = ["libopus.so.0", "libopus.0.dylib", "libopus.dylib", "libopus.so", "opus.dll"]


def _load():
    """Opens libopus, or returns None.

    Opening it rather than looking for the file, because those are different questions and
    answering the easy one is how a readiness report comes to say a codec is available while
    every call that needs it fails.
    """
    candidates = [ctypes.util.find_library("opus"), *NAMES]

    for directory in SEARCH:
        candidates.extend(os.path.join(directory, name) for name in NAMES)

    for candidate in candidates:
        if not candidate:
            continue
        try:
            return ctypes.CDLL(candidate)
        except OSError:
            continue

    return None


_OPUS = _load()


def available():
    """Whether Opus can be used — loaded, not merely present on disk."""
    return _OPUS is not None


def library():
    """The file that was actually opened, for a readiness report to name."""
    return getattr(_OPUS, "_name", None) if _OPUS is not None else None


def _declare():
    _OPUS.opus_encoder_create.restype = ctypes.c_void_p
    _OPUS.opus_encoder_create.argtypes = [
        ctypes.c_int32, ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_int)]

    _OPUS.opus_encode.restype = ctypes.c_int32
    _OPUS.opus_encode.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(ctypes.c_int16), ctypes.c_int,
        ctypes.c_char_p, ctypes.c_int32]

    _OPUS.opus_encoder_destroy.argtypes = [ctypes.c_void_p]

    _OPUS.opus_decoder_create.restype = ctypes.c_void_p
    _OPUS.opus_decoder_create.argtypes = [
        ctypes.c_int32, ctypes.c_int, ctypes.POINTER(ctypes.c_int)]

    _OPUS.opus_decode.restype = ctypes.c_int
    _OPUS.opus_decode.argtypes = [
        ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32,
        ctypes.POINTER(ctypes.c_int16), ctypes.c_int, ctypes.c_int]

    _OPUS.opus_decoder_destroy.argtypes = [ctypes.c_void_p]


if _OPUS is not None:
    _declare()


def _check(code, what):
    if code < 0:
        raise RuntimeError("opus %s failed: %s" % (what, _ERRORS.get(code, code)))
    return code


def _require():
    if _OPUS is None:
        raise OpusUnavailable(
            "libopus is not installed. Discord voice carries Opus and nothing else; install it "
            "with your package manager (brew install opus, apt install libopus0).")


class Encoder:
    """PCM in, Opus out. One per outgoing stream."""

    def __init__(self, bitrate=64000):
        _require()

        error = ctypes.c_int()
        self._state = _OPUS.opus_encoder_create(
            SAMPLE_RATE, CHANNELS, APPLICATION_VOIP, ctypes.byref(error))

        _check(error.value, "encoder_create")

    def encode(self, pcm):
        """One 20ms frame of 48kHz stereo 16-bit PCM."""
        if len(pcm) != BYTES_PER_FRAME:
            raise ValueError(
                "a frame is %d bytes of 48kHz stereo PCM, not %d" % (BYTES_PER_FRAME, len(pcm)))

        samples = (ctypes.c_int16 * (SAMPLES_PER_FRAME * CHANNELS)).from_buffer_copy(pcm)
        out = ctypes.create_string_buffer(MAX_PACKET)

        written = _check(
            _OPUS.opus_encode(self._state, samples, SAMPLES_PER_FRAME, out, MAX_PACKET),
            "encode")

        return out.raw[:written]

    def close(self):
        if self._state:
            _OPUS.opus_encoder_destroy(ctypes.c_void_p(self._state))
            self._state = None

    def __del__(self):
        try:
            self.close()
        except Exception:
            pass


class Decoder:
    """Opus in, PCM out. One per speaker, because Opus carries state between frames."""

    def __init__(self):
        _require()

        error = ctypes.c_int()
        self._state = _OPUS.opus_decoder_create(SAMPLE_RATE, CHANNELS, ctypes.byref(error))
        _check(error.value, "decoder_create")

    def decode(self, packet):
        pcm = (ctypes.c_int16 * (SAMPLES_PER_FRAME * CHANNELS))()

        samples = _check(
            _OPUS.opus_decode(
                self._state, packet, len(packet), pcm, SAMPLES_PER_FRAME, 0),
            "decode")

        return bytes(memoryview(pcm).cast("B")[:samples * CHANNELS * 2])

    def close(self):
        if self._state:
            _OPUS.opus_decoder_destroy(ctypes.c_void_p(self._state))
            self._state = None

    def __del__(self):
        try:
            self.close()
        except Exception:
            pass


def silence():
    """A frame of nothing, which is what Discord expects between utterances."""
    return b"\xf8\xff\xfe"
