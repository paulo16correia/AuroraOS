"""Turn-taking in a voice channel, as a state machine with no I/O.

Everything hard about being present in a call is decided here: when Aurora may speak, when it must
stop, whose audio counts as input, and when somebody has finished a sentence. Separated from the
sockets and the codecs on purpose — those are hard to test and this is where being wrong is
expensive, so this file takes events and returns decisions and touches nothing.

The rules it exists to keep:

* Aurora never hears itself. Its own audio is not input, however it arrives.
* Aurora does not talk over people. Somebody starting to speak stops Aurora mid-sentence.
* One utterance produces one observation, even when the packets arrive twice.
* Silence ends a turn. A person who stops talking has finished, and waiting for them to say so
  would mean waiting for ever.
"""

# How long a speaker must be quiet before what they said counts as finished. Long enough to survive
# the pause in the middle of a sentence, short enough not to feel like being ignored.
SILENCE_MS = 900

# Audio shorter than this is a cough, a keyboard, or somebody bumping a desk.
MIN_UTTERANCE_MS = 300

IDLE = "idle"
LISTENING = "listening"
SPEAKING = "speaking"


class VoiceSession:
    """One presence in one voice channel."""

    def __init__(self, guild_id, channel_id, own_user_id, silence_ms=SILENCE_MS,
                 min_utterance_ms=MIN_UTTERANCE_MS):
        self.guild_id = guild_id
        self.channel_id = channel_id
        self.own_user_id = own_user_id

        self._silence_ms = silence_ms
        self._min_utterance_ms = min_utterance_ms

        self.state = IDLE

        # ssrc -> user id. Discord sends audio tagged with an ssrc and tells us separately who it
        # belongs to; audio from an ssrc we have not been told about is audio from somebody we
        # cannot name.
        self._speakers = {}
        self._participants = set()

        # user id -> {"started": ms, "last": ms, "frames": n}
        self._talking = {}

        # Utterances already reported, so a duplicate packet stream does not produce two.
        self._reported = set()

        self._speech_id = 0
        self._stopped_speaking_because = None

    # ---- who is here ----

    def participant_joined(self, user_id):
        if user_id != self.own_user_id:
            self._participants.add(user_id)

    def participant_left(self, user_id):
        self._participants.discard(user_id)
        self._talking.pop(user_id, None)

        for ssrc, owner in list(self._speakers.items()):
            if owner == user_id:
                del self._speakers[ssrc]

    def identify_speaker(self, ssrc, user_id):
        """Discord's SPEAKING event: this stream belongs to this person."""
        self._speakers[ssrc] = user_id

        if user_id != self.own_user_id:
            self._participants.add(user_id)

    @property
    def participants(self):
        return sorted(self._participants)

    # ---- audio arriving ----

    def audio(self, ssrc, at_ms, duration_ms=20):
        """One packet of somebody's audio. Returns the actions Aurora should take.

        Actions are the only output: this never speaks, never sends, never writes. A list so that
        a single packet can both stop Aurora talking and start a turn.
        """
        user_id = self._speakers.get(ssrc)

        if user_id is None:
            # Audio from a stream nobody has claimed. Discarded rather than attributed: an
            # observation that names the wrong speaker is worse than one that never happened.
            return [("ignored", {"reason": "unknown_speaker", "ssrc": ssrc})]

        if user_id == self.own_user_id:
            # Aurora's own voice, arriving back through the channel. Never input. Without this the
            # system transcribes itself and answers its own sentences.
            return [("ignored", {"reason": "own_voice"})]

        actions = []

        if user_id not in self._talking:
            actions.extend(self._someone_started(user_id, at_ms))
            self._talking[user_id] = {"started": at_ms, "last": at_ms, "frames": 1}
        else:
            turn = self._talking[user_id]
            turn["last"] = at_ms
            turn["frames"] += 1

        return actions

    def _someone_started(self, user_id, at_ms):
        actions = [("speaker_started", {"user_id": user_id, "at_ms": at_ms})]

        if self.state == SPEAKING:
            # Barge-in. Somebody talking over Aurora means Aurora stops — a system that finishes
            # its sentence while being interrupted is not in a conversation, it is broadcasting.
            self._stopped_speaking_because = "interrupted"
            self.state = LISTENING
            actions.append(("stop_speaking", {"reason": "interrupted_by", "user_id": user_id}))
        elif self.state == IDLE:
            self.state = LISTENING

        return actions

    def tick(self, at_ms):
        """Time passing. Returns the turns that have ended."""
        actions = []

        for user_id, turn in list(self._talking.items()):
            if at_ms - turn["last"] < self._silence_ms:
                continue

            del self._talking[user_id]

            length = turn["last"] - turn["started"]

            if length < self._min_utterance_ms:
                # A cough, a keystroke, a desk. Transcribing it produces noise that reads like
                # somebody said something.
                actions.append(("discarded", {"user_id": user_id, "reason": "too_short"}))
                continue

            key = (user_id, turn["started"])

            if key in self._reported:
                continue

            self._reported.add(key)

            actions.append(("utterance_ended", {
                "user_id": user_id,
                "started_ms": turn["started"],
                "ended_ms": turn["last"],
                "duration_ms": length,
            }))

        if not self._talking and self.state == LISTENING:
            self.state = IDLE

        return actions

    # ---- Aurora speaking ----

    def may_speak(self):
        """Whether starting to speak now would be talking over somebody."""
        return not self._talking

    def begin_speaking(self):
        """Returns a speech id, or None when somebody else has the floor."""
        if not self.may_speak():
            return None

        self._speech_id += 1
        self.state = SPEAKING
        self._stopped_speaking_because = None
        return self._speech_id

    def finished_speaking(self, speech_id):
        """Aurora reached the end of what it was saying."""
        if self.state == SPEAKING and speech_id == self._speech_id:
            self.state = LISTENING if self._talking else IDLE
            self._stopped_speaking_because = "finished"
            return True

        # An interruption already moved on. Reporting this as finished would record Aurora as
        # having said a whole sentence it was cut off in the middle of.
        return False

    def stop_speaking(self, reason="asked"):
        if self.state != SPEAKING:
            return False

        self._stopped_speaking_because = reason
        self.state = LISTENING if self._talking else IDLE
        return True

    @property
    def last_stop_reason(self):
        return self._stopped_speaking_because

    def snapshot(self):
        return {
            "guild_id": self.guild_id,
            "channel_id": self.channel_id,
            "state": self.state,
            "participants": self.participants,
            "speaking_now": sorted(self._talking),
        }
