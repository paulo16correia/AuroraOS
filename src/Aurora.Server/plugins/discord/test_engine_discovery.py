#!/usr/bin/env python3
"""A confined plugin can only reach engines that ship with it (F/voice, Windows).

The plugin's PATH under the sandbox is the system directories alone, so a TTS/STT engine installed
elsewhere is invisible to it. voice_engines therefore looks in the plugin's own directories — which
the sandbox grants it — before PATH, the same way it already finds its whisper model. These tests
pin that discovery so a confined plugin can use bundled engines, without changing that PATH and the
macOS built-in `say` keep working.

The stubs go in a temporary directory that `_engine_dirs` is pointed at, never in the plugin's real
`bin/`. An earlier version of this file wrote its `piper` stub over whatever was installed there
and removed it afterwards — which deleted a working engine off the machine it was testing, and cost
an evening of wondering why a call had gone silent.
"""

import os
import shutil
import stat
import tempfile
import unittest

import voice_engines


class BundledEngineDiscovery(unittest.TestCase):
    def setUp(self):
        self.bin = tempfile.mkdtemp(prefix="aurora-engines-")

        # Discovery is being tested, so discovery is what gets pointed somewhere safe. Patching
        # the directory list rather than the filesystem means the real search order — bundled
        # first, PATH after — is still the one running.
        self._engine_dirs = voice_engines._engine_dirs
        voice_engines._engine_dirs = lambda: [self.bin]

    def tearDown(self):
        voice_engines._engine_dirs = self._engine_dirs
        shutil.rmtree(self.bin, ignore_errors=True)

    def _drop(self, name):
        program = name + (".exe" if os.name == "nt" else "")
        path = os.path.join(self.bin, program)

        with open(path, "wb") as handle:
            handle.write(b"stub")

        if os.name != "nt":
            os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)

        return path

    def test_a_bundled_engine_is_found_in_the_plugins_own_bin(self):
        dropped = self._drop("piper")

        self.assertEqual(dropped, voice_engines._find_program("piper"))

    def test_absent_engine_falls_back_to_path(self):
        # Nothing bundled: the answer is whatever PATH says (None here on a clean machine), which
        # is exactly the pre-existing behaviour for an engine that is not installed.
        self.assertEqual(
            voice_engines._find_program("definitely-not-an-engine-xyz"),
            shutil.which("definitely-not-an-engine-xyz"))

    def test_the_real_plugin_directory_is_left_alone(self):
        # The guard on the mistake above. What discovery is pointed at is a temporary directory,
        # and nothing these tests write can land beside the plugin.
        self._drop("piper")

        self.assertNotIn(
            os.path.dirname(os.path.abspath(voice_engines.__file__)),
            voice_engines._find_program("piper"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
