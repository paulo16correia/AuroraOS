"""The leg that actually carries audio: voice gateway v4, UDP, and RTP.

Joining a Discord voice channel is two conversations. The main gateway is told which channel to put
Aurora in, and Discord answers with an endpoint and a token for a *second* websocket — the voice
gateway — which negotiates a UDP flow and hands over the encryption key. Audio then goes as RTP
packets on that UDP socket, one every twenty milliseconds.

Everything here is transport. What may be said, when, and to whom is decided in `voice_session.py`
and by Aurora's kernel before anything reaches this file.
"""

import json
import os
import socket
import struct
import threading
import time

import crypto
import gateway
import opus_codec
from websocket import WebSocket, WebSocketError

IDENTIFY = 0
SELECT_PROTOCOL = 1
READY = 2
HEARTBEAT = 3
SESSION_DESCRIPTION = 4
SPEAKING = 5
HEARTBEAT_ACK = 6
RESUME = 7
HELLO = 8
RESUMED = 9
CLIENT_DISCONNECT = 13

MODE = "aead_xchacha20_poly1305_rtpsize"

# RTP: version 2, no padding, no extension, no CSRCs; payload type 120 is Opus for Discord.
RTP_VERSION_FLAGS = 0x80
RTP_PAYLOAD_TYPE = 0x78
RTP_HEADER_BYTES = 12

FRAME_INTERVAL = opus_codec.FRAME_MS / 1000.0


def rtp_header(sequence, timestamp, ssrc):
    """The twelve bytes in front of every packet, and the bytes the cipher authenticates."""
    return struct.pack(
        ">BBHII", RTP_VERSION_FLAGS, RTP_PAYLOAD_TYPE,
        sequence & 0xFFFF, timestamp & 0xFFFFFFFF, ssrc & 0xFFFFFFFF)


def discovery_packet(ssrc):
    """The 74-byte probe that asks Discord what address it sees us on.

    A machine behind NAT does not know the address and port the outside world will send its audio
    to, and cannot ask its own network stack. Discord answers with what it observed.
    """
    return struct.pack(">HHI", 0x1, 70, ssrc) + bytes(66)


def parse_discovery(answer):
    """The address and port Discord saw, out of its 74-byte reply."""
    if len(answer) < 74:
        raise ValueError("the discovery reply was %d bytes, not 74" % len(answer))

    # The address is null-padded ASCII; the port is the last two bytes, big-endian.
    address = answer[8:72].split(b"\x00", 1)[0].decode()
    (port,) = struct.unpack(">H", answer[72:74])

    return address, port


def nonce_for(counter):
    """XChaCha20 wants 24 bytes; Discord sends 4 and both sides zero-pad the rest.

    The counter is per-packet and never repeats within a session, which is what a nonce has to be.
    Reusing one with the same key would leak the keystream.
    """
    return struct.pack(">I", counter & 0xFFFFFFFF) + bytes(20)


class VoiceTransport:
    """One voice connection: the second websocket, the UDP socket, and the audio going out."""

    def __init__(self, endpoint, guild_id, user_id, session_id, token, on_audio=None):
        self._endpoint = endpoint
        self._guild_id = guild_id
        self._user_id = user_id
        self._session_id = session_id
        self._token = token
        self._on_audio = on_audio

        self._socket = None
        self._udp = None
        self._sending = threading.Lock()
        self._stop = threading.Event()

        self._ssrc = None
        self._key = None
        self._server = None

        self._sequence = 0
        self._timestamp = 0
        self._nonce_counter = 0

        self._encoder = None
        self._decoders = {}
        self._playing = threading.Event()
        self._current_speech = None

        self.state = "disconnected"
        self.detail = None

        self._thread = None

    # ---- connecting ----

    def start(self, timeout=20):
        """Connects, negotiates, and returns once audio can flow. Raises when it cannot."""
        if not opus_codec.available():
            raise opus_codec.OpusUnavailable(
                "libopus is not installed; Discord voice carries Opus and nothing else")

        self._stop.clear()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

        deadline = time.monotonic() + timeout

        while time.monotonic() < deadline:
            if self.state == "ready":
                return True
            if self.state == "failed":
                raise WebSocketError(self.detail or "the voice connection failed")
            time.sleep(0.05)

        self.stop()
        raise TimeoutError("the voice connection did not become ready within %ds" % timeout)

    def _run(self):
        try:
            self._connect()
        except Exception as broken:
            self.state = "failed"
            # The type and a short reason. A voice endpoint URL carries a session token.
            self.detail = "%s" % type(broken).__name__
        finally:
            self._teardown()

    def _connect(self):
        url = "wss://%s?v=4" % self._endpoint.split(":")[0]
        self.state = "connecting"

        socket_ = WebSocket(url, timeout=20, headers=gateway.HEADERS)

        with self._sending:
            self._socket = socket_

        self._send({"op": IDENTIFY, "d": {
            "server_id": self._guild_id,
            "user_id": self._user_id,
            "session_id": self._session_id,
            "token": self._token,
        }})

        interval = None
        last_beat = time.monotonic()

        while not self._stop.is_set():
            socket_._socket.settimeout(1)

            try:
                raw = socket_.receive()
            except (TimeoutError, OSError) as quiet:
                if isinstance(quiet, OSError) and "timed out" not in str(quiet) \
                        and not isinstance(quiet, TimeoutError):
                    raise
                raw = None

            if raw is None and socket_._closed:
                return

            if raw:
                interval = self._handle(json.loads(raw), interval) or interval

            if interval and (time.monotonic() - last_beat) * 1000 >= interval:
                self._send({"op": HEARTBEAT, "d": int(time.time() * 1000)})
                last_beat = time.monotonic()

    def _handle(self, frame, interval):
        op = frame.get("op")
        data = frame.get("d") or {}

        if op == HELLO:
            return data.get("heartbeat_interval", 13750)

        if op == READY:
            self._ssrc = data["ssrc"]
            self._server = (data["ip"], data["port"])

            if MODE not in (data.get("modes") or []):
                raise WebSocketError(
                    "Discord did not offer %s; this build encrypts nothing else" % MODE)

            self._open_udp()
            return interval

        if op == SESSION_DESCRIPTION:
            self._key = bytes(data["secret_key"])
            self._encoder = opus_codec.Encoder()
            self.state = "ready"
            return interval

        return interval

    def _open_udp(self):
        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp.settimeout(5)
        udp.sendto(discovery_packet(self._ssrc), self._server)

        answer, _ = udp.recvfrom(74)
        address, port = parse_discovery(answer)

        self._udp = udp

        self._send({"op": SELECT_PROTOCOL, "d": {
            "protocol": "udp",
            "data": {"address": address, "port": port, "mode": MODE},
        }})

    def _send(self, payload):
        with self._sending:
            if self._socket is None:
                raise WebSocketError("the voice websocket is closed")
            self._socket.send_json(payload)

    # ---- audio out ----

    def speaking(self, is_speaking):
        """Discord shows a ring around whoever is speaking, and mutes streams that never say so."""
        self._send({"op": SPEAKING, "d": {
            "speaking": 1 if is_speaking else 0, "delay": 0, "ssrc": self._ssrc}})

    def play(self, pcm, speech_id=None):
        """Sends 48kHz stereo PCM as Opus, paced in real time. Returns when done or stopped."""
        if self.state != "ready":
            raise RuntimeError("the voice transport is not ready")

        self._current_speech = speech_id
        self._playing.set()
        self.speaking(True)

        try:
            started = time.monotonic()
            frames = 0

            for offset in range(0, len(pcm), opus_codec.BYTES_PER_FRAME):
                if not self._playing.is_set():
                    # Stopped mid-sentence, which is what being interrupted looks like from here.
                    break

                frame = pcm[offset:offset + opus_codec.BYTES_PER_FRAME]

                if len(frame) < opus_codec.BYTES_PER_FRAME:
                    frame += bytes(opus_codec.BYTES_PER_FRAME - len(frame))

                self._send_frame(self._encoder.encode(frame))
                frames += 1

                # Paced against a fixed start rather than sleeping a flat 20ms, so encoding time
                # does not accumulate into audible drift over a long sentence.
                target = started + frames * FRAME_INTERVAL
                delay = target - time.monotonic()

                if delay > 0:
                    time.sleep(delay)

            return frames
        finally:
            self._playing.clear()
            self._current_speech = None

            try:
                self.speaking(False)
            except (WebSocketError, OSError):
                pass

    def _send_frame(self, opus_packet):
        header = rtp_header(self._sequence, self._timestamp, self._ssrc)

        self._nonce_counter += 1
        nonce = nonce_for(self._nonce_counter)

        # The header is the associated data: carried in the clear so Discord can route it, and
        # authenticated so it cannot be altered in flight.
        sealed = crypto.encrypt(self._key, nonce, opus_packet, header)

        # rtpsize: the four significant nonce bytes travel at the end of the packet.
        self._udp.sendto(header + sealed + nonce[:4], self._server)

        self._sequence = (self._sequence + 1) & 0xFFFF
        self._timestamp = (self._timestamp + opus_codec.SAMPLES_PER_FRAME) & 0xFFFFFFFF

    def stop(self):
        """Cuts off whatever is playing. Safe to call when nothing is."""
        self._playing.clear()

    def close(self):
        self._stop.set()
        self._playing.clear()
        self._teardown()

    def _teardown(self):
        with self._sending:
            if self._socket is not None:
                self._socket.close()
                self._socket = None

        if self._udp is not None:
            try:
                self._udp.close()
            except OSError:
                pass
            self._udp = None

        if self._encoder is not None:
            self._encoder.close()
            self._encoder = None

        for decoder in self._decoders.values():
            decoder.close()

        self._decoders.clear()

        if self.state != "failed":
            self.state = "disconnected"

    # ---- audio in ----

    def receive_packet(self, packet):
        """Decrypts and decodes one incoming RTP packet. Returns (ssrc, pcm) or None.

        Separated from the socket so it can be tested: a packet is bytes, and what this does with
        them does not depend on where they came from.
        """
        if len(packet) < RTP_HEADER_BYTES + 4:
            return None

        header = packet[:RTP_HEADER_BYTES]
        (ssrc,) = struct.unpack(">I", header[8:12])

        nonce = packet[-4:] + bytes(20)
        body = packet[RTP_HEADER_BYTES:-4]

        try:
            opus_packet = crypto.decrypt(self._key, nonce, body, header)
        except ValueError:
            # A packet that does not authenticate is one somebody else wrote. Dropped silently:
            # there is nothing to report and nothing to be done about it.
            return None

        if ssrc not in self._decoders:
            # One decoder per speaker, because Opus carries state between frames and mixing two
            # people through one decoder produces artefacts that sound like a bad connection.
            self._decoders[ssrc] = opus_codec.Decoder()

        try:
            return ssrc, self._decoders[ssrc].decode(opus_packet)
        except RuntimeError:
            return None
