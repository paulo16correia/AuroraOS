#!/usr/bin/env python3
"""The wire format, and a packet going all the way out and back.

Discord is not here, so what is tested is everything that does not need it: the exact bytes of an
RTP header, the discovery probe, and a full round trip through framing, encryption and Opus. That
last one is the useful one — it puts real PCM through the real encoder, the real cipher and the
real packet layout, and takes it apart again.
"""

import struct
import unittest

import crypto
import opus_codec
import voice_transport as vt


def tone(samples=opus_codec.SAMPLES_PER_FRAME):
    """A frame of something rather than silence, so a decode failure is visible."""
    import math
    return b"".join(
        struct.pack("<hh", int(9000 * math.sin(i / 14)), int(9000 * math.sin(i / 14)))
        for i in range(samples))


class RtpHeader(unittest.TestCase):
    def test_the_header_is_the_twelve_bytes_the_rfc_describes(self):
        header = vt.rtp_header(sequence=0x1234, timestamp=0xDEADBEEF, ssrc=0x01020304)

        self.assertEqual(len(header), 12)

        # Version 2, no padding, no extension, no CSRCs; payload type 120 is Opus for Discord.
        self.assertEqual(header[0], 0x80)
        self.assertEqual(header[1], 0x78)
        self.assertEqual(header[2:4], b"\x12\x34")
        self.assertEqual(header[4:8], b"\xde\xad\xbe\xef")
        self.assertEqual(header[8:12], b"\x01\x02\x03\x04")

    def test_the_sequence_and_timestamp_wrap_rather_than_overflow(self):
        # Both are fixed-width on the wire. A long call reaches the end of both, and a crash there
        # would be a crash after an hour of working perfectly.
        header = vt.rtp_header(sequence=0x10001, timestamp=0x1FFFFFFFF, ssrc=1)

        self.assertEqual(header[2:4], b"\x00\x01")
        self.assertEqual(header[4:8], b"\xff\xff\xff\xff")


class Discovery(unittest.TestCase):
    def test_the_probe_is_the_shape_discord_expects(self):
        packet = vt.discovery_packet(0xAABBCCDD)

        self.assertEqual(len(packet), 74)
        self.assertEqual(packet[:2], b"\x00\x01")
        self.assertEqual(packet[2:4], struct.pack(">H", 70))
        self.assertEqual(packet[4:8], b"\xaa\xbb\xcc\xdd")

    def test_the_reply_gives_back_the_address_the_outside_world_sees(self):
        # A machine behind NAT cannot ask its own stack what address Discord will send to.
        reply = struct.pack(">HHI", 0x2, 70, 0xAABBCCDD)
        reply += b"203.0.113.7".ljust(64, b"\x00")
        reply += struct.pack(">H", 50001)

        self.assertEqual(vt.parse_discovery(reply), ("203.0.113.7", 50001))

    def test_a_short_reply_is_refused_rather_than_misread(self):
        with self.assertRaises(ValueError):
            vt.parse_discovery(b"too short")


class Nonce(unittest.TestCase):
    def test_the_nonce_is_padded_to_what_the_cipher_wants(self):
        nonce = vt.nonce_for(1)

        self.assertEqual(len(nonce), crypto.NONCE_BYTES)
        self.assertEqual(nonce[:4], b"\x00\x00\x00\x01")
        self.assertEqual(nonce[4:], bytes(20))

    def test_two_packets_never_share_a_nonce(self):
        # Reusing a nonce with the same key leaks the keystream, which for voice means somebody
        # who captured two packets can read both.
        self.assertNotEqual(vt.nonce_for(1), vt.nonce_for(2))


class RoundTrip(unittest.TestCase):
    """PCM, out through the real layers, and back."""

    def _transport(self, key):
        transport = vt.VoiceTransport.__new__(vt.VoiceTransport)
        transport._key = key
        transport._decoders = {}
        return transport

    @unittest.skipUnless(opus_codec.available(), "libopus is not installed")
    def test_a_frame_survives_framing_encryption_and_the_codec(self):
        key = crypto.random_key()
        encoder = opus_codec.Encoder()

        pcm = tone()
        opus_packet = encoder.encode(pcm)

        header = vt.rtp_header(7, 960, 0x01020304)
        nonce = vt.nonce_for(7)
        sealed = crypto.encrypt(key, nonce, opus_packet, header)
        wire = header + sealed + nonce[:4]

        # Exactly what goes on the socket. Anything wrong with the layout shows up here rather
        # than as silence in a call.
        received = self._transport(key).receive_packet(wire)

        self.assertIsNotNone(received)
        ssrc, decoded = received

        self.assertEqual(ssrc, 0x01020304)
        self.assertEqual(len(decoded), opus_codec.BYTES_PER_FRAME)

        # Opus is lossy, so the samples are not identical. What matters is that it is audio and
        # not silence — a decoder handed a broken packet returns zeroes.
        self.assertGreater(max(abs(s) for s in struct.unpack("<%dh" % (len(decoded) // 2), decoded)), 500)

    @unittest.skipUnless(opus_codec.available(), "libopus is not installed")
    def test_a_packet_from_somebody_else_is_dropped_silently(self):
        transport = self._transport(crypto.random_key())

        header = vt.rtp_header(1, 0, 99)
        forged = header + bytes(60) + b"\x00\x00\x00\x01"

        # It does not authenticate, so it is somebody else's or it was altered. Nothing to report
        # and nothing to be done about it.
        self.assertIsNone(transport.receive_packet(forged))

    def test_a_runt_packet_is_dropped(self):
        self.assertIsNone(self._transport(crypto.random_key()).receive_packet(b"\x80\x78"))

    @unittest.skipUnless(opus_codec.available(), "libopus is not installed")
    def test_each_speaker_gets_their_own_decoder(self):
        key = crypto.random_key()
        transport = self._transport(key)
        encoder = opus_codec.Encoder()

        for index, ssrc in enumerate([111, 222, 111]):
            header = vt.rtp_header(index, index * 960, ssrc)
            nonce = vt.nonce_for(index + 1)
            packet = header + crypto.encrypt(key, nonce, encoder.encode(tone()), header) + nonce[:4]
            transport.receive_packet(packet)

        # Opus carries state between frames; mixing two people through one decoder produces
        # artefacts that sound like a bad connection.
        self.assertEqual(sorted(transport._decoders), [111, 222])


if __name__ == "__main__":
    unittest.main(verbosity=2)
