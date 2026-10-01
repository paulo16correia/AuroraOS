#!/usr/bin/env python3
"""The scratch directory an utterance is processed in has to be one the plugin can write in.

Under the sandbox, TEMP is the plugin's own working directory, which is granted to its container
and to nothing else. `tempfile.mkdtemp` applies its 0700 as a real Windows ACL from Python 3.13 on,
replacing the inherited protection and blocking inheritance — so the plugin created the directory,
failed on the first file inside it, and reported every single utterance as not understood.
"""

import os
import subprocess
import tempfile
import unittest

import voice_engines


class Workspace(unittest.TestCase):
    def setUp(self):
        self.parent = tempfile.mkdtemp(prefix="aurora-parent-")
        self._temp = os.environ.get("TEMP"), os.environ.get("TMP"), os.environ.get("TMPDIR")
        for name in ("TEMP", "TMP", "TMPDIR"):
            os.environ[name] = self.parent
        tempfile.tempdir = None

    def tearDown(self):
        for name, previous in zip(("TEMP", "TMP", "TMPDIR"), self._temp):
            if previous is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = previous
        tempfile.tempdir = None

        import shutil
        shutil.rmtree(self.parent, ignore_errors=True)

    def test_the_workspace_is_made_inside_temp(self):
        made = voice_engines._workspace()

        self.assertTrue(os.path.isdir(made))
        self.assertEqual(os.path.dirname(made), self.parent)

    def test_a_file_can_actually_be_written_in_it(self):
        # The whole point. Creating the directory always worked; this is the part that did not.
        made = voice_engines._workspace()

        with open(os.path.join(made, "utterance.wav"), "wb") as handle:
            handle.write(b"RIFF")

        self.assertTrue(os.path.isfile(os.path.join(made, "utterance.wav")))

    def test_two_workspaces_do_not_collide(self):
        self.assertNotEqual(voice_engines._workspace(), voice_engines._workspace())

    @unittest.skipUnless(os.name == "nt", "the inheritance this pins is a Windows ACL")
    def test_the_workspace_inherits_rather_than_replaces_its_protection(self):
        # An inherited entry is marked (I). A directory made the mkdtemp way has none at all — its
        # DACL is written from scratch and inheritance is blocked, which is exactly why a container
        # granted the parent could not reach the child. Asserted on the marker rather than on any
        # account name, which is localised.
        made = voice_engines._workspace()

        listed = subprocess.run(
            ["icacls", made], capture_output=True, text=True, check=True).stdout

        self.assertIn("(I)", listed, listed)

    @unittest.skipIf(os.name == "nt", "POSIX keeps the 0700 guarantee, which Windows has no use for")
    def test_posix_keeps_the_private_mode(self):
        # /tmp is shared on POSIX, so the mode is the protection and it is not given up.
        made = voice_engines._workspace()

        self.assertEqual(0o700, os.stat(made).st_mode & 0o777)


if __name__ == "__main__":
    unittest.main(verbosity=2)


class AudioContext(unittest.TestCase):
    """Sizing whisper's encoder window to the utterance (docs/adr/0085).

    The encoder walks a thirty-second window whatever it is given, and encoding was 73% of the time
    recognition took. Shortening it to fit is free on the short utterances a conversation is made
    of, and destructive if cut too close — which is why the rule is a margin over what the audio
    needs rather than a number somebody liked.
    """

    def test_the_window_is_twice_what_the_audio_needs(self):
        # Fifty units a second is whisper's own ratio; twice that is the margin. Asserted above
        # the floor, since below it the floor is the answer and this rule is not what is being read.
        self.assertEqual(500, voice_engines.audio_context_for(5.0))
        self.assertEqual(637, voice_engines.audio_context_for(6.37))

    def test_the_floor_wins_where_the_margin_would_be_smaller(self):
        # 3.48 seconds wants 348, which is under the floor. The floor is there because 256 was
        # measurably worse on audio whisper struggles with, and a short utterance is exactly where
        # the saving would have been smallest anyway.
        self.assertEqual(voice_engines.CONTEXT_FLOOR, voice_engines.audio_context_for(3.48))

    def test_short_utterances_still_get_a_floor(self):
        # Below the floor the decoder starts thrashing and takes *longer* than the full window.
        # A second of speech needs a hundred units; it gets 256.
        self.assertEqual(voice_engines.CONTEXT_FLOOR, voice_engines.audio_context_for(1.0))
        self.assertEqual(voice_engines.CONTEXT_FLOOR, voice_engines.audio_context_for(0.2))

    def test_long_utterances_ask_for_the_whole_window(self):
        # Past fifteen seconds the margin exceeds the window, and asking for all of it and asking
        # for nothing are the same thing to whisper.
        self.assertIsNone(voice_engines.audio_context_for(15.0))
        self.assertIsNone(voice_engines.audio_context_for(60.0))

    def test_audio_that_cannot_be_measured_uses_the_whole_window(self):
        # A malformed header is not a reason to fail a transcription. The full window still works.
        self.assertIsNone(voice_engines.audio_context_for(0))
        self.assertEqual(0, voice_engines.wav_seconds(b"not a wav at all"))

    def test_the_duration_is_read_from_the_header(self):
        import io as _io
        import struct
        import wave

        rate, seconds = 16000, 2.0
        frames = int(rate * seconds)

        raw = _io.BytesIO()
        written = wave.open(raw, "wb")
        written.setnchannels(1)
        written.setsampwidth(2)
        written.setframerate(rate)
        written.writeframes(struct.pack("<%dh" % frames, *([0] * frames)))
        written.close()

        self.assertAlmostEqual(seconds, voice_engines.wav_seconds(raw.getvalue()), places=3)
