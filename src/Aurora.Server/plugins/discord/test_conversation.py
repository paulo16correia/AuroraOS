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

    def test_a_misheard_name_still_counts(self):
        c = group()

        # A name is the word speech recognition gets wrong most — a proper noun, usually absent
        # from the vocabulary. "Aurora" came back as "aurra" from a real call. Requiring it exactly
        # means the one word that must be recognised is the one least likely to be.
        # "aura" is from a real channel on 2026-10-06 — "Aura o Ericar de São Paulo" — and it is two
        # edits, not one. The rule used to stop at one and so could not match the example written
        # beside it.
        for misheard in ("aurra", "auroa", "aurorra", "auror", "aura"):
            self.assertTrue(c.heard(PAULO, "%s, tudo bem?" % misheard, 10000)["named"], misheard)

    def test_a_word_that_merely_resembles_the_name_does_not_count(self):
        c = group()

        # Two edits are allowed now, but only with the first three letters intact — and that prefix
        # is what keeps this list safe. "agora" is one of the commonest words in Portuguese and is
        # two edits from "aurora"; without the prefix rule Aurora would answer every time somebody
        # said "now". "amora" is two edits and starts "amo".
        #
        # "aurea" left this list when the tolerance widened: it keeps the prefix and is two edits,
        # so it matches now. That is a real cost, taken deliberately — see the test below. A rare
        # word occasionally catching her attention is a smaller failure than her never answering to
        # her own name, which is what the narrower rule produced in a real channel.
        for other in ("agora", "aurora boreal is nice", "amora", "ahora", "arara"):
            heard = c.heard(PAULO, other, 10000)
            if other.startswith("aurora"):
                continue
            self.assertFalse(heard["named"], other)

    def test_a_name_buried_inside_a_longer_word_is_a_coincidence(self):
        c = group()

        # Whole words only. A name inside a longer one is not an address.
        self.assertFalse(c.heard(PAULO, "the auroraborealis display", 10000)["named"])

    def test_the_tolerance_costs_the_occasional_false_positive(self):
        """Stated rather than hidden, because it is a trade and both sides are real.

        One edit of tolerance means real words a letter away from the name count as an address.
        "Aurorae" is the plural of aurora and would get an answer it did not ask for.

        The other side is worse. Without tolerance the first real thing anybody said in a call —
        "Aurora, estás a ouvir?" — came back from the recogniser as "Aura a hora" and went
        unanswered, because the one word that had to be recognised is the one a speech model is
        least equipped to get right. Answering once when nobody asked is a smaller failure than
        never answering when they did.
        """
        c = group()
        self.assertTrue(c.heard(PAULO, "we saw the aurorae last night", 10000)["named"])

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


class BeingTalkedTo(unittest.TestCase):
    """Whether she can tell she is being spoken to without being named every sentence.

    The owner's complaint, in their words: "tenho que tar sempre a dizer Aurora para ela
    responder". Two causes, and the first was an ordering: `just_spoke` was checked before
    `invited`, so for eight seconds after every answer she refused anything without her name in
    it — and a reply in a real conversation arrives inside eight seconds. The rule exists to stop
    her inserting herself where she was not wanted; applied to a reply to somebody already talking
    to her, it did the opposite of what it was written for.
    """

    AURORA = "999000999000999000"
    PAULO = "444444444444444444"
    MARIA = "555555555555555555"
    JOAO = "666666666666666666"

    def _exchange(self, present, lines):
        """Plays an exchange through, with Aurora speaking whenever she decides to."""
        talk = Conversation("Aurora", self.AURORA)
        at = 10000
        said = []

        for who, words in lines:
            at += 3000
            decision = talk.heard(who, words, at, present=present)
            said.append(decision)

            if decision["speak"]:
                # She answers, which is what used to close the door on the next sentence.
                talk.spoke(at + 900, invited=True)

        return said

    def test_the_name_is_said_once_and_the_conversation_carries_on(self):
        spoke = self._exchange([self.PAULO, self.AURORA], [
            (self.PAULO, "Aurora, estás a ouvir?"),
            (self.PAULO, "E de onde é que tu és?"),
            (self.PAULO, "Pois, eu moro perto da Ericeira."),
            (self.PAULO, "Achas que vale a pena ir amanhã?"),
        ])

        self.assertEqual("addressed", spoke[0]["reason"])
        self.assertTrue(all(turn["speak"] for turn in spoke))

        # Named once, at the start, and never again.
        self.assertTrue(spoke[0]["named"])
        self.assertFalse(any(turn["named"] for turn in spoke[1:]))

    def test_a_grunt_is_still_not_an_opening(self):
        # The exchange staying open is not a licence to answer everything inside it.
        spoke = self._exchange([self.PAULO, self.AURORA], [
            (self.PAULO, "Aurora, estás a ouvir?"),
            (self.PAULO, "sim"),
        ])

        self.assertTrue(spoke[0]["speak"])
        self.assertFalse(spoke[1]["speak"])
        self.assertEqual("backchannel", spoke[1]["reason"])

    def test_she_stays_out_of_a_question_asked_of_somebody_else(self):
        # Measured before the roster was passed in: she answered both of Maria's turns, and both
        # times Maria was talking to João.
        spoke = self._exchange([self.PAULO, self.MARIA, self.JOAO, self.AURORA], [
            (self.MARIA, "Então João, viste o jogo ontem?"),
            (self.JOAO, "Vi, foi horrível."),
        ])

        self.assertFalse(any(turn["speak"] for turn in spoke))

    def test_an_exchange_belongs_to_whoever_opened_it(self):
        # In a room, being mid-conversation with one person says nothing about the next person to
        # speak. Keyed by speaker for exactly this: a single "when was I last addressed" would
        # hand Aurora every sentence anybody said for the next forty-five seconds.
        room = [self.PAULO, self.MARIA, self.JOAO, self.AURORA]
        talk = Conversation("Aurora", self.AURORA)

        asked = talk.heard(self.PAULO, "Aurora, que horas são?", 10000, present=room)
        self.assertTrue(asked["speak"])
        talk.spoke(11000, invited=True)

        # Paulo carries on and is answered; Maria was never in this exchange and is not.
        carried = talk.heard(self.PAULO, "E amanhã vai chover?", 14000, present=room)
        self.assertTrue(carried["speak"])
        self.assertEqual("invited", carried["reason"])

        other = talk.heard(self.MARIA, "João, passas-me o link?", 17000, present=room)
        self.assertFalse(other["speak"])

    def test_who_is_in_the_room_beats_who_has_spoken(self):
        # Without the roster, how many people are here is guessed from who has spoken lately, and
        # the first speaker in a conversation is always exactly one person. That is how she decided
        # she was alone with Maria.
        room = [self.PAULO, self.MARIA, self.JOAO, self.AURORA]

        # One each. Asking the same instance twice would have the first answer open an exchange
        # that the second call then continues — which is true of a real mistake too: once she has
        # wrongly answered somebody, she keeps answering them. All the more reason not to make the
        # first one.
        alone = Conversation("Aurora", self.AURORA).heard(
            self.MARIA, "Então João, viste o jogo?", 10000)
        crowded = Conversation("Aurora", self.AURORA).heard(
            self.MARIA, "Então João, viste o jogo?", 10000, present=room)

        self.assertEqual(2, alone["people_present"])
        self.assertEqual(4, crowded["people_present"])
        self.assertFalse(crowded["speak"])

    def test_somebody_who_leaves_is_no_longer_mid_conversation(self):
        talk = Conversation("Aurora", self.AURORA)

        talk.heard(self.PAULO, "Aurora, estás aí?", 10000, present=[self.PAULO, self.AURORA])
        talk.left(self.PAULO)

        # Coming back starts a new conversation, which is what it means between people too.
        self.assertEqual(0, talk.snapshot()["in_exchange_with"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
