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


def _edits(word, name, ceiling):
    """How many edits turn one into the other, giving up once past `ceiling`.

    Ordinary Levenshtein, bounded. Bounded because the answer is only ever compared against a small
    number and the words are short, and because giving up early says what is meant: past the ceiling
    the distance stops being interesting.
    """
    if abs(len(word) - len(name)) > ceiling:
        return ceiling + 1

    previous = list(range(len(name) + 1))

    for i, letter in enumerate(word, start=1):
        row = [i]

        for j, other in enumerate(name, start=1):
            row.append(min(
                previous[j] + 1,
                row[j - 1] + 1,
                previous[j - 1] + (letter != other)))

        if min(row) > ceiling:
            return ceiling + 1

        previous = row

    return previous[-1]


# How much of the start has to survive before two edits are forgiven.
#
# Three letters, and the number is doing real work. At two edits and no prefix rule, "aurora" matches
# "agora" — one of the commonest words in Portuguese — and Aurora would answer every time somebody
# said "now". With it, "agora" starts "ago" and is not considered at all, while "aura" starts "aur"
# and is. Speech recognition mangles the ends of proper nouns far more than the beginnings, which is
# why the start is the part worth trusting.
SURVIVING_PREFIX = 3


def _is_near_miss(word, name):
    """Whether this word is the name, misheard.

    One edit on a name of five letters or more, which is the old rule. Or two, when the first three
    letters are intact — added because the old rule could not match the example its own comment
    gave: "Aurora" to "Aura" is two edits, and that is exactly what came back from a real channel.
    """
    if len(name) < 5:
        # Too short for a tolerance that would not match half the dictionary.
        return False

    if _edits(word, name, 1) <= 1:
        return True

    return (
        len(name) >= 6
        and len(word) >= SURVIVING_PREFIX
        and word[:SURVIVING_PREFIX] == name[:SURVIVING_PREFIX]
        and _edits(word, name, 2) <= 2)


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

        So a near miss counts — one edit on a name of five letters or more, or two when the first
        three letters survive. The prefix is what makes the second safe: at two edits and nothing
        else, "aurora" matches "agora", and something that answers every time somebody says "now" is
        worse than something slightly deaf. Recognisers mangle the ends of proper nouns far more
        than the beginnings.

        This rule used to stop at one edit, and could not match the example written beside it:
        "Aurora" to "Aura" is two. A real channel produced "Aura o Ericar de São Paulo" and she sat
        there.
        """
        if not self.own_name:
            return False

        if re.search(r"\b%s\b" % re.escape(self.own_name), lowered) is not None:
            return True

        return any(
            _is_near_miss(word, self.own_name)
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
