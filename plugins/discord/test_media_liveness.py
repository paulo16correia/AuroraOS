#!/usr/bin/env python3
"""Whether audio is still arriving is its own question (docs/adr/0082).

Aurora sat in a voice channel for two hours with a dead media socket. The gateway stayed
connected, participant events kept arriving, `in_call` stayed true and `e2ee_ready` stayed true
the whole time — and not one of those four is evidence that a packet ever came back. These tests
pin the difference, and pin it at the boundary: the states, the threshold, and the lifecycle of
the watcher that reports them.

Detection only. Reconnecting is a separate decision and is deliberately not made here or tested
here.
"""

import threading
import time
import unittest

import discord_service
import voice_transport as vt


def transport(observing=True):
    made = vt.VoiceTransport("wss://voice.example.invalid", "g1", "u1", "session", "token",
                             channel_id="c1")
    if observing:
        made._listening.set()
    return made


class Policy(unittest.TestCase):
    """The rule itself, with no socket and no clock of its own."""

    def test_recent_media_is_healthy(self):
        self.assertEqual(
            vt.MEDIA_HEALTHY,
            vt.media_liveness(last_media_ms=10_000, now_ms=11_000,
                              participants=["a"], observing=True, timeout_seconds=45))

    def test_prolonged_silence_with_people_present_is_dead(self):
        # The incident: people in the channel, something draining the socket, nothing arriving.
        self.assertEqual(
            vt.MEDIA_DEAD,
            vt.media_liveness(last_media_ms=0, now_ms=46_000,
                              participants=["a", "b"], observing=True, timeout_seconds=45))

    def test_the_half_way_mark_is_suspect_not_a_verdict(self):
        self.assertEqual(
            vt.MEDIA_SUSPECT,
            vt.media_liveness(last_media_ms=0, now_ms=30_000,
                              participants=["a"], observing=True, timeout_seconds=45))

    def test_an_empty_channel_is_not_a_dead_one(self):
        # Nobody to send anything, so silence is exactly what a working path produces. Calling
        # this dead would report a failure every time a call empties out.
        self.assertEqual(
            vt.MEDIA_UNOBSERVED,
            vt.media_liveness(last_media_ms=0, now_ms=10_000_000,
                              participants=[], observing=True, timeout_seconds=45))

    def test_not_draining_the_socket_is_not_evidence_of_anything(self):
        # Aurora in a call but not listening: no packet would be counted even over a perfect path,
        # so silence says nothing and the answer must not be a verdict.
        self.assertEqual(
            vt.MEDIA_UNOBSERVED,
            vt.media_liveness(last_media_ms=0, now_ms=10_000_000,
                              participants=["a"], observing=False, timeout_seconds=45))

    def test_the_threshold_is_the_boundary_it_says_it_is(self):
        just_under = vt.media_liveness(0, 44_999, ["a"], True, timeout_seconds=45)
        exactly = vt.media_liveness(0, 45_000, ["a"], True, timeout_seconds=45)

        self.assertEqual(vt.MEDIA_SUSPECT, just_under)
        self.assertEqual(vt.MEDIA_DEAD, exactly)

    def test_the_threshold_is_configurable(self):
        # Ten seconds of quiet is dead under a five second timeout and healthy under a hundred.
        self.assertEqual(vt.MEDIA_DEAD, vt.media_liveness(0, 10_000, ["a"], True, timeout_seconds=5))
        self.assertEqual(
            vt.MEDIA_HEALTHY, vt.media_liveness(0, 10_000, ["a"], True, timeout_seconds=100))

    def test_traffic_returning_recovers_the_state(self):
        dead = vt.media_liveness(0, 60_000, ["a"], True, timeout_seconds=45)
        self.assertEqual(vt.MEDIA_DEAD, dead)

        # One packet arrives at 60s. The path is healthy again from that moment, not after a
        # cooldown: evidence that it carries packets is evidence, whenever it turns up.
        self.assertEqual(
            vt.MEDIA_HEALTHY,
            vt.media_liveness(60_000, 60_100, ["a"], True, timeout_seconds=45))


class WhatCountsAsMedia(unittest.TestCase):
    """Driven through the real receive loop, with a socket that is not real."""

    class Socket:
        """Hands the loop a scripted sequence, then times out forever."""

        def __init__(self, packets):
            self.packets = list(packets)

        def settimeout(self, _seconds):
            pass

        def recvfrom(self, _size):
            if self.packets:
                return self.packets.pop(0), ("127.0.0.1", 50000)
            raise TimeoutError("nothing more")

    @staticmethod
    def rtcp():
        # Payload type 201, a receiver report: what Discord keeps sending through a call where
        # nobody is speaking.
        return b"\x80\xc9" + b"\x00" * 30

    @staticmethod
    def rtp():
        return vt.rtp_header(sequence=1, timestamp=0, ssrc=111) + b"\x00" * 40

    def drive(self, packets):
        made = transport()
        made._udp = self.Socket(packets)
        made._stop.clear()

        # One pass of the real loop, ended by the timeout the fake socket raises after the script.
        runner = threading.Thread(target=made._receive_loop, daemon=True)
        runner.start()
        time.sleep(0.3)
        made._listening.clear()
        runner.join(timeout=2)

        return made

    def test_rtp_keeps_the_path_alive(self):
        made = self.drive([self.rtp()])

        self.assertIsNotNone(made.last_media_ms)
        self.assertEqual(1, made.received)
        self.assertEqual(0, made.control)

    def test_rtcp_alone_keeps_the_path_alive(self):
        # The half that matters most. A call where nobody speaks produces no RTP at all, and a
        # liveness rule that only counted RTP would call every quiet call a failure.
        made = self.drive([self.rtcp()])

        self.assertIsNotNone(made.last_media_ms)
        self.assertEqual(1, made.control)

    def test_both_together_keep_it_alive(self):
        made = self.drive([self.rtp(), self.rtcp(), self.rtp()])

        self.assertIsNotNone(made.last_media_ms)
        self.assertEqual(3, made.received)
        self.assertEqual(1, made.control)


class NotEvidence(unittest.TestCase):
    """The signals that stayed true for two hours and meant nothing."""

    class Dave:
        ready = True

    def test_e2ee_ready_does_not_keep_the_media_path_alive(self):
        made = transport()
        made._dave = self.Dave()
        made.observing_since_ms = time.monotonic() * 1000 - 600_000

        self.assertTrue(made.counters()["e2ee_ready"], "the exact state during the incident")
        self.assertEqual(vt.MEDIA_DEAD, made.liveness(participants=["a", "b"], timeout_seconds=45))

    def test_participants_from_the_gateway_do_not_keep_it_alive(self):
        # Participant events kept arriving all night over the main gateway. They say who is in the
        # channel; they say nothing about whether audio flows.
        made = transport()
        made.observing_since_ms = time.monotonic() * 1000 - 600_000

        self.assertEqual(
            vt.MEDIA_DEAD,
            made.liveness(participants=["a", "b", "c", "d", "e", "f", "g"], timeout_seconds=45))


class Watcher(unittest.TestCase):
    """Its lifecycle: one per session, cancelled with the session, holding nothing open."""

    def setUp(self):
        self.started = []
        self.daemons = []
        self._thread = discord_service.threading.Thread
        outer = self

        class Recorded:
            def __init__(self, target=None, args=(), daemon=None):
                outer.started.append(target)
                outer.daemons.append(daemon)

            def start(self):
                pass

        discord_service.threading.Thread = Recorded

    def tearDown(self):
        discord_service.threading.Thread = self._thread

    def test_a_session_gets_one_watcher_not_two(self):
        state = {}

        discord_service._start_media_watch(state)
        discord_service._start_media_watch(state)
        discord_service._start_media_watch(state)

        self.assertEqual(1, len(self.started))

    def test_stopping_clears_the_guard_so_a_later_session_can_watch_again(self):
        state = {}

        discord_service._start_media_watch(state)
        discord_service._stop_media_watch(state)

        self.assertNotIn("voice_media_watch", state)

        discord_service._start_media_watch(state)
        self.assertEqual(2, len(self.started))

    def test_the_watcher_is_a_daemon_so_it_cannot_hold_shutdown_open(self):
        discord_service._start_media_watch({})

        self.assertEqual([True], self.daemons)


class WatcherAgainstATornDownSession(unittest.TestCase):
    """The real loop, run against a session that disappears under it."""

    def test_it_stops_rather_than_reading_a_transport_that_is_gone(self):
        state = {"voice_media_watch": True, "voice": object(), "voice_transport": None}

        runner = threading.Thread(target=discord_service._watch_media, args=(state,), daemon=True)
        runner.start()
        runner.join(timeout=5)

        self.assertFalse(runner.is_alive(), "a watcher must not outlive what it watches")

    def test_leaving_cancels_it(self):
        # voice_leave clears the flag before the transport is closed, which is the cancellation.
        state = {"voice_media_watch": True, "voice": None, "voice_transport": None}

        runner = threading.Thread(target=discord_service._watch_media, args=(state,), daemon=True)
        runner.start()
        discord_service._stop_media_watch(state)
        runner.join(timeout=5)

        self.assertFalse(runner.is_alive())


class Status(unittest.TestCase):
    """What an operator reads."""

    class Session:
        guild_id = "g1"
        channel_id = "c1"

        def snapshot(self):
            return {"guild_id": "g1", "channel_id": "c1", "participants": ["a", "b"],
                    "state": "listening", "speaking_now": []}

    def setUp(self):
        self._readiness = discord_service.voice_engines.readiness
        discord_service.voice_engines.readiness = lambda voice=None: {
            "can_join": True, "can_speak": True, "can_listen": True, "missing": []}

    def tearDown(self):
        discord_service.voice_engines.readiness = self._readiness

    def status_for(self, quiet_seconds):
        made = transport()
        made.observing_since_ms = time.monotonic() * 1000 - quiet_seconds * 1000
        state = {"voice": self.Session(), "voice_transport": made, "voice_listening": True}

        return discord_service.voice_status(state, {})

    def test_a_dead_media_path_shows_as_dead(self):
        answered = self.status_for(quiet_seconds=600)

        self.assertEqual(vt.MEDIA_DEAD, answered["media"])

        # And the older signals still read as they did, which is the whole point of a new field.
        self.assertTrue(answered["in_call"])

    def test_a_wobbling_media_path_shows_as_suspect(self):
        self.assertEqual(vt.MEDIA_SUSPECT, self.status_for(quiet_seconds=30)["media"])

    def test_a_healthy_one_shows_as_healthy(self):
        self.assertEqual(vt.MEDIA_HEALTHY, self.status_for(quiet_seconds=1)["media"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
