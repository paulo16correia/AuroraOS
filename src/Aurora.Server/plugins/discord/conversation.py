"""Whether to say anything at all.

This is the difference between something that is in a conversation and something that is answering
queries in a room where other people happen to be. A system that replies to every sentence it hears
is unmistakable within about thirty seconds, and no amount of good phrasing fixes it — the tell is
not what it says, it is that it says something every time.

So most of what is here decides to stay quiet. The rules are the ones people follow without
noticing:

* Being named is an invitation. Almost nothing else is.
* A question asked into a group is not a question asked of you, unless nobody else is there.
* Somebody who just answered has taken the turn; saying the same thing after them is worse than
  silence.
* Having just spoken makes the next contribution less welcome, not more.
* "yeah", "mhm" and "haha" are not openings.
* Answering instantly is its own kind of wrong. People leave a gap.

Nothing here writes a reply. It decides whether one is wanted, and how long to wait before it would
be natural — the words are Aurora's business, and this is the part that keeps Aurora from being the
person at the party who talks over everybody.
"""

import random
import re

# Things people say that carry no invitation to respond, however friendly they are.
BACKCHANNEL = {
    "yeah", "yep", "yes", "no", "nope", "ok", "okay", "sure", "right", "mhm", "mm",
    "hm", "hmm", "ah", "oh", "haha", "lol", "lmao", "nice", "cool", "true", "exactly",
    "sim", "não", "nao", "pois", "claro", "boa", "certo", "tá", "ta", "fixe", "pá", "pa",
}

QUESTION = re.compile(r"\?\s*$")

# Asked of the room, not of anybody. Answering these is how something makes itself the centre.
OPEN_TO_THE_ROOM = re.compile(
    r"^(does anyone|has anyone|can anyone|anybody|alguém|alguem|does someone)\b", re.I)


def _within_one_edit(word, name):
    """Whether one insertion, deletion or substitution turns one into the other."""
    if abs(len(word) - len(name)) > 1:
        return False

    if word == name:
        return True

    shorter, longer = sorted((word, name), key=len)
    at = 0

    while at < len(shorter) and shorter[at] == longer[at]:
        at += 1

    if len(shorter) == len(longer):
        # A substitution: everything after the first difference must match.
        return shorter[at + 1:] == longer[at + 1:]

    # An insertion or deletion: the rest of the shorter word matches from one further along.
    return shorter[at:] == longer[at + 1:]


class Conversation:
    """What Aurora knows about the room, and what it decides to do about it."""

    def __init__(self, own_name, own_user_id, quiet_for_ms=8000, monologue_limit=2):
        self.own_name = (own_name or "").lower()
        self.own_user_id = own_user_id

        # How long after speaking Aurora treats itself as having recently had the floor.
        self._quiet_for_ms = quiet_for_ms

        # How many times in a row Aurora may contribute before it needs to be invited again.
        self._monologue_limit = monologue_limit

        self._last_spoke_ms = None
        self._unprompted_in_a_row = 0
        self._last_addressed_ms = None

        # user id -> when they last said something. Used only to tell a two-person conversation
        # from a group, which changes what counts as being spoken to.
        self._recent = {}

    # ---- what happened ----

    def heard(self, user_id, text, at_ms):
        """Records an utterance and decides what, if anything, it asks of Aurora."""
        self._recent[user_id] = at_ms

        words = (text or "").strip()
        lowered = words.lower()

        named = self._named_in(lowered)
        question = bool(QUESTION.search(words))
        to_the_room = bool(OPEN_TO_THE_ROOM.match(words))
        trivial = self._is_backchannel(lowered)

        others = self._others_present(at_ms)

        reason = self._decide(
            named=named, question=question, to_the_room=to_the_room,
            trivial=trivial, others=others, at_ms=at_ms)

        speak = reason == "addressed" or reason == "invited"

        if named or speak:
            self._last_addressed_ms = at_ms

        return {
            "speak": speak,
            "reason": reason,
            "named": named,
            "question": question,
            "addressed_to_room": to_the_room,
            "people_present": others + 1,

            # A person leaves a gap. Answering the instant somebody stops is uncanny in a way
            # that has nothing to do with what is said.
            "delay_ms": self._delay_for(reason, question, named) if speak else None,
        }

    def spoke(self, at_ms, invited):
        """Aurora said something. Invited means somebody had asked for it."""
        self._last_spoke_ms = at_ms

        if invited:
            self._unprompted_in_a_row = 0
        else:
            self._unprompted_in_a_row += 1

    def someone_answered(self, user_id, at_ms):
        """Somebody else took the turn, so whatever Aurora was going to say is late."""
        self._recent[user_id] = at_ms

    def left(self, user_id):
        self._recent.pop(user_id, None)

    # ---- the decision ----

    def _decide(self, named, question, to_the_room, trivial, others, at_ms):
        if trivial and not named:
            # "yeah" is not an opening, however warmly it is meant.
            return "backchannel"

        if named:
            # Being named is an invitation, and nearly the only reliable one.
            return "addressed"

        if self._just_spoke(at_ms):
            # Having just had the floor makes the next contribution less welcome, not more.
            return "just_spoke"

        if self._unprompted_in_a_row >= self._monologue_limit:
            # Enough. Somebody has to want the next one.
            return "monologuing"

        if others <= 1 and question:
            # One other person, and they asked something. There is nobody else it could be for.
            return "invited"

        if others <= 1 and not trivial and self._recently_addressed(at_ms):
            # A two-person exchange already under way: the next sentence is still part of it.
            return "invited"

        if to_the_room:
            # Asked of everybody, which means asked of nobody in particular. Answering these is
            # how something makes itself the centre of a conversation it was a guest in.
            return "open_to_the_room"

        return "not_for_me"

    def _delay_for(self, reason, question, named):
        """How long to wait, in milliseconds, before it would be natural to start.

        Not a constant. A person's reply time varies with how much thinking the answer needed, and
        a fixed pause is as recognisable as no pause at all.
        """
        if reason == "addressed" and not question:
            base = 350          # acknowledging something takes no thought
        elif named:
            base = 600
        else:
            base = 900          # joining in uninvited deserves a beat of hesitation

        return base + random.randint(0, 400)

    # ---- what it knows about the room ----

    def _named_in(self, lowered):
        """Whether Aurora was named, allowing for the recogniser mishearing it.

        A name is the word speech recognition gets wrong most: it is a proper noun, usually absent
        from the language model's vocabulary, and it arrives mangled. "Aurora" came back as "Aura"
        in a real call. Matching it exactly means the one word that must be recognised is the one
        least likely to be.

        So a near miss counts — one edit on a name of five letters or more. Not two: at two edits
        a six-letter name starts matching ordinary words, and something that answers to words that
        merely rhyme with its name is worse than something slightly deaf.
        """
        if not self.own_name:
            return False

        if re.search(r"\b%s\b" % re.escape(self.own_name), lowered) is not None:
            return True

        if len(self.own_name) < 5:
            # Too short for a tolerance that would not match half the dictionary.
            return False

        return any(
            _within_one_edit(word, self.own_name)
            for word in re.findall(r"[^\W\d_]+", lowered, re.UNICODE))

    @staticmethod
    def _is_backchannel(lowered):
        stripped = re.sub(r"[^\w\s]", "", lowered).strip()
        return bool(stripped) and stripped in BACKCHANNEL

    def _others_present(self, at_ms, window_ms=120000):
        return sum(
            1 for user_id, when in self._recent.items()
            if user_id != self.own_user_id and at_ms - when <= window_ms)

    def _just_spoke(self, at_ms):
        return (
            self._last_spoke_ms is not None
            and at_ms - self._last_spoke_ms < self._quiet_for_ms)

    def _recently_addressed(self, at_ms, window_ms=45000):
        return (
            self._last_addressed_ms is not None
            and at_ms - self._last_addressed_ms <= window_ms)

    def snapshot(self):
        return {
            "unprompted_in_a_row": self._unprompted_in_a_row,
            "people_recently_speaking": len(self._recent),
        }
