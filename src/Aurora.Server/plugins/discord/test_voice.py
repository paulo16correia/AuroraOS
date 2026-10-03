#!/usr/bin/env python3
"""Tests for the turn-taking rules. Run by the .NET suite so there is one place to look."""

import unittest

from voice_session import VoiceSession, IDLE, LISTENING, SPEAKING

AURORA = "999000999000999000"
PAULO = "444444444444444444"
MARIA = "555555555555555555"


def session():
    s = VoiceSession("g1", "c1", AURORA)
    s.identify_speaker(1, PAULO)
    s.identify_speaker(2, MARIA)
    s.identify_speaker(9, AURORA)
    return s


def kinds(actions):
    return [name for name, _ in actions]


class OwnVoice(unittest.TestCase):
    def test_auroras_own_audio_is_never_input(self):
        s = session()
        actions = s.audio(ssrc=9, at_ms=1000)

        # Without this the system transcribes itself and answers its own sentences.
        self.assertEqual(kinds(actions), ["ignored"])
        self.assertEqual(actions[0][1]["reason"], "own_voice")
        self.assertEqual(s.state, IDLE)

    def test_audio_from_an_unknown_stream_is_not_attributed_to_anybody(self):
        s = session()
        actions = s.audio(ssrc=77, at_ms=1000)

        # An observation naming the wrong speaker is worse than one that never happened.
        self.assertEqual(actions[0][1]["reason"], "unknown_speaker")


class TurnTaking(unittest.TestCase):
    def test_silence_ends_a_turn_and_produces_one_utterance(self):
        s = session()
        for offset in range(0, 1000, 20):
            s.audio(1, 1000 + offset)

        self.assertEqual(kinds(s.tick(1980)), [])
        ended = s.tick(3000)

        self.assertEqual(kinds(ended), ["utterance_ended"])
        self.assertEqual(ended[0][1]["user_id"], PAULO)
        self.assertGreaterEqual(ended[0][1]["duration_ms"], 900)

    def test_the_same_turn_is_not_reported_twice(self):
        s = session()
        for offset in range(0, 1000, 20):
            s.audio(1, 1000 + offset)

        self.assertEqual(kinds(s.tick(3000)), ["utterance_ended"])
        self.assertEqual(kinds(s.tick(4000)), [])

    def test_a_cough_is_not_a_sentence(self):
        s = session()
        s.audio(1, 1000)
        s.audio(1, 1100)

        actions = s.tick(2500)
        self.assertEqual(kinds(actions), ["discarded"])
        self.assertEqual(actions[0][1]["reason"], "too_short")

    def test_two_people_talking_are_two_turns(self):
        s = session()
        for offset in range(0, 1000, 20):
            s.audio(1, 1000 + offset)
            s.audio(2, 1000 + offset)

        ended = s.tick(3000)
        self.assertEqual(len(ended), 2)
        self.assertEqual({a[1]["user_id"] for a in ended}, {PAULO, MARIA})


class Interruption(unittest.TestCase):
    def test_somebody_speaking_stops_aurora_mid_sentence(self):
        s = session()
        speech = s.begin_speaking()
        self.assertIsNotNone(speech)
        self.assertEqual(s.state, SPEAKING)

        actions = s.audio(1, 5000)

        # A system that finishes its sentence while being interrupted is not in a conversation.
        self.assertIn("stop_speaking", kinds(actions))
        self.assertEqual(s.state, LISTENING)
        self.assertEqual(s.last_stop_reason, "interrupted")

    def test_finishing_a_speech_that_was_interrupted_is_refused(self):
        s = session()
        speech = s.begin_speaking()
        s.audio(1, 5000)

        # Recording this as finished would say Aurora delivered a whole sentence it was cut off in.
        self.assertFalse(s.finished_speaking(speech))

    def test_aurora_does_not_start_talking_over_somebody(self):
        s = session()
        s.audio(1, 1000)

        self.assertFalse(s.may_speak())
        self.assertIsNone(s.begin_speaking())

    def test_aurora_may_speak_once_the_floor_is_free(self):
        s = session()
        s.audio(1, 1000)
        s.audio(1, 1400)
        s.tick(3000)

        self.assertTrue(s.may_speak())
        self.assertIsNotNone(s.begin_speaking())


class Participants(unittest.TestCase):
    def test_somebody_leaving_takes_their_unfinished_turn_with_them(self):
        s = session()
        s.audio(1, 1000)
        s.participant_left(PAULO)

        self.assertEqual(kinds(s.tick(3000)), [])
        self.assertNotIn(PAULO, s.participants)

    def test_speaking_identifies_a_participant(self):
        s = VoiceSession("g1", "c1", AURORA)
        s.identify_speaker(1, PAULO)

        self.assertEqual(s.participants, [PAULO])

    def test_aurora_is_not_one_of_the_participants(self):
        s = session()
        self.assertNotIn(AURORA, s.participants)


if __name__ == "__main__":
    unittest.main(verbosity=2)
