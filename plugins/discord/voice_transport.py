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
import dave
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

# Discord's end-to-end encryption. The transitions are JSON; the key material is binary.
DAVE_PREPARE_TRANSITION = 21
DAVE_EXECUTE_TRANSITION = 22
DAVE_TRANSITION_READY = 23
DAVE_PREPARE_EPOCH = 24
MLS_EXTERNAL_SENDER = 25
MLS_KEY_PACKAGE = 26
MLS_PROPOSALS = 27
MLS_COMMIT_WELCOME = 28
MLS_ANNOUNCE_COMMIT_TRANSITION = 29
MLS_WELCOME = 30

MODE = "aead_xchacha20_poly1305_rtpsize"

# RTP: version 2, no padding, no extension, no CSRCs; payload type 120 is Opus for Discord.
RTP_VERSION_FLAGS = 0x80
RTP_PAYLOAD_TYPE = 0x78
RTP_HEADER_BYTES = 12

FRAME_INTERVAL = opus_codec.FRAME_MS / 1000.0


def rtp_header_length(packet):
    """How much of this packet is header, which is what "rtpsize" means.

    The twelve-byte fixed header is a floor, not the answer. Contributing sources add four bytes
    each, and an extension adds a four-byte prefix plus its own length in words. The AAD for
    `aead_xchacha20_poly1305_rtpsize` is exactly this run of bytes, so treating it as always twelve
    authenticates the wrong thing and every packet fails to decrypt — silently, because a failed
    tag looks the same as a forged packet.
    """
    if len(packet) < RTP_HEADER_BYTES:
        return None

    length = RTP_HEADER_BYTES + 4 * (packet[0] & 0x0F)

    if packet[0] & 0x10:
        if len(packet) < length + 4:
            return None

        (words,) = struct.unpack_from(">H", packet, length + 2)
        length += 4 + 4 * words

    return length if len(packet) > length else None


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

    def __init__(self, endpoint, guild_id, user_id, session_id, token, on_audio=None,
                 channel_id=None):
        self._endpoint = endpoint
        self._guild_id = guild_id
        self._user_id = user_id
        self._session_id = session_id
        self._token = token
        self._on_audio = on_audio
        self._channel_id = channel_id or guild_id

        self._socket = None
        self._udp = None
        self._sending = threading.Lock()
        self._stop = threading.Event()

        self._ssrc = None
        self._key = None
        self._server = None

        self._sequence = 0
        self._timestamp = 0

        # What v8 wants echoed back in every heartbeat. Minus one until Discord numbers something.
        self._seq_ack = -1
        self._nonce_counter = 0

        self._encoder = None
        self._decoders = {}

        # The end-to-end session, when the call is encrypted. None means the audio is protected by
        # the transport key alone, which is what Discord allows where DAVE is not required.
        self._dave = None
        self._dave_version = 0
        self._dave_pending = {}

        # ssrc -> user id, learned from the voice gateway's SPEAKING frames. End-to-end decryption
        # needs to know whose audio a packet is: the group key is per-member, and a packet that
        # cannot be attributed cannot be decrypted.
        self._speakers = {}
        self._playing = threading.Event()
        self._current_speech = None

        # The listening half. Started only when somebody asks Aurora to listen, because receiving
        # what people say is not something to do by default for being in the room.
        self._listening = threading.Event()
        self._receiver = None

        # What actually arrived, counted. "Aurora heard nothing" has three different causes —
        # no packets, packets it cannot attribute, packets it cannot decrypt — and they need
        # different fixes. Without counting them they look identical from outside.
        self.received = 0
        self.malformed = 0
        self.unauthenticated = 0
        self.unattributed = 0
        self.undecryptable = 0
        self.decoded = 0

        self.state = "disconnected"
        self.detail = None

        # What it did, in order. A voice connection is four handshakes in a row and knowing which
        # one it reached is the difference between a diagnosis and a guess.
        self.trail = []

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
                # The trail here too. It was only on the timeout path, so a connection that failed
                # outright reported the error with no account of where it had got to — which is
                # the half that says which step to look at.
                raise WebSocketError(
                    "%s | after %s" % (
                        self.detail or "the voice connection failed",
                        " -> ".join(self.trail[-3:]) or "nothing"))
            time.sleep(0.05)

        # What it was doing when the time ran out, which is the whole diagnosis. "It timed out"
        # names the symptom and hides which of the four steps — websocket, identify, UDP discovery,
        # session description — never finished.
        stalled = "%s | state=%s detail=%s" % (
            " -> ".join(self.trail) or "nothing", self.state, self.detail or "none")
        self.stop()

        # The trail first. The record that carries this is truncated, and what matters is where it
        # stopped rather than the sentence around it.
        raise TimeoutError("%s | not ready in %ds" % (stalled, timeout))

    def _step(self, name):
        self.state = name
        self.trail.append(name)

    def _run(self):
        try:
            self._step("starting")
            self._connect()
            self.trail.append("connect returned")
        except Exception as broken:
            self.state = "failed"

            # The message and the line it came from. The voice token travels in the SELECT_PROTOCOL
            # payload, not in socket errors, so withholding either hides the cause and protects
            # nothing — and a bare exception type in a four-step handshake says almost nothing
            # about which step raised it.
            import traceback

            where = traceback.extract_tb(broken.__traceback__)
            spot = "%s:%d" % (where[-1].name, where[-1].lineno) if where else "unknown"

            self.detail = "%s at %s: %s" % (type(broken).__name__, spot, str(broken)[:160])
        finally:
            self._teardown()

    def _connect(self):
        if not self._endpoint:
            # Discord sends a null endpoint while it is moving the voice server. Connecting to
            # "wss://None" produces a DNS error that reads like the network being broken.
            raise WebSocketError(
                "Discord has not said which voice server to use yet")

        # The endpoint, port and all. Discord answers VOICE_SERVER_UPDATE with something like
        # "c-mad01-b6770b7b.discord.media:2087", and that port is not decoration. Port 443 on the
        # same host accepts a websocket handshake and is not the voice server for this session, so
        # it takes the identify and refuses it with 4006 — "session is no longer valid", which is
        # true, and points at the credentials rather than at the address they were sent to.
        #
        # Splitting the port off cost an afternoon of reading correct code. It was the first line
        # of the reference implementation's log.
        host = self._endpoint

        if host.startswith("wss://"):
            host = host[6:]

        url = "wss://%s/?v=8" % host
        self.trail.append("endpoint=%s" % host)
        self.state = "connecting"

        socket_ = WebSocket(url, timeout=20, headers=gateway.HEADERS)

        with self._sending:
            self._socket = socket_

        # Identify is sent when HELLO arrives, not before it. The main gateway tolerates being
        # spoken to first; the voice gateway answers nothing at all, which looks exactly like a
        # connection that opened and then died.
        # Identify at once, before HELLO. This is what Discord's own client does, and the voice
        # gateway is not the main one: it answers a connection that waits with silence.
        self.trail.append(
            "identify(server=%s user=%s session=%s token=%s)" % (
                "set" if self._guild_id else "MISSING",
                "set" if self._user_id else "MISSING",
                "set" if self._session_id else "MISSING",
                "set" if self._token else "MISSING"))

        self._send({"op": IDENTIFY, "d": {
            "server_id": str(self._guild_id),
            "user_id": str(self._user_id),
            "session_id": self._session_id,
            "token": self._token,

            # Discord's end-to-end voice encryption. Zero means "I do not implement it", which is
            # what its own client sends when the optional library is absent — and the field being
            # absent entirely is not the same statement.
            # What this machine can actually do, asked of the library rather than asserted. Zero
            # is honest and Discord may refuse it with 4017 where a server requires end-to-end
            # encryption; claiming a version without a working library would be worse, because the
            # call would be established and the audio would not be what the people in it were told.
            "max_dave_protocol_version": dave.protocol_version(),
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

            if raw is None and socket_.closed:
                # Why, when it said. A voice close code is the only explanation Discord gives for
                # refusing a session, and discarding it leaves "disconnected" — which is what
                # happened rather than why.
                if socket_.close_code:
                    self.state = "failed"
                    self.detail = "Discord closed the voice socket (%s)%s" % (
                        socket_.close_code,
                        ": " + socket_.close_reason if socket_.close_reason else "")
                    self.trail.append("closed:%s" % socket_.close_code)
                else:
                    self.trail.append("closed:no-code")

                return

            if isinstance(raw, (bytes, bytearray)):
                # Binary, which on this socket is only ever MLS key material. Told apart by type
                # rather than by inspecting the bytes: a websocket says which it sent, and
                # guessing would mean deciding whether a key package happens to look like JSON.
                self.trail.append("bin%s" % (raw[2] if len(raw) > 2 else "?"))
                self._handle_binary(raw)
                raw = None

            if raw:
                frame = json.loads(raw)

                # Opcode numbers only. Which frames arrived, and in what order, is the whole
                # question when a handshake stops halfway and says nothing about why.
                self.trail.append("op%s" % frame.get("op"))

                # v8 numbers the frames it wants acknowledged, and the heartbeat carries the last
                # one back. A heartbeat without it is not an answer to anything.
                self._seq_ack = frame.get("seq", self._seq_ack)

                interval = self._handle(frame, interval) or interval

            if interval and (time.monotonic() - last_beat) * 1000 >= interval:
                self._send({"op": HEARTBEAT, "d": {
                    "t": int(time.time() * 1000),
                    "seq_ack": self._seq_ack,
                }})
                last_beat = time.monotonic()

    def _handle(self, frame, interval):
        op = frame.get("op")
        data = frame.get("d") or {}

        if op == HELLO:
            self._step("heartbeating")

            # Capped at five seconds, whatever Discord names. Its own client does the same, and a
            # voice socket that heartbeats only every thirteen seconds is one Discord stops
            # considering live.
            return min(data.get("heartbeat_interval", 13750), 5000)

        if op == READY:
            self._step("discovering the address")
            self._ssrc = data["ssrc"]
            self._server = (data["ip"], data["port"])

            if MODE not in (data.get("modes") or []):
                raise WebSocketError(
                    "Discord did not offer %s; this build encrypts nothing else" % MODE)

            self._open_udp()
            self._step("waiting for the session key")
            return interval

        if op == SESSION_DESCRIPTION:
            self._key = bytes(data["secret_key"])
            self._encoder = opus_codec.Encoder()

            self._dave_version = data.get("dave_protocol_version", 0) or 0

            if self._dave_version > 0:
                self._begin_dave()
                # Not ready yet: the group key is agreed over the frames that follow, and speaking
                # before it exists would send audio nobody in the call can decrypt.
                self._step("agreeing the group key")
                return interval

            self._step("ready")
            return interval

        if op == SPEAKING:
            # Discord says which stream belongs to whom. Everything downstream — attribution,
            # end-to-end decryption, and not mistaking one person for another — rests on this.
            if data.get("ssrc") and data.get("user_id"):
                self._speakers[data["ssrc"]] = data["user_id"]

            return interval

        if op == DAVE_PREPARE_TRANSITION:
            self._dave_pending[data["transition_id"]] = data["protocol_version"]

            if data["transition_id"] == 0:
                self._execute_transition(0)
            else:
                self._send({"op": DAVE_TRANSITION_READY,
                            "d": {"transition_id": data["transition_id"]}})

            return interval

        if op == DAVE_EXECUTE_TRANSITION:
            self._execute_transition(data["transition_id"])
            return interval

        if op == DAVE_PREPARE_EPOCH:
            if data.get("epoch") == 1:
                self._dave_version = data["protocol_version"]
                self._begin_dave()

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

    # ---- end-to-end encryption ----

    def _begin_dave(self):
        """Starts an MLS session and offers this client's key package to the group."""
        self._dave = dave.session(self._dave_version, self._user_id, self._channel_id)
        self._send_binary(MLS_KEY_PACKAGE, self._dave.get_serialized_key_package())
        self.trail.append("dave%d" % self._dave_version)

    def _execute_transition(self, transition_id):
        if transition_id not in self._dave_pending:
            return

        self._dave_version = self._dave_pending.pop(transition_id)

        if self._dave_version == 0 and self._dave is not None:
            # Downgraded by the group. The session stays, passing frames through unencrypted,
            # because Discord expects a client to keep up rather than drop out.
            self._dave.set_passthrough_mode(True, 10)

        self._check_ready()

    def _check_ready(self):
        """Ready once the group key exists, or once there is no group key to wait for."""
        if self.state == "ready":
            return

        if self._dave_version == 0 or (self._dave is not None and self._dave.ready):
            self._step("ready")

    def _handle_binary(self, message):
        """One MLS frame: two bytes of sequence, one of opcode, then key material."""
        if len(message) < 3 or self._dave is None:
            return

        self._seq_ack = struct.unpack_from(">H", message, 0)[0]
        op = message[2]

        try:
            if op == MLS_EXTERNAL_SENDER:
                self._dave.set_external_sender(message[3:])

            elif op == MLS_PROPOSALS:
                result = self._dave.process_proposals(
                    dave.proposals_operation(append=message[3] == 0), message[4:])

                if dave.is_commit_welcome(result):
                    self._send_binary(
                        MLS_COMMIT_WELCOME,
                        result.commit + result.welcome if result.welcome else result.commit)

            elif op == MLS_ANNOUNCE_COMMIT_TRANSITION:
                transition_id = struct.unpack_from(">H", message, 3)[0]
                self._dave.process_commit(message[5:])
                self._after_transition(transition_id)

            elif op == MLS_WELCOME:
                transition_id = struct.unpack_from(">H", message, 3)[0]
                self._dave.process_welcome(message[5:])
                self._after_transition(transition_id)
        except Exception as rejected:
            # A commit this client cannot process means its view of the group is wrong. Starting
            # the session again is the recovery the protocol offers; carrying on would mean
            # encrypting to a group that has moved on without us.
            self.trail.append("dave-recover:%s" % type(rejected).__name__)
            self._begin_dave()
            return

        self._check_ready()

    def _after_transition(self, transition_id):
        if transition_id != 0:
            self._dave_pending[transition_id] = self._dave_version
            self._send({"op": DAVE_TRANSITION_READY, "d": {"transition_id": transition_id}})

    def _send_binary(self, opcode, payload):
        with self._sending:
            if self._socket is None:
                raise WebSocketError("the voice websocket is closed")
            self._socket.send_bytes(bytes([opcode]) + payload)

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

    # ---- listening ----

    def counters(self):
        """What arrived and what became of it."""
        return {
            "received": self.received,
            "malformed": self.malformed,
            "unauthenticated": self.unauthenticated,
            "unattributed": self.unattributed,
            "undecryptable": self.undecryptable,
            "decoded": self.decoded,
            "speakers": len(self._speakers),
            "e2ee_ready": bool(self._dave is not None and self._dave.ready),
        }

    def speaker_of(self, ssrc):
        """Who this stream belongs to, as the voice gateway reported it."""
        return self._speakers.get(ssrc)

    def listen(self, on_audio):
        """Starts reading what participants say. `on_audio(ssrc, pcm, at_ms)` per packet."""
        if self._receiver and self._receiver.is_alive():
            self._on_audio = on_audio
            return

        self._on_audio = on_audio
        self._listening.set()
        self._receiver = threading.Thread(target=self._receive_loop, daemon=True)
        self._receiver.start()

    def deafen(self):
        """Stops reading. The socket stays open, because Aurora is still in the call."""
        self._listening.clear()

    def _receive_loop(self):
        """Reads the audio Discord sends, decrypts it, decodes it, and hands it on.

        Its own thread because audio arrives every twenty milliseconds whether or not anything is
        ready for it, and a socket nobody drains fills its buffer and starts dropping — which
        sounds like a bad connection rather than like a program that was busy.
        """
        while self._listening.is_set() and not self._stop.is_set():
            udp = self._udp

            if udp is None:
                return

            try:
                udp.settimeout(0.5)
                packet, _ = udp.recvfrom(4096)
            except (TimeoutError, OSError) as quiet:
                if isinstance(quiet, OSError) and not isinstance(quiet, TimeoutError) \
                        and "timed out" not in str(quiet):
                    return
                continue

            self.received += 1

            try:
                heard = self.receive_packet(packet)
            except Exception:
                # One bad packet is one bad packet. Twenty milliseconds of somebody's sentence is
                # not worth ending the session over.
                continue

            if heard is None or self._on_audio is None:
                continue

            ssrc, pcm = heard

            try:
                self._on_audio(ssrc, pcm, int(time.monotonic() * 1000))
            except Exception:
                # Whatever is downstream failing must not stop the audio arriving.
                continue

    def stop(self):
        """Cuts off whatever is playing. Safe to call when nothing is."""
        self._playing.clear()

    def close(self):
        self._stop.set()
        self._playing.clear()
        self._listening.clear()
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

        length = rtp_header_length(packet)

        if length is None:
            self.malformed += 1
            return None

        header = packet[:length]
        (ssrc,) = struct.unpack_from(">I", packet, 8)

        nonce = packet[-4:] + bytes(20)
        body = packet[length:-4]

        try:
            opus_packet = crypto.decrypt(self._key, nonce, body, header)
        except ValueError:
            # A packet that does not authenticate is one somebody else wrote, or one this client
            # framed wrongly. Counted, because those two look identical from here and only the
            # count tells them apart: a handful is the internet, all of them is a bug.
            self.unauthenticated += 1
            return None

        if self._dave is not None and self._dave_version > 0 and self._dave.ready:
            speaker = self._speakers.get(ssrc)

            if speaker is None:
                # Audio from a stream nobody has claimed. It cannot be decrypted, because the group
                # key is per-member, and it must not be guessed at: attributing somebody's words to
                # the wrong person is worse than losing them.
                self.unattributed += 1
                return None

            try:
                # Encrypted twice: once for the transport, which the step above undid, and once for
                # the group. Without this the bytes decode into noise, which sounds like a codec
                # fault and is not one.
                opus_packet = self._dave.decrypt(int(speaker), 0, opus_packet)
            except Exception:
                self.undecryptable += 1
                return None

        if ssrc not in self._decoders:
            # One decoder per speaker, because Opus carries state between frames and mixing two
            # people through one decoder produces artefacts that sound like a bad connection.
            self._decoders[ssrc] = opus_codec.Decoder()

        try:
            pcm = self._decoders[ssrc].decode(opus_packet)
            self.decoded += 1
            return ssrc, pcm
        except RuntimeError:
            return None
