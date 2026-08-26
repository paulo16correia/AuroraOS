#!/usr/bin/env python3
"""When to speak, and — mostly — when not to.

Almost every test here asserts silence. That ratio is the point: a system that replies to every
sentence it hears is recognisable within thirty seconds, and no amount of good phrasing fixes it,
because the tell is not what it says but that it says something every time.
"""

import unittest

from conversation import Conversation

AURORA_ID = "999"
PAULO = "111"
MARIA = "222"
JOAO = "333"


def room(**kwargs):
    return Conversation("aurora", AURORA_ID, **kwargs)


def group():
    """Three people already talking, which is the case that matters most."""
    c = room()
    c.heard(PAULO, "so I was saying", 1000)
    c.heard(MARIA, "right", 2000)
    c.heard(JOAO, "and then what", 3000)
    return c


class BeingAddressed(unittest.TestCase):
    def test_being_named_is_an_invitation(self):
        c = group()
        answer = c.heard(PAULO, "aurora, what do you think?", 10000)

        self.assertTrue(answer["speak"])
        self.assertEqual(answer["reason"], "addressed")
        self.assertTrue(answer["named"])

    def test_a_name_inside_another_word_is_a_coincidence(self):
        c = group()
        # "aurorae" is a word. Treating it as being addressed is the kind of thing that makes
        # something feel like it is listening for its name rather than to the conversation.
        self.assertFalse(c.heard(PAULO, "we saw the aurorae last night", 10000)["named"])

    def test_two_people_and_a_question_is_for_aurora(self):
        c = room()
        c.heard(PAULO, "hey", 1000)

        # Nobody else is here. There is no other person the question could be for.
        answer = c.heard(PAULO, "have you seen the new one?", 5000)
        self.assertTrue(answer["speak"])
        self.assertEqual(answer["reason"], "invited")


class StayingQuiet(unittest.TestCase):
    def test_a_question_in_a_group_is_not_for_aurora(self):
        c = group()
        answer = c.heard(PAULO, "what time is it there?", 10000)

        # Three people are talking. Answering this is how something makes itself the centre of a
        # conversation it was a guest in.
        self.assertFalse(answer["speak"])
        self.assertEqual(answer["reason"], "not_for_me")

    def test_asking_the_room_is_asking_nobody(self):
        c = group()
        for opener in ("does anyone know the score?", "alguém sabe as horas?"):
            answer = c.heard(MARIA, opener, 10000)
            self.assertFalse(answer["speak"], opener)
            self.assertEqual(answer["reason"], "open_to_the_room")

    def test_backchannel_is_not_an_opening(self):
        c = room()
        c.heard(PAULO, "hey", 1000)

        for noise in ("yeah", "mhm", "haha", "pois", "fixe", "ok."):
            answer = c.heard(PAULO, noise, 5000)
            self.assertFalse(answer["speak"], noise)
            self.assertEqual(answer["reason"], "backchannel")

    def test_having_just_spoken_makes_the_next_one_less_welcome(self):
        c = room()
        c.heard(PAULO, "hey", 1000)
        c.spoke(2000, invited=True)

        answer = c.heard(PAULO, "and what about tomorrow?", 4000)

        self.assertFalse(answer["speak"])
        self.assertEqual(answer["reason"], "just_spoke")

    def test_being_named_gets_through_anyway(self):
        c = room()
        c.spoke(2000, invited=True)

        # Somebody deliberately asking should never be ignored because of a cooldown.
        answer = c.heard(PAULO, "aurora, sorry — one more thing", 3000)
        self.assertTrue(answer["speak"])

    def test_aurora_stops_after_contributing_uninvited_twice(self):
        c = room(quiet_for_ms=0)
        c.heard(PAULO, "hey", 1000)

        c.spoke(2000, invited=False)
        c.spoke(3000, invited=False)

        answer = c.heard(PAULO, "what do you reckon?", 9000)

        # Enough. Somebody has to want the next one.
        self.assertFalse(answer["speak"])
        self.assertEqual(answer["reason"], "monologuing")

    def test_being_invited_resets_the_count(self):
        c = room(quiet_for_ms=0)
        c.heard(PAULO, "hey", 1000)

        c.spoke(2000, invited=False)
        c.spoke(3000, invited=False)
        c.spoke(4000, invited=True)

        self.assertTrue(c.heard(PAULO, "and then?", 9000)["speak"])


class Timing(unittest.TestCase):
    def test_nothing_answers_instantly(self):
        c = room()
        c.heard(PAULO, "hey", 1000)
        answer = c.heard(PAULO, "aurora what do you think?", 5000)

        # Answering the instant somebody stops talking is uncanny in a way that has nothing to do
        # with what is said.
        self.assertIsNotNone(answer["delay_ms"])
        self.assertGreaterEqual(answer["delay_ms"], 300)

    def test_the_pause_is_not_always_the_same(self):
        c = room(quiet_for_ms=0)
        delays = set()

        for at in range(10000, 40000, 3000):
            answer = c.heard(PAULO, "aurora, thoughts?", at)
            delays.add(answer["delay_ms"])
            c.spoke(at + 500, invited=True)

        # A fixed pause is as recognisable as no pause at all.
        self.assertGreater(len(delays), 1)

    def test_staying_quiet_has_no_delay_to_report(self):
        c = group()
        self.assertIsNone(c.heard(PAULO, "what time is it?", 10000)["delay_ms"])


class TheRoom(unittest.TestCase):
    def test_somebody_leaving_makes_it_a_smaller_room(self):
        c = group()
        c.left(MARIA)
        c.left(JOAO)

        # Now it is two people, and a question has nobody else to be for.
        answer = c.heard(PAULO, "so what do you make of it?", 10000)
        self.assertTrue(answer["speak"])

    def test_someone_answering_counts_as_them_taking_the_turn(self):
        c = room()
        c.heard(PAULO, "hey", 1000)
        c.someone_answered(MARIA, 2000)

        # Two other people are here now, so a plain question is no longer obviously for Aurora.
        answer = c.heard(PAULO, "what time is it?", 3000)
        self.assertFalse(answer["speak"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
