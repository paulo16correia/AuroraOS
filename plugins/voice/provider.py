"""The telephone provider, behind an interface that is not Twilio's.

Aurora's domain says `VoiceChannel.Phone` and knows nothing about who carries the call. This is the
seam where that becomes somebody's API, and everything provider-shaped lives on this side of it:
their signature scheme, their webhook payloads, their identifiers, their idea of what a call is.

Two implementations ship: Twilio, and a fake that behaves like a provider on a bad day. Tests use
the second, which is why none of them need credentials.

**Everything arriving from a provider is untrusted.** A webhook is an HTTP request from the public
internet that claims to be from Twilio. It is a claim until the signature says otherwise, and even
then its contents are somebody's speech and somebody's caller ID rather than instructions.
"""

import base64
import hashlib
import hmac
import time

# Refusal codes crossing the pipe into Aurora. Stable strings rather than prose.
E_SIGNATURE = "voice_bad_signature"
E_SCHEMA = "voice_bad_payload"
E_REPLAY = "voice_replayed"
E_PROVIDER = "voice_provider_failed"
E_UNSUPPORTED = "voice_unsupported"

# How old a provider event may be and still be acted on. Long enough for an ordinary retry after a
# network hiccup, short enough that a captured request is not usable tomorrow.
MAX_EVENT_AGE_SECONDS = 300


class ProviderRefused(Exception):
    """A provider event Aurora will not act on, and why."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


class WebhookGuard:
    """Decides whether a provider event is real, before anything reads what it says.

    Three questions in order, and the order matters: is it signed by somebody holding the token, is
    it recent, and have we seen it before. Checking the payload first would mean parsing
    attacker-controlled JSON on every request that reaches the port.
    """

    def __init__(self, auth_token, now=None, max_age=MAX_EVENT_AGE_SECONDS):
        self._token = auth_token
        self._now = now or time.time
        self._max_age = max_age

        # Event identifiers already acted on. Bounded, because an unbounded set is a way to spend
        # a process's memory by sending it events.
        self._seen = {}

    def check(self, url, form, signature, event_id, timestamp=None):
        """Validates one webhook. Raises rather than returning a boolean, so it cannot be ignored."""
        if not self._token:
            # No token means no way to tell a real event from a forged one. Refusing is the only
            # honest answer; accepting unsigned events "until it is configured" is how an endpoint
            # spends its first week accepting anything.
            raise ProviderRefused(
                E_SIGNATURE, "no provider auth token is configured, so nothing can be validated")

        if not signature or not verify_twilio_signature(self._token, url, form, signature):
            raise ProviderRefused(E_SIGNATURE, "the signature does not match")

        if timestamp is not None:
            age = self._now() - _as_epoch(timestamp)

            if age > self._max_age or age < -60:
                # Both directions. Too old is a captured request being replayed; too far in the
                # future is a clock nobody should be trusting.
                raise ProviderRefused(E_REPLAY, "the event is outside the window Aurora accepts")

        if not event_id:
            raise ProviderRefused(E_SCHEMA, "the event carries no identifier to deduplicate on")

        self._forget_old()

        if event_id in self._seen:
            raise ProviderRefused(E_REPLAY, "this event has already been handled")

        self._seen[event_id] = self._now()

    def _forget_old(self):
        cutoff = self._now() - (self._max_age * 2)
        self._seen = {k: v for k, v in self._seen.items() if v > cutoff}


def verify_twilio_signature(auth_token, url, form, signature):
    """Twilio's scheme: HMAC-SHA1 over the URL with the sorted form fields appended.

    Written out rather than taken from a library because the plugin has no dependencies, and
    because this is the one function whose correctness decides whether an endpoint on the public
    internet can be talked into anything.
    """
    expected = sign_twilio(auth_token, url, form)

    # Constant time. A comparison that returns early tells an attacker how much of a forged
    # signature was right, one byte at a time.
    return hmac.compare_digest(expected, signature)


def _as_epoch(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def parse_call_event(form):
    """Reads a provider's call webhook into the fields Aurora uses, and refuses anything else.

    A allowlist of fields rather than passing the form through. A provider may add fields whenever
    it likes and an attacker may add any field at all; what crosses into Aurora is the set named
    here, bounded, and nothing more.
    """
    if not isinstance(form, dict):
        raise ProviderRefused(E_SCHEMA, "the event is not a form")

    call_sid = _field(form, "CallSid", 64)
    status = _field(form, "CallStatus", 40)

    if not call_sid:
        raise ProviderRefused(E_SCHEMA, "the event names no call")

    return {
        "external_ref": call_sid,
        "status": status,

        # Caller ID. Named "claimed" because that is what it is: the public telephone network will
        # carry whatever the originating carrier says, and Aurora treats it as an identifier to
        # look somebody up by rather than as evidence of who they are.
        "claimed_from": _field(form, "From", 32),
        "to": _field(form, "To", 32),
        "direction": _field(form, "Direction", 32),
        "account": _field(form, "AccountSid", 64),
    }


def _field(form, name, limit):
    value = form.get(name)

    if value is None:
        return None

    return str(value)[:limit]


def e164(number):
    """A telephone number in the one format Aurora stores, or a refusal.

    E.164 throughout, so that +351 911 111 111 and +351911111111 are the same participant rather
    than two. Portugal is the first target and nothing here is specific to it.
    """
    if not isinstance(number, str):
        raise ProviderRefused(E_SCHEMA, "that is not a telephone number")

    cleaned = "".join(c for c in number if c.isdigit() or c == "+")

    if not cleaned.startswith("+") or not 8 <= len(cleaned) <= 16:
        raise ProviderRefused(
            E_SCHEMA, "a number must be E.164, like +351911111111")

    return cleaned


class FakePhoneProvider:
    """A provider that answers deterministically, including badly.

    Stands where Twilio would. Records what it was asked, so a test can tell "Aurora refused" from
    "Aurora asked and the provider said no" — which look the same from the caller's side.
    """

    def __init__(self, auth_token="test-token"):
        self.auth_token = auth_token
        self.placed = []
        self.ended = []
        self.next_failure = None

    def place_call(self, to, from_number, session_id):
        if self.next_failure:
            failure, self.next_failure = self.next_failure, None
            raise ProviderRefused(E_PROVIDER, failure)

        self.placed.append({"to": to, "from": from_number, "session_id": session_id})

        return {"external_ref": "CA-fake-%d" % len(self.placed), "status": "queued"}

    def end_call(self, external_ref):
        self.ended.append(external_ref)
        return {"external_ref": external_ref, "status": "completed"}

    def sign(self, url, form):
        """A signature the guard will accept, so tests exercise the real check rather than a stub.

        Uses the same computation the verifier does. A fake that signed differently would let a
        broken verifier pass its own tests.
        """
        return sign_twilio(self.auth_token, url, form)


def sign_twilio(auth_token, url, form):
    """The signature Twilio would send. One implementation, used to make and to check."""
    payload = url

    for key in sorted(form):
        payload += key + str(form[key])

    return base64.b64encode(
        hmac.new(auth_token.encode("utf-8"), payload.encode("utf-8"), hashlib.sha1).digest()
    ).decode("ascii")
