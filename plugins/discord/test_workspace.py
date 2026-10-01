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


class Pitch(unittest.TestCase):
    """Raising the voice without speeding it up.

    Piper's whole Portuguese catalogue is five voices and the highest measures the same as a voice
    Windows ships as female, so "pick a different one" is not an option that exists. Shifting is the
    only lever, and it is only worth having if the speech does not get faster with the pitch.
    """

    def test_the_slowdown_is_twice_the_distance_from_one(self):
        # --length_scale buys about half the duration it asks for, because the silence around the
        # phonemes does not stretch with them. Compensating with the pitch itself leaves the voice
        # talking ~10% faster, which is what the first attempt did.
        self.assertAlmostEqual(1.0, voice_engines._length_scale_for(1.0))
        self.assertAlmostEqual(1.4, voice_engines._length_scale_for(1.2))
        self.assertAlmostEqual(1.3, voice_engines._length_scale_for(1.15))

    def test_shifting_shortens_the_audio_by_the_factor(self):
        import io as _io
        import struct
        import wave

        rate, seconds, factor = 22050, 1.0, 1.2
        frames = int(rate * seconds)

        raw = _io.BytesIO()
        written = wave.open(raw, "wb")
        written.setnchannels(1)
        written.setsampwidth(2)
        written.setframerate(rate)
        written.writeframes(struct.pack("<%dh" % frames, *([1000] * frames)))
        written.close()

        shifted = wave.open(_io.BytesIO(voice_engines._shift_pitch(raw.getvalue(), factor)))

        self.assertEqual(rate, shifted.getframerate())
        self.assertAlmostEqual(frames / factor, shifted.getnframes(), delta=2)

    def test_audio_that_is_not_sixteen_bit_is_left_alone(self):
        # Resampling the wrong width produces noise or silence, and both are worse than a low voice.
        import io as _io
        import wave

        raw = _io.BytesIO()
        written = wave.open(raw, "wb")
        written.setnchannels(1)
        written.setsampwidth(1)
        written.setframerate(22050)
        written.writeframes(b"\x80" * 1000)
        written.close()

        self.assertEqual(raw.getvalue(), voice_engines._shift_pitch(raw.getvalue(), 1.2))
