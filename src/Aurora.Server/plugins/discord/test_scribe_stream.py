#!/usr/bin/env python3
"""The realtime recogniser's side of the wire, without a network.

What is worth holding here is the shape of the request and one judgement: that partial transcripts
are dropped. They are what makes this fast — the service transcribes while somebody talks — and they
change as it listens, so answering one would be answering a guess the service itself had not
finished making.
"""

import base64
import json
import threading
import unittest

import scribe_stream


class Socket:
    """Enough of a WebSocket to see what was sent and to hand back what a server would say."""

    def __init__(self, replies=None):
        self.sent = []
        self.closed_by_caller = False
        self._replies = list(replies or [])
        self._drained = threading.Event()

    def send_json(self, value):
        self.sent.append(value)

    def receive(self):
        if self._replies:
            return json.dumps(self._replies.pop(0))

        # Nothing left: behave like a server that closed, which ends the reader thread.
        self._drained.set()
        return None

    def close(self):
        self.closed_by_caller = True

    def wait(self, seconds=2.0):
        self._drained.wait(seconds)


class Opening(unittest.TestCase):
    """The URL, which is where every choice about this recogniser is declared."""

    def test_it_asks_for_the_realtime_model_at_discords_own_rate(self):
        url = scribe_stream.ScribeStream("k")._url

        self.assertIn("wss://api.elevenlabs.io/v1/speech-to-text/realtime", url)
        self.assertIn("model_id=scribe_v2_realtime", url)

        # 48kHz because that is what Discord carries. For the first time nothing in this path
        # resamples anything.
        self.assertIn("audio_format=pcm_48000", url)

    def test_the_service_decides_where_a_sentence_ends(self):
        # The point of the change. A silence threshold here was this plugin guessing from a timer,
        # without the audio or the model; the service has both and decides while somebody is still
        # speaking.
        url = scribe_stream.ScribeStream("k")._url

        self.assertIn("commit_strategy=vad", url)
        self.assertIn("vad_silence_threshold_secs=", url)

    def test_the_language_is_sent_when_it_is_known(self):
        self.assertIn("language_code=pt", scribe_stream.ScribeStream("k", language="pt")._url)
        self.assertNotIn("language_code", scribe_stream.ScribeStream("k")._url)

    def test_the_key_travels_as_a_header(self):
        # The server-side half of this API. The other half is a single-use token in the query
        # string, for a browser that must never hold a key — not a plugin on the owner's machine.
        stream = scribe_stream.ScribeStream("segredo")

        self.assertEqual({"xi-api-key": "segredo"}, stream._headers)
        self.assertNotIn("segredo", stream._url)

    def test_realtime_takes_fewer_keyterms_than_batch(self):
        # Fifty against a thousand, so a vocabulary that is fine for the batch model has to be cut
        # rather than refused.
        stream = scribe_stream.ScribeStream("k", keyterms=["t%d" % n for n in range(80)])

        self.assertEqual(50, stream._url.count("keyterms="))


class Sending(unittest.TestCase):
    def test_audio_goes_up_base64_with_its_rate(self):
        stream = scribe_stream.ScribeStream("k")
        stream._socket = Socket()

        stream.send(b"\x01\x02\x03\x04")

        sent, = stream._socket.sent
        self.assertEqual("input_audio_chunk", sent["message_type"])
        self.assertEqual(b"\x01\x02\x03\x04", base64.b64decode(sent["audio_base_64"]))
        self.assertEqual(48000, sent["sample_rate"])
        self.assertFalse(sent["commit"])

    def test_a_failed_send_is_remembered_and_stops_the_stream(self):
        # Reported rather than retried silently: a quota or a key is something an owner can fix,
        # and it is indistinguishable from silence otherwise.
        class Broken(Socket):
            def send_json(self, value):
                raise OSError("gone")

        stream = scribe_stream.ScribeStream("k")
        stream._socket = Broken()

        self.assertFalse(stream.send(b"\x00\x00"))
        self.assertIn("OSError", stream.failure)

        # And nothing more is sent after it.
        self.assertFalse(stream.send(b"\x00\x00"))


class Hearing(unittest.TestCase):
    def _run(self, replies):
        stream = scribe_stream.ScribeStream("k")
        stream._socket = Socket(replies)
        stream._reader = threading.Thread(target=stream._read, daemon=True)
        stream._reader.start()
        stream._socket.wait()
        stream._reader.join(2.0)
        return stream

    def test_committed_transcripts_come_back(self):
        stream = self._run([
            {"message_type": "session_started", "session_id": "s"},
            {"message_type": "committed_transcript", "text": "Aurora, onde fica a Ericeira?"},
        ])

        self.assertEqual(["Aurora, onde fica a Ericeira?"], stream.heard())

    def test_partial_transcripts_are_dropped(self):
        """The judgement worth a test.

        Partials are what make this fast, and they change as the service listens. Answering one
        would be answering a guess it had not finished making — and in a voice channel that is
        Aurora interrupting somebody mid-sentence with a reply to half of it.
        """
        stream = self._run([
            {"message_type": "partial_transcript", "text": "Aurora, onde"},
            {"message_type": "partial_transcript", "text": "Aurora, onde fica"},
            {"message_type": "committed_transcript", "text": "Aurora, onde fica a Ericeira?"},
        ])

        self.assertEqual(["Aurora, onde fica a Ericeira?"], stream.heard())

    def test_an_empty_commit_is_not_a_sentence(self):
        stream = self._run([{"message_type": "committed_transcript", "text": "   "}])

        self.assertEqual([], stream.heard())

    def test_what_the_owner_can_fix_is_kept(self):
        # A quota, a key, a licence. These are the failures with a person's action behind them, and
        # they look exactly like nobody talking if they are not reported.
        stream = self._run([{"message_type": "quota_exceeded", "error": "out of credits"}])

        self.assertIn("quota_exceeded", stream.failure)
        self.assertIn("out of credits", stream.failure)

    def test_asking_twice_does_not_repeat_a_sentence(self):
        stream = self._run([{"message_type": "committed_transcript", "text": "olá"}])

        self.assertEqual(["olá"], stream.heard())
        self.assertEqual([], stream.heard())


class Folding(unittest.TestCase):
    def test_two_channels_become_one_at_the_same_rate(self):
        import struct

        # One second of 48kHz stereo, the two channels differing so the average is visible.
        pcm = b"".join(struct.pack("<hh", 1000, 2000) for _ in range(48000))
        mono = scribe_stream.mono(pcm)

        self.assertEqual(48000 * 2, len(mono))
        self.assertEqual(1500, struct.unpack_from("<h", mono, 0)[0])

    def test_nothing_is_resampled(self):
        # An average, not a decimation. Every frame in produces a frame out, so no band is lost —
        # which is the whole difference from the conversion whisper.cpp needed.
        import struct

        pcm = b"".join(struct.pack("<hh", n, n) for n in range(100))

        self.assertEqual(100 * 2, len(scribe_stream.mono(pcm)))


if __name__ == "__main__":
    unittest.main(verbosity=2)
