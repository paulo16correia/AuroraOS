#!/usr/bin/env python3
"""RFC test vectors for the cipher, because hand-written cryptography deserves suspicion.

Every vector here is copied from the specification rather than produced by this code, which is the
only kind of test that can tell a correct implementation from a self-consistent wrong one. If these
pass, the pure-Python path computes what the RFC says it should — byte for byte, including the
intermediate keystream blocks.
"""

import unittest

import crypto


class ChaCha20Block(unittest.TestCase):
    """RFC 8439 §2.3.2."""

    def test_the_block_function_matches_the_rfc(self):
        key = bytes(range(32))
        nonce = bytes.fromhex("000000090000004a00000000")

        block = crypto.chacha20_block(key, 1, nonce)

        self.assertEqual(
            block.hex(),
            "10f1e7e4d13b5915500fdd1fa32071c4"
            "c7d1f4c733c068030422aa9ac3d46c4e"
            "d2826446079faa0914c2d705d98b02a2"
            "b5129cd1de164eb9cbd083e8a2503c4e")


class ChaCha20Stream(unittest.TestCase):
    """RFC 8439 §2.4.2."""

    def test_the_stream_cipher_matches_the_rfc(self):
        key = bytes(range(32))
        nonce = bytes.fromhex("000000000000004a00000000")
        plaintext = (
            b"Ladies and Gentlemen of the class of '99: If I could offer you "
            b"only one tip for the future, sunscreen would be it.")

        ciphertext = crypto.chacha20(key, 1, nonce, plaintext)

        self.assertEqual(
            ciphertext.hex(),
            "6e2e359a2568f98041ba0728dd0d6981"
            "e97e7aec1d4360c20a27afccfd9fae0b"
            "f91b65c5524733ab8f593dabcd62b357"
            "1639d624e65152ab8f530c359f0861d8"
            "07ca0dbf500d6a6156a38e088a22b65e"
            "52bc514d16ccf806818ce91ab7793736"
            "5af90bbf74a35be6b40b8eedf2785e42"
            "874d")


class Poly1305(unittest.TestCase):
    """RFC 8439 §2.5.2."""

    def test_the_authenticator_matches_the_rfc(self):
        key = bytes.fromhex(
            "85d6be7857556d337f4452fe42d506a8"
            "0103808afb0db2fd4abff6af4149f51b")
        message = b"Cryptographic Forum Research Group"

        self.assertEqual(
            crypto.poly1305(key, message).hex(), "a8061dc1305136c6c22b8baf0c0127a9")


class HChaCha20(unittest.TestCase):
    """RFC draft-irtf-cfrg-xchacha §2.2.1."""

    def test_the_subkey_derivation_matches_the_draft(self):
        key = bytes.fromhex(
            "000102030405060708090a0b0c0d0e0f"
            "101112131415161718191a1b1c1d1e1f")
        nonce = bytes.fromhex("000000090000004a0000000031415927")

        self.assertEqual(
            crypto.hchacha20(key, nonce).hex(),
            "82413b4227b27bfed30e42508a877d73"
            "a0f9e4d58a74a853c12ec41326d3ecdc")


class Aead(unittest.TestCase):
    """RFC 8439 §2.8.2, through the XChaCha20 construction."""

    def test_chacha20_poly1305_matches_the_rfc(self):
        key = bytes.fromhex(
            "808182838485868788898a8b8c8d8e8f"
            "909192939495969798999a9b9c9d9e9f")
        nonce = bytes.fromhex("070000004041424344454647")
        aad = bytes.fromhex("50515253c0c1c2c3c4c5c6c7")
        plaintext = (
            b"Ladies and Gentlemen of the class of '99: If I could offer you "
            b"only one tip for the future, sunscreen would be it.")

        sealed = crypto._aead_chacha20_poly1305(key, nonce, plaintext, aad)

        self.assertEqual(
            sealed[:-16].hex(),
            "d31a8d34648e60db7b86afbc53ef7ec2"
            "a4aded51296e08fea9e2b5a736ee62d6"
            "3dbea45e8ca9671282fafb69da92728b"
            "1a71de0a9e060b2905d6a5b67ecd3b36"
            "92ddbd7f2d778b8c9803aee328091b58"
            "fab324e4fad675945585808b4831d7bc"
            "3ff4def08e4b7a9de576d26586cec64b"
            "6116")

        self.assertEqual(sealed[-16:].hex(), "1ae10b594f09e26a7e902ecbd0600691")


class Behaviour(unittest.TestCase):
    def test_a_round_trip_returns_what_went_in(self):
        key = crypto.random_key()
        nonce = bytes(range(24))

        sealed = crypto.encrypt(key, nonce, b"one twenty-millisecond frame", b"rtp")
        self.assertEqual(crypto.decrypt(key, nonce, sealed, b"rtp"), b"one twenty-millisecond frame")

    def test_a_tampered_packet_is_refused(self):
        key = crypto.random_key()
        nonce = bytes(range(24))
        sealed = bytearray(crypto.encrypt(key, nonce, b"audio", b"rtp"))

        sealed[0] ^= 0x01

        # An attacker who can change a packet and have it decrypt anyway can put words in Aurora's
        # mouth in somebody else's call.
        with self.assertRaises(ValueError):
            crypto.decrypt(key, nonce, bytes(sealed), b"rtp")

    def test_the_header_is_authenticated_not_just_carried(self):
        key = crypto.random_key()
        nonce = bytes(range(24))
        sealed = crypto.encrypt(key, nonce, b"audio", b"header-a")

        # The RTP header is the AAD. If it were not authenticated, a packet could be replayed under
        # a different sequence number or attributed to a different stream.
        with self.assertRaises(ValueError):
            crypto.decrypt(key, nonce, sealed, b"header-b")

    def test_a_different_nonce_does_not_decrypt(self):
        key = crypto.random_key()
        sealed = crypto.encrypt(key, bytes(range(24)), b"audio", b"rtp")

        with self.assertRaises(ValueError):
            crypto.decrypt(key, bytes(range(1, 25)), sealed, b"rtp")


if __name__ == "__main__":
    unittest.main(verbosity=2)
