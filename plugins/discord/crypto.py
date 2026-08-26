"""XChaCha20-Poly1305, which is what Discord's voice protocol now requires.

Discord retired the older voice encryption modes and `aead_xchacha20_poly1305_rtpsize` is what is
left. Python's standard library has no AEAD at all, so there are two ways to get one and this file
takes both:

* **libsodium, when it is installed.** Vetted, constant-time, and audited by people who do this for
  a living. Preferred whenever it can be found.
* **RFC 8439 in Python, when it is not.** Written here because the alternative is telling somebody
  their voice call needs a system package they have never heard of.

Hand-written cryptography deserves suspicion, so this one is checked rather than trusted: the RFC's
own test vectors run as part of the suite, and the authentication tag is compared with
`hmac.compare_digest` rather than `==`. What the pure-Python path does not have is resistance to
timing attacks in the cipher itself — Python cannot offer that — which is why libsodium is used
when it is there and recommended when it is not.
"""

import ctypes
import ctypes.util
import hmac
import os
import struct

KEY_BYTES = 32
NONCE_BYTES = 24
TAG_BYTES = 16


# ---------------------------------------------------------------------------
# libsodium, when it is available
# ---------------------------------------------------------------------------


def _load_sodium():
    found = ctypes.util.find_library("sodium")

    for candidate in [found, "libsodium.so.23", "libsodium.so", "libsodium.dylib"]:
        if not candidate:
            continue
        try:
            library = ctypes.CDLL(candidate)
        except OSError:
            continue

        if library.sodium_init() < 0:
            continue

        return library

    return None


_SODIUM = _load_sodium()


def using_libsodium():
    """Whether the vetted implementation is the one in use. Reported, not assumed."""
    return _SODIUM is not None


# ---------------------------------------------------------------------------
# RFC 8439, for when it is not
# ---------------------------------------------------------------------------

_MASK = 0xFFFFFFFF


def _rotate(value, count):
    return ((value << count) | (value >> (32 - count))) & _MASK


def _quarter_round(state, a, b, c, d):
    state[a] = (state[a] + state[b]) & _MASK
    state[d] = _rotate(state[d] ^ state[a], 16)
    state[c] = (state[c] + state[d]) & _MASK
    state[b] = _rotate(state[b] ^ state[c], 12)
    state[a] = (state[a] + state[b]) & _MASK
    state[d] = _rotate(state[d] ^ state[a], 8)
    state[c] = (state[c] + state[d]) & _MASK
    state[b] = _rotate(state[b] ^ state[c], 7)


_CONSTANTS = (0x61707865, 0x3320646E, 0x79622D32, 0x6B206574)


def _chacha_state(key, counter, nonce):
    return [
        *_CONSTANTS,
        *struct.unpack("<8I", key),
        counter,
        *struct.unpack("<3I", nonce),
    ]


def _rounds(state):
    working = list(state)

    for _ in range(10):
        _quarter_round(working, 0, 4, 8, 12)
        _quarter_round(working, 1, 5, 9, 13)
        _quarter_round(working, 2, 6, 10, 14)
        _quarter_round(working, 3, 7, 11, 15)
        _quarter_round(working, 0, 5, 10, 15)
        _quarter_round(working, 1, 6, 11, 12)
        _quarter_round(working, 2, 7, 8, 13)
        _quarter_round(working, 3, 4, 9, 14)

    return working


def chacha20_block(key, counter, nonce):
    """One 64-byte keystream block (RFC 8439 §2.3)."""
    state = _chacha_state(key, counter, nonce)
    working = _rounds(state)

    return struct.pack(
        "<16I", *[(working[i] + state[i]) & _MASK for i in range(16)])


def chacha20(key, counter, nonce, data):
    out = bytearray(len(data))

    for offset in range(0, len(data), 64):
        block = chacha20_block(key, counter + offset // 64, nonce)
        chunk = data[offset:offset + 64]

        for i, byte in enumerate(chunk):
            out[offset + i] = byte ^ block[i]

    return bytes(out)


def hchacha20(key, nonce16):
    """Derives a subkey from a 16-byte nonce (RFC 8439 §2.2 applied as in XChaCha20).

    This is the whole of what makes XChaCha20 different: a 24-byte nonce is split into 16 bytes
    that make a new key and 8 that become the ChaCha20 nonce, which is what allows random nonces
    without worrying about collisions.
    """
    state = [
        *_CONSTANTS,
        *struct.unpack("<8I", key),
        *struct.unpack("<4I", nonce16),
    ]

    working = _rounds(state)

    return struct.pack("<8I", *(working[0:4] + working[12:16]))


_P = (1 << 130) - 5


def poly1305(key, message):
    """The one-time authenticator (RFC 8439 §2.5)."""
    r = int.from_bytes(key[:16], "little") & 0x0FFFFFFC0FFFFFFC0FFFFFFC0FFFFFFF
    s = int.from_bytes(key[16:32], "little")

    accumulator = 0

    for offset in range(0, len(message), 16):
        chunk = message[offset:offset + 16]
        n = int.from_bytes(chunk + b"\x01", "little")
        accumulator = ((accumulator + n) * r) % _P

    return ((accumulator + s) & ((1 << 128) - 1)).to_bytes(16, "little")


def _pad16(data):
    remainder = len(data) % 16
    return b"\x00" * (16 - remainder) if remainder else b""


def _aead_chacha20_poly1305(key, nonce12, plaintext, aad, decrypt_ct=None):
    """RFC 8439 §2.8. Encrypts when `decrypt_ct` is None, otherwise verifies and decrypts."""
    poly_key = chacha20_block(key, 0, nonce12)[:32]

    if decrypt_ct is None:
        ciphertext = chacha20(key, 1, nonce12, plaintext)
    else:
        ciphertext = decrypt_ct

    authenticated = (
        aad + _pad16(aad)
        + ciphertext + _pad16(ciphertext)
        + struct.pack("<Q", len(aad))
        + struct.pack("<Q", len(ciphertext))
    )

    tag = poly1305(poly_key, authenticated)

    if decrypt_ct is None:
        return ciphertext + tag

    return tag, chacha20(key, 1, nonce12, ciphertext)


def _xchacha_parts(key, nonce24):
    subkey = hchacha20(key, nonce24[:16])
    return subkey, b"\x00\x00\x00\x00" + nonce24[16:24]


# ---------------------------------------------------------------------------
# the interface the transport uses
# ---------------------------------------------------------------------------


def encrypt(key, nonce24, plaintext, aad=b""):
    """Encrypts and authenticates. Returns ciphertext with the tag appended."""
    if len(key) != KEY_BYTES:
        raise ValueError("the key must be 32 bytes")
    if len(nonce24) != NONCE_BYTES:
        raise ValueError("the nonce must be 24 bytes")

    if _SODIUM is not None:
        out = ctypes.create_string_buffer(len(plaintext) + TAG_BYTES)
        written = ctypes.c_ulonglong(0)

        result = _SODIUM.crypto_aead_xchacha20poly1305_ietf_encrypt(
            out, ctypes.byref(written),
            plaintext, ctypes.c_ulonglong(len(plaintext)),
            aad, ctypes.c_ulonglong(len(aad)),
            None, nonce24, key)

        if result != 0:
            raise ValueError("libsodium refused to encrypt")

        return out.raw[:written.value]

    subkey, nonce12 = _xchacha_parts(key, nonce24)
    return _aead_chacha20_poly1305(subkey, nonce12, plaintext, aad)


def decrypt(key, nonce24, ciphertext, aad=b""):
    """Verifies and decrypts. Raises ValueError when the tag does not match."""
    if len(ciphertext) < TAG_BYTES:
        raise ValueError("too short to carry a tag")

    if _SODIUM is not None:
        out = ctypes.create_string_buffer(len(ciphertext))
        written = ctypes.c_ulonglong(0)

        result = _SODIUM.crypto_aead_xchacha20poly1305_ietf_decrypt(
            out, ctypes.byref(written), None,
            ciphertext, ctypes.c_ulonglong(len(ciphertext)),
            aad, ctypes.c_ulonglong(len(aad)),
            nonce24, key)

        if result != 0:
            raise ValueError("the packet did not authenticate")

        return out.raw[:written.value]

    subkey, nonce12 = _xchacha_parts(key, nonce24)
    body, tag = ciphertext[:-TAG_BYTES], ciphertext[-TAG_BYTES:]

    expected, plaintext = _aead_chacha20_poly1305(subkey, nonce12, None, aad, decrypt_ct=body)

    # Constant-time, because a comparison that stops at the first wrong byte tells an attacker how
    # much of a forged tag was right.
    if not hmac.compare_digest(expected, tag):
        raise ValueError("the packet did not authenticate")

    return plaintext


def random_key():
    return os.urandom(KEY_BYTES)
