#!/usr/bin/env python3
"""What joining does when Discord says no.

A voice join is retried, and each retry leaves the channel and rejoins to ask for fresh
credentials — which is what Discord's own client does and what a lost session needs (docs/adr/0068).
The cost of that retry is paid in public: everybody in the call watches Aurora appear and disappear
once per attempt. So it has to be spent on refusals that another attempt could actually change,
and these tests are about the difference (docs/adr/0080).

Discord is not here. The gateway is a stand-in that records the voice states it was asked to send,
because the count of those *is* what the people in the call saw.
"""

import unittest

import conversation
import discord_service
import voice_session as vs
import voice_transport as vt


class Gateway:
    """Enough of the main gateway to join with, recording what reached the wire."""

    def __init__(self):
        self.state = "connected"
        self.voice_states = []
        self.voice_trail = ["state", "server"]

    def status(self):
        return {"bot_user_id": "999000999000999000"}

    def voice_state(self, guild_id, channel_id):
        self.voice_states.append(channel_id)

    def await_voice_credentials(self, timeout=10):
        return {"endpoint": "voice.example.invalid", "user_id": "999000999000999000",
                "session_id": "session", "token": "token"}

    @property
    def joins(self):
        """The times Aurora appeared in the channel, which is what the flapping was."""
        return [c for c in self.voice_states if c is not None]


def refused_with(close_code):
    """A transport that fails the way Discord failed it, carrying the code it sent."""

    class Transport:
        def __init__(self, *args, **kwargs):
            pass

        def start(self, timeout=20):
            broken = vt.WebSocketError("Discord closed the voice socket (%s)" % close_code)
            broken.close_code = close_code
            raise broken

    return Transport


class Join(unittest.TestCase):
    def setUp(self):
        self.gateway = Gateway()
        self.state = {"gateway": self.gateway}
        self.args = {"guild_id": "g1", "channel_id": "c1"}

        self._transport = discord_service.VoiceTransport
        self._readiness = discord_service.voice_engines.readiness

        # The backoff between attempts is real and deliberate, and waiting it out here would buy
        # nothing: what these tests count is how many times Aurora appeared in the channel, not
        # how long it left between them.
        self._sleep = discord_service.time.sleep
        discord_service.time.sleep = lambda _seconds: None
        # Takes the voice the caller names, the way the real one does: a stand-in with an older
        # signature turns a change in the code under test into a failure in the double.
        discord_service.voice_engines.readiness = lambda *_, **__: {
            "can_join": True, "can_speak": True, "can_listen": True, "missing": []}

    def tearDown(self):
        discord_service.VoiceTransport = self._transport
        discord_service.voice_engines.readiness = self._readiness
        discord_service.time.sleep = self._sleep

    def join(self):
        with self.assertRaises(discord_service.Refused) as refusal:
            discord_service.voice_join(self.state, self.args)
        return refusal.exception

    def test_a_refusal_discord_will_repeat_is_not_asked_again(self):
        # 4017 is Discord saying the call is end-to-end encrypted and this machine said it cannot
        # take part. Rejoining sends the same identify from the same machine, so the answer is the
        # same one — and the only thing five attempts add is five appearances in somebody's call.
        discord_service.VoiceTransport = refused_with(4017)

        self.join()

        self.assertEqual(
            len(self.gateway.joins), 1,
            "a settled refusal must cost exactly one appearance in the channel")

    def test_a_terminal_refusal_still_leaves_the_channel(self):
        discord_service.VoiceTransport = refused_with(4017)

        self.join()

        # Not retrying must not turn into staying: a join that failed has to leave, or Aurora sits
        # in the call unable to hear or be heard.
        self.assertIsNone(self.gateway.voice_states[-1])
        self.assertNotIn("voice", self.state)

    def test_the_refusal_says_why_it_stopped(self):
        discord_service.VoiceTransport = refused_with(4017)

        detail = str(self.join())

        self.assertIn("not retried", detail)
        self.assertIn("end-to-end encryption", detail)

    def test_a_lost_session_is_still_retried(self):
        # The other half, and the reason the retry exists. 4006 is a session Discord has forgotten;
        # leaving and rejoining is exactly what gets a new one, so this one keeps all five tries.
        discord_service.VoiceTransport = refused_with(4006)

        self.join()

        self.assertEqual(len(self.gateway.joins), 5)

    def test_a_failure_that_never_reached_discord_is_still_retried(self):
        # No close code at all: a timeout, a name that would not resolve, a socket that never
        # opened. Discord has not refused anything, so there is nothing settled to respect.
        class Transport:
            def __init__(self, *args, **kwargs):
                pass

            def start(self, timeout=20):
                raise TimeoutError("nothing came back")

        discord_service.VoiceTransport = Transport

        self.join()

        self.assertEqual(len(self.gateway.joins), 5)


class TerminalCloseCodes(unittest.TestCase):
    """The table itself, read off the exception rather than out of its message."""

    def test_a_settled_refusal_is_named(self):
        broken = vt.WebSocketError("closed")
        broken.close_code = 4017

        self.assertIn("end-to-end encryption", vt.terminal_close_reason(broken))

    def test_the_retryable_codes_are_not_in_the_table(self):
        # 4006 and 4009 are a session Discord has forgotten, 4015 a voice server that fell over.
        # A fresh session is the fix for all three, and the retry is how one is asked for.
        for retryable in (4006, 4009, 4015):
            broken = vt.WebSocketError("closed")
            broken.close_code = retryable

            self.assertIsNone(vt.terminal_close_reason(broken), retryable)

    def test_an_error_carrying_no_code_is_not_settled(self):
        self.assertIsNone(vt.terminal_close_reason(TimeoutError("nothing came back")))

    def test_the_code_reaches_the_caller_as_a_number(self):
        # Matching on the sentence would break the next time the sentence is worded better.
        transport = vt.VoiceTransport(
            "wss://voice.example.invalid", "g1", "u1", "session", "token", channel_id="c1")

        def refuse():
            transport.state = "failed"
            transport.close_code = 4017
            transport.detail = "Discord closed the voice socket (4017)"

        transport._run = refuse

        # start() refuses outright without libopus, which is correct and is not what this test is
        # about: a codec cannot change whether a close code rides on the exception. The real one is
        # put through a full round trip in test_transport.py, where it belongs.
        available = vt.opus_codec.available
        vt.opus_codec.available = lambda: True

        try:
            with self.assertRaises(vt.WebSocketError) as refusal:
                transport.start(timeout=5)
        finally:
            vt.opus_codec.available = available

        self.assertEqual(refusal.exception.close_code, 4017)
        self.assertIsNotNone(vt.terminal_close_reason(refusal.exception))


class OneUtteranceThroughTheRealSession(unittest.TestCase):
    """`utterance_heard` against a real VoiceSession, which nothing exercised before.

    This is the gap two defects have now walked through. `test_conversation` drives the decision
    with a roster it builds itself, and `test_local` fakes the engines; between them sits the line
    that actually asks the session who is here, and nobody was testing it. Twice that line raised:
    `session.transport` after the telephone was removed, and `session.participants()` on what is a
    property. Both times the listening thread died and a working conversation looked exactly like
    an empty room.
    """

    AURORA = "1542272336613343292"
    PAULO = "710960489315827734"

    def setUp(self):
        self.reported = []
        self._real_report = discord_service.report
        discord_service.report = lambda kind, payload: self.reported.append((kind, payload))

    def tearDown(self):
        discord_service.report = self._real_report

    def _state(self):
        session = vs.VoiceSession("g1", "c1", self.AURORA)
        session.participant_joined(self.PAULO)
        session.identify_speaker(1, self.PAULO)

        return {
            "voice": session,
            "conversation": conversation.Conversation("Aurora", self.AURORA),
        }

    def test_a_named_utterance_is_decided_on_and_not_an_exception(self):
        decision = discord_service.utterance_heard(
            self._state(), self.PAULO, "Aurora, estás a ouvir?", 10000)

        self.assertTrue(decision["speak"])
        self.assertTrue(decision["named"])
        self.assertEqual("addressed", decision["reason"])

    def test_the_roster_reaches_the_decision(self):
        # The whole reason that line exists: without the session's roster, how many people are
        # here is guessed from who has spoken, and the first speaker is always exactly one person.
        state = self._state()
        state["voice"].participant_joined("111111111111111111")
        state["voice"].participant_joined("222222222222222222")

        decision = discord_service.utterance_heard(
            state, self.PAULO, "Aurora, estás aí?", 10000)

        # Three others plus Aurora, counted from the channel rather than from who has spoken.
        self.assertEqual(4, decision["people_present"])

    def test_every_utterance_is_reported_even_the_ones_it_stays_quiet_for(self):
        discord_service.utterance_heard(self._state(), self.PAULO, "pois", 10000)

        kinds = [kind for kind, _ in self.reported]
        self.assertIn("voice.heard", kinds)


class WaitingForAGap(unittest.TestCase):
    """A reply waits briefly for somebody to stop, rather than being dropped on the spot.

    `speak_in_conversation` refuses when the floor is taken, which is right — Aurora does not talk
    over people. It was refusing instantly, and in a channel where the microphones carry breathing
    and a fan there is hardly ever a moment when nobody holds the floor. Measured: every answer she
    decided to give was refused, the kernel reported an opaque "Execution failed", and nothing said
    which refusal it had been.
    """

    class Floor:
        """A session that holds the floor for a given number of attempts and then lets go."""

        def __init__(self, busy_for):
            self.busy_for = busy_for
            self.asked = 0

        def begin_speaking(self):
            self.asked += 1

            if self.asked <= self.busy_for:
                return None

            return 7

    def setUp(self):
        self._patience = discord_service.FLOOR_PATIENCE_MS
        discord_service.FLOOR_PATIENCE_MS = 600

    def tearDown(self):
        discord_service.FLOOR_PATIENCE_MS = self._patience

    def test_a_free_floor_is_taken_at_once(self):
        floor = self.Floor(busy_for=0)

        self.assertEqual(7, discord_service._floor_or_wait({}, floor))
        self.assertEqual(1, floor.asked)

    def test_it_waits_for_a_gap_instead_of_giving_up(self):
        # The case that matters. Somebody is mid-word when Aurora is ready to answer; a beat later
        # they stop, and the answer is still worth saying.
        floor = self.Floor(busy_for=2)

        self.assertEqual(7, discord_service._floor_or_wait({}, floor))
        self.assertGreater(floor.asked, 1)

    def test_it_gives_up_if_nobody_ever_stops(self):
        # Still bounded. Waiting indefinitely would hold the capability open and then say something
        # answering a question two minutes old.
        floor = self.Floor(busy_for=10_000)

        self.assertIsNone(discord_service._floor_or_wait({}, floor))

        # And it tried more than once on the way, rather than returning the first refusal.
        self.assertGreater(floor.asked, 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)


class ListeningTwice(unittest.TestCase):
    """Turning listening off and on again has to actually leave Aurora hearing.

    The turn watcher is started behind a one-shot guard and its loop ends when listening stops. If
    the guard outlives the thread, the second enable starts nothing and reports `listening: true`
    over a plugin that can no longer hear anybody.
    """

    class Transport:
        def __init__(self):
            self.deafened = 0

        def listen(self, on_audio):
            pass

        def deafen(self):
            self.deafened += 1

    def setUp(self):
        self.transport = self.Transport()
        self.state = {"voice": object(), "voice_transport": self.transport, "gateway": None}

        self._readiness = discord_service.voice_engines.readiness
        # Takes the voice the caller names, the way the real one does: a stand-in with an older
        # signature turns a change in the code under test into a failure in the double.
        discord_service.voice_engines.readiness = lambda *_, **__: {
            "can_join": True, "can_speak": True, "can_listen": True, "missing": []}

        # The watcher itself is a thread with a recogniser in it; what is under test is whether one
        # is asked for, so the asking is what gets recorded.
        self.started = []
        self._thread = discord_service.threading.Thread
        outer = self

        class Recorded:
            def __init__(self, target=None, args=(), daemon=None):
                outer.started.append(target)

            def start(self):
                pass

        discord_service.threading.Thread = Recorded

    def tearDown(self):
        discord_service.threading.Thread = self._thread
        discord_service.voice_engines.readiness = self._readiness

    def test_listening_again_starts_the_watcher_again(self):
        discord_service.voice_listen(self.state, {"enabled": True})
        self.assertEqual(len(self.started), 1)

        discord_service.voice_listen(self.state, {"enabled": False})
        self.assertFalse(self.state.get("voice_listening"))

        discord_service.voice_listen(self.state, {"enabled": True})

        self.assertEqual(
            len(self.started), 2,
            "the second enable must start a watcher, not inherit a thread that has exited")

    def test_enabling_twice_without_stopping_does_not_start_two(self):
        discord_service.voice_listen(self.state, {"enabled": True})
        discord_service.voice_listen(self.state, {"enabled": True})

        self.assertEqual(len(self.started), 1)
