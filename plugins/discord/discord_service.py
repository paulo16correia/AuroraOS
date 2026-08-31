#!/usr/bin/env python3
"""Aurora's Discord plugin.

Speaks Aurora's service protocol on stdin/stdout (one JSON object per line) and Discord's REST API
over HTTPS. Nothing else: the standard library only, because a plugin that needs `pip install`
needs a network before it has been granted one, and a dependency is another thing the owner is
agreeing to without being asked.

Aurora governs every call before it arrives here. This file's job is to be a faithful, boring
translator — and to be honest about the one thing Discord cannot tell us, which is whether a
message we did not hear back about was actually sent.
"""

import json
import os
import ssl
import struct
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

import voice_engines
from gateway import Gateway
from voice_transport import VoiceTransport
from conversation import Conversation
from voice_session import VoiceSession

DEFAULT_API = "https://discord.com/api/v10"
USER_AGENT = "DiscordBot (https://github.com/paulo16correia/AuroraOS, 1.0.0)"

# Error codes Aurora's planner can tell apart. A single generic failure would leave it unable to
# decide between "try again", "ask a person", and "this will never work".
E_NO_CREDENTIALS = "missing_credentials"
E_BAD_CREDENTIALS = "invalid_credentials"
E_REVOKED = "revoked_credentials"
E_FORBIDDEN = "discord_permission_denied"
E_NOT_FOUND = "not_found"
E_RATE_LIMITED = "rate_limited"
E_DISCORD = "discord_api_error"
E_TIMEOUT = "timeout"
E_NETWORK = "network_failure"
E_INPUT = "malformed_input"
E_UNSUPPORTED = "unsupported_capability"


class Refused(Exception):
    """A refusal with a code the planner can branch on."""

    def __init__(self, code, detail, retry_after=None):
        super().__init__(detail)
        self.code = code
        self.detail = detail
        self.retry_after = retry_after


class Unknown(Exception):
    """The call reached Discord and we do not know what happened.

    Never converted to a failure. A message may be in the channel; saying it failed invites a
    retry that sends it twice.
    """

    def __init__(self, detail):
        super().__init__(detail)
        self.detail = detail


def api_base(allowed):
    """Where to send requests, checked against the hosts the manifest declared.

    Reads `api_base` from an optional config.json beside this file, so the plugin can be pointed at
    a stand-in during testing without a second code path — the same request building, the same
    error mapping, the same rate-limit handling as production.

    The host must be one the manifest declared and the owner granted. Aurora cannot enforce that
    (neither sandbox-exec nor bubblewrap filters by hostname, see docs/adr/0067), which is exactly
    why the plugin enforces it on itself: it is the only party in the system that knows the
    hostname it is about to resolve.
    """
    here = os.path.dirname(os.path.abspath(__file__))
    chosen = DEFAULT_API

    try:
        with open(os.path.join(here, "config.json"), "r") as handle:
            chosen = (json.load(handle) or {}).get("api_base") or DEFAULT_API
    except (OSError, ValueError):
        pass

    host = urllib.parse.urlparse(chosen).hostname or ""

    if allowed and host not in allowed:
        raise Refused(
            E_INPUT,
            "config.json points at '%s', which the manifest does not declare" % host)

    return chosen


class Discord:
    """The REST client. One socket policy, one error model, one rate-limit rule."""

    def __init__(self, token, base):
        self._token = token
        self._base = base
        self._context = ssl.create_default_context()

        # Earliest time each route may be called again, learned from Discord's own headers rather
        # than guessed. Keyed by bucket so a slow channel does not throttle every other.
        self._buckets = {}

    def _headers(self):
        return {
            "Authorization": "Bot " + self._token,
            "User-Agent": USER_AGENT,
            "Content-Type": "application/json",
        }

    def request(self, method, path, body=None, query=None, timeout=10, bucket=None):
        url = self._base + path
        if query:
            clean = {k: v for k, v in query.items() if v is not None}
            if clean:
                url += "?" + urllib.parse.urlencode(clean)

        bucket = bucket or path
        wait = self._buckets.get(bucket, 0) - time.monotonic()
        if wait > 0:
            # Discord already told us this route is exhausted. Sleeping here is the plugin's own
            # thread and never Aurora's: the Kernel is not waiting on this, the call is.
            if wait > 5:
                raise Refused(
                    E_RATE_LIMITED,
                    "the route is rate limited for another %.0fs" % wait,
                    retry_after=wait,
                )
            time.sleep(wait)

        if not url.startswith("https://") and urllib.parse.urlparse(url).hostname not in (
                "127.0.0.1", "localhost", "::1"):
            raise Refused(E_INPUT, "refusing to send a bot token over an unencrypted connection")

        payload = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(url, data=payload, method=method)
        for name, value in self._headers().items():
            request.add_header(name, value)

        try:
            with urllib.request.urlopen(request, timeout=timeout, context=self._context) as answer:
                self._remember(bucket, answer.headers)
                raw = answer.read()
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as failure:
            self._remember(bucket, failure.headers)
            raise self._interpret(failure) from None
        except TimeoutError:
            # A read that timed out did not necessarily fail to happen. The caller decides what
            # that means for the operation it was performing.
            raise Unknown("no answer from Discord within %ds" % timeout) from None
        except urllib.error.URLError as broken:
            reason = getattr(broken, "reason", broken)
            if isinstance(reason, TimeoutError):
                raise Unknown("the connection to Discord timed out") from None
            raise Refused(E_NETWORK, "could not reach Discord: %s" % type(reason).__name__) from None

    def _remember(self, bucket, headers):
        remaining = headers.get("X-RateLimit-Remaining")
        reset_after = headers.get("X-RateLimit-Reset-After")
        if remaining == "0" and reset_after:
            try:
                self._buckets[bucket] = time.monotonic() + float(reset_after)
            except ValueError:
                pass

    def _interpret(self, failure):
        status = failure.code
        try:
            detail = json.loads(failure.read() or b"{}")
        except ValueError:
            detail = {}

        # Discord's own message, not ours, and truncated: it is written by a service outside this
        # machine and travels into Aurora's records.
        said = str(detail.get("message", ""))[:200]

        if status == 401:
            return Refused(E_BAD_CREDENTIALS, "Discord rejected the bot token")
        if status == 403:
            return Refused(E_FORBIDDEN, "the bot lacks the Discord permission for this: " + said)
        if status == 404:
            return Refused(E_NOT_FOUND, said or "no such guild, channel or message")
        if status == 429:
            retry = 1.0
            try:
                retry = float(detail.get("retry_after", failure.headers.get("Retry-After", 1)))
            except (TypeError, ValueError):
                pass
            return Refused(
                E_RATE_LIMITED, "Discord is rate limiting this route", retry_after=retry
            )
        if 500 <= status < 600:
            # Discord had the request. Whether it acted on it is exactly what nobody knows.
            return Unknown("Discord returned %d" % status)
        return Refused(E_DISCORD, "Discord returned %d: %s" % (status, said))


# ---------------------------------------------------------------------------
# capabilities
# ---------------------------------------------------------------------------


def _channel_kind(raw):
    return {0: "text", 2: "voice", 4: "category", 5: "announcement",
            11: "public_thread", 12: "private_thread", 13: "stage"}.get(raw, str(raw))


def _message(raw):
    """The parts of a Discord message Aurora has a use for, and nothing else."""
    author = raw.get("author") or {}
    return {
        "message_id": raw.get("id"),
        "channel_id": raw.get("channel_id"),
        "author_id": author.get("id"),
        "author_name": author.get("username"),
        "author_is_bot": bool(author.get("bot", False)),
        "content": raw.get("content", ""),
        "timestamp": raw.get("timestamp"),
        "reply_to": (raw.get("referenced_message") or {}).get("id"),
        "nonce": raw.get("nonce"),
    }


def guilds_list(api, _):
    return {"guilds": [
        {"guild_id": g.get("id"), "name": g.get("name")}
        for g in api.request("GET", "/users/@me/guilds")
    ]}


def guilds_get(api, args):
    raw = api.request("GET", "/guilds/%s" % args["guild_id"])
    return {"guild_id": raw.get("id"), "name": raw.get("name"),
            "owner_id": raw.get("owner_id"), "description": raw.get("description")}


def channels_list(api, args):
    return {"channels": [
        {"channel_id": c.get("id"), "name": c.get("name"),
         "type": _channel_kind(c.get("type")), "parent_id": c.get("parent_id")}
        for c in api.request("GET", "/guilds/%s/channels" % args["guild_id"])
    ]}


def channels_get(api, args):
    raw = api.request("GET", "/channels/%s" % args["channel_id"])
    return {"channel_id": raw.get("id"), "name": raw.get("name"),
            "type": _channel_kind(raw.get("type")), "topic": raw.get("topic"),
            "guild_id": raw.get("guild_id")}


def messages_list(api, args):
    raw = api.request(
        "GET", "/channels/%s/messages" % args["channel_id"],
        query={"limit": args.get("limit", 50),
               "before": args.get("before"), "after": args.get("after")},
        bucket="messages:" + args["channel_id"])
    return {"messages": [_message(m) for m in raw]}


def messages_get(api, args):
    return _message(api.request(
        "GET", "/channels/%s/messages/%s" % (args["channel_id"], args["message_id"])))


def members_list(api, args):
    raw = api.request("GET", "/guilds/%s/members" % args["guild_id"],
                      query={"limit": args.get("limit", 100)})
    return {"members": [
        {"user_id": (m.get("user") or {}).get("id"),
         "name": (m.get("user") or {}).get("username"),
         "nick": m.get("nick"),
         "is_bot": bool((m.get("user") or {}).get("bot", False))}
        for m in raw
    ]}


def users_get(api, args):
    raw = api.request("GET", "/users/%s" % args["user_id"])
    return {"user_id": raw.get("id"), "name": raw.get("username"),
            "global_name": raw.get("global_name"), "is_bot": bool(raw.get("bot", False))}


def threads_list(api, args):
    raw = api.request(
        "GET", "/channels/%s/threads/archived/public" % args["channel_id"],
        query={"limit": 50})
    return {"threads": [
        {"thread_id": t.get("id"), "name": t.get("name"),
         "archived": bool((t.get("thread_metadata") or {}).get("archived", False))}
        for t in raw.get("threads", [])
    ]}


def threads_get(api, args):
    raw = api.request("GET", "/channels/%s" % args["thread_id"])
    meta = raw.get("thread_metadata") or {}
    return {"thread_id": raw.get("id"), "name": raw.get("name"),
            "archived": bool(meta.get("archived", False)),
            "locked": bool(meta.get("locked", False)),
            "parent_id": raw.get("parent_id")}


def _send(api, args, nonce, reference=None):
    """Posts a message, and can tell afterwards whether it landed.

    Discord has no idempotency key for message creation. It does echo back a `nonce`, so a send
    that gets no answer can be resolved by reading the channel and looking for our own nonce —
    which is the difference between retrying safely and posting twice.
    """
    body = {"content": args["content"]}
    if nonce:
        body["nonce"] = str(nonce)[:25]
    if reference:
        body["message_reference"] = {"message_id": reference, "fail_if_not_exists": False}

    try:
        return _message(api.request(
            "POST", "/channels/%s/messages" % args["channel_id"], body=body,
            bucket="messages:" + args["channel_id"]))
    except Unknown as unsure:
        if not nonce:
            raise

        found = _find_by_nonce(api, args["channel_id"], nonce)
        if found is not None:
            # It did land. Reporting the failure would have had Aurora send it again.
            return found
        raise Unknown(
            "%s; the channel does not show it, but Discord may still be processing it"
            % unsure.detail) from None


def _find_by_nonce(api, channel_id, nonce):
    try:
        recent = api.request(
            "GET", "/channels/%s/messages" % channel_id, query={"limit": 20},
            bucket="messages:" + channel_id)
    except (Refused, Unknown):
        return None

    for raw in recent:
        if str(raw.get("nonce") or "") == str(nonce)[:25]:
            return _message(raw)
    return None


def messages_send(api, args, nonce=None):
    return _send(api, args, nonce)


def messages_reply(api, args, nonce=None):
    return _send(api, args, nonce, reference=args["message_id"])


def messages_edit(api, args, nonce=None):
    before = api.request(
        "GET", "/channels/%s/messages/%s" % (args["channel_id"], args["message_id"]))

    raw = api.request(
        "PATCH", "/channels/%s/messages/%s" % (args["channel_id"], args["message_id"]),
        body={"content": args["content"]})

    result = _message(raw)
    # What it said before, so the caller has what it needs to put it back. That is what
    # `reversible` claims in the manifest, and the claim has to be paid for here.
    result["previous_content"] = before.get("content", "")
    return result


def messages_delete(api, args, nonce=None):
    api.request(
        "DELETE", "/channels/%s/messages/%s" % (args["channel_id"], args["message_id"]))
    return {"message_id": args["message_id"], "deleted": True}


def _emoji(raw):
    return urllib.parse.quote(raw, safe="")


def reactions_add(api, args, nonce=None):
    api.request(
        "PUT", "/channels/%s/messages/%s/reactions/%s/@me"
        % (args["channel_id"], args["message_id"], _emoji(args["emoji"])))
    return {"message_id": args["message_id"], "emoji": args["emoji"], "added": True}


def reactions_remove(api, args, nonce=None):
    api.request(
        "DELETE", "/channels/%s/messages/%s/reactions/%s/@me"
        % (args["channel_id"], args["message_id"], _emoji(args["emoji"])))
    return {"message_id": args["message_id"], "emoji": args["emoji"], "removed": True}


def threads_create(api, args, nonce=None):
    if args.get("message_id"):
        path = "/channels/%s/messages/%s/threads" % (args["channel_id"], args["message_id"])
        body = {"name": args["name"]}
    else:
        path = "/channels/%s/threads" % args["channel_id"]
        body = {"name": args["name"], "type": 11}

    raw = api.request("POST", path, body=body)
    return {"thread_id": raw.get("id"), "name": raw.get("name"),
            "parent_id": raw.get("parent_id")}


def channels_create(api, args, nonce=None):
    body = {"name": args["name"], "type": 2 if args.get("type") == "voice" else 0}
    if args.get("parent_id"):
        body["parent_id"] = args["parent_id"]

    raw = api.request("POST", "/guilds/%s/channels" % args["guild_id"], body=body)
    # The id is what makes this reversible: whoever asked for it can delete it.
    return {"channel_id": raw.get("id"), "name": raw.get("name"),
            "type": _channel_kind(raw.get("type")), "guild_id": args["guild_id"]}


def channels_edit(api, args, nonce=None):
    before = api.request("GET", "/channels/%s" % args["channel_id"])

    body = {}
    if args.get("name"):
        body["name"] = args["name"]
    if args.get("topic") is not None:
        body["topic"] = args["topic"]
    if not body:
        raise Refused(E_INPUT, "nothing to change: give a name or a topic")

    raw = api.request("PATCH", "/channels/%s" % args["channel_id"], body=body)
    return {"channel_id": raw.get("id"), "name": raw.get("name"), "topic": raw.get("topic"),
            "previous": {"name": before.get("name"), "topic": before.get("topic")}}


def threads_archive(api, args, nonce=None):
    before = api.request("GET", "/channels/%s" % args["thread_id"])
    meta = before.get("thread_metadata") or {}

    raw = api.request(
        "PATCH", "/channels/%s" % args["thread_id"],
        body={"archived": True, "locked": bool(args.get("locked", False))})

    result = raw.get("thread_metadata") or {}
    return {"thread_id": args["thread_id"], "archived": bool(result.get("archived", True)),
            "locked": bool(result.get("locked", False)),
            "previous": {"archived": bool(meta.get("archived", False)),
                         "locked": bool(meta.get("locked", False))}}


# ---------------------------------------------------------------------------
# voice
# ---------------------------------------------------------------------------

E_VOICE_UNAVAILABLE = "voice_unavailable"
E_NOT_IN_CALL = "not_in_a_call"


def voice_pending(state, args):
    """What Aurora decided to answer and has not answered.

    Aurora has no language model, deliberately: understanding belongs to the client that speaks to
    it (RFC 045, docs/adr/0051). So the loop cannot close inside this process — what closes it is
    something with language reading this, deciding what to say, and calling speak.

    Read-only and cheap, because whatever is holding the conversation will ask often.
    """
    waiting = state.get("voice_pending") or []

    return {
        "waiting": list(waiting),
        "conversing": conversation_window(state) is not None,
        "utterances_left": (conversation_window(state) or {}).get("remaining", 0),
    }


def voice_status(state, args):
    """What Aurora is doing in voice, and what this machine can do at all.

    The readiness half is reported rather than discovered at the moment of failure: somebody
    deciding whether to have Aurora join a call should be able to find out first, and an error in
    the middle of a conversation is a bad way to learn that a codec is missing.
    """
    ready = voice_engines.readiness()
    session = state.get("voice")

    window = conversation_window(state)
    transport = state.get("voice_transport")

    return {
        "audio": transport.counters() if transport else None,
        "in_call": session is not None,
        "session": session.snapshot() if session else None,
        "muted": bool(state.get("voice_muted", False)),
        "listening": bool(state.get("voice_listening", False)),
        "conversing": window is not None,
        "utterances_left": window["remaining"] if window else 0,
        "capabilities": ready,
    }


def voice_list_channels(api, args):
    channels = api.request("GET", "/guilds/%s/channels" % args["guild_id"])

    return {"voice_channels": [
        {"channel_id": c.get("id"), "name": c.get("name"),
         "user_limit": c.get("user_limit", 0)}
        for c in channels if c.get("type") == 2
    ]}


def voice_join(state, args, nonce=None):
    ready = voice_engines.readiness()

    if not ready["can_join"]:
        # Joining a call Aurora cannot hear or be heard in is worse than refusing: it puts a
        # silent presence in somebody's conversation and looks like a bug rather than a missing
        # dependency.
        raise Refused(
            E_VOICE_UNAVAILABLE,
            "voice needs: " + "; ".join(ready["missing"]))

    gateway = state.get("gateway")

    if gateway is None or gateway.state != "connected":
        raise Refused(
            E_NETWORK, "Aurora is not signed in to Discord; connect the gateway first")

    identity = gateway.status().get("bot_user_id")

    session = VoiceSession(args["guild_id"], args["channel_id"], identity)
    state["voice"] = session
    state["voice_muted"] = False
    state["voice_listening"] = False

    # One voice state change and no more. Leaving first looked like hygiene and is a second state
    # change racing the first: Discord answers the events out of order and the credentials that
    # arrive belong to a state that has already been superseded. Discord's own client sends one
    # (docs/adr/0068).
    #
    # Discord is told through the gateway, not the REST API: voice membership is gateway state.
    gateway.voice_state(args["guild_id"], args["channel_id"])

    # Discord refuses voice sessions that look perfectly valid, and its own client treats that as
    # ordinary: it attempts the handshake five times, leaving and rejoining between tries, with a
    # growing wait. That is not a workaround bolted on — it is what the protocol turns out to
    # require, and one attempt is the thing that was wrong (docs/adr/0068).
    attempts = []
    transport = None

    for attempt in range(5):
        if attempt:
            # Leave and ask again, exactly as the reference does between tries. A retry that keeps
            # the old voice state is a retry of the same rejected session.
            gateway.voice_state(args["guild_id"], None)
            time.sleep(1 + attempt * 2)
            gateway.voice_state(args["guild_id"], args["channel_id"])

        credentials = gateway.await_voice_credentials(timeout=10)

        if credentials is None:
            attempts.append("no credentials")
            continue

        try:
            transport = VoiceTransport(
                credentials["endpoint"], args["guild_id"], credentials["user_id"],
                credentials["session_id"], credentials["token"],
                channel_id=args["channel_id"])

            transport.start(timeout=20)
            break
        except Exception as retryable:
            attempts.append("%s: %s" % (type(retryable).__name__, str(retryable)[:90]))
            transport = None

    if transport is None:
        broken = RuntimeError("; ".join(attempts[-3:]) or "no attempt was made")

        try:
            # Leaving again, so a failed join never leaves Aurora sitting in the channel unable to
            # hear or be heard.
            state.pop("voice", None)
            gateway.voice_state(args["guild_id"], None)
        except Exception:
            pass

        # The message, not only the type. The voice token lives in the SELECT_PROTOCOL payload and
        # never reaches a socket exception — reporting the class name alone is the precaution that
        # cost an afternoon on the main gateway (docs/adr/0067).
        # The error first. Whatever carries this is truncated somewhere, and the trail is the part
        # that can be reconstructed from the events; the exception is not.
        raise Refused(
            E_VOICE_UNAVAILABLE,
            "%s | after %s" % (
                str(broken)[:300], " -> ".join(gateway.voice_trail[-3:]))) from None

    state["voice_transport"] = transport

    report("voice.joined", session.snapshot())
    return session.snapshot()


def voice_leave(state, args, nonce=None):
    session = state.pop("voice", None)
    state["voice_listening"] = False
    close_conversation(state, "left")

    transport = state.pop("voice_transport", None)

    if transport is not None:
        transport.close()

    gateway = state.get("gateway")

    if gateway is not None and session is not None:
        gateway.voice_state(session.guild_id, None)

    if session is not None:
        report("voice.left", {"guild_id": session.guild_id, "channel_id": session.channel_id})

    return {"in_call": False}


def voice_listen(state, args, nonce=None):
    session = state.get("voice")
    transport = state.get("voice_transport")

    if session is None or transport is None:
        raise Refused(E_NOT_IN_CALL, "Aurora is not in a voice channel")

    if not args["enabled"]:
        transport.deafen()
        state["voice_listening"] = False
        state.pop("voice_audio", None)
        return {"listening": False}

    ready = voice_engines.readiness()

    if not ready["can_listen"]:
        raise Refused(E_VOICE_UNAVAILABLE, "listening needs: " + "; ".join(ready["missing"]))

    # One buffer per speaker, holding only what they are saying right now. Cleared when the turn
    # ends, because a recording of somebody's conversation is not something to keep.
    heard = {}
    state["voice_audio"] = heard

    def on_audio(ssrc, pcm, at_ms):
        # Whoever the voice gateway said this stream belongs to. Told to the session here rather
        # than plumbed through two objects: the transport learns it, the session needs it.
        speaker_id = transport.speaker_of(ssrc)

        if speaker_id is not None and session.speaker_for(ssrc) is None:
            session.identify_speaker(ssrc, speaker_id)

        for action, detail in session.audio(ssrc, at_ms):
            if action == "speaker_started":
                heard[detail["user_id"]] = bytearray()

        speaker = session.speaker_for(ssrc)

        if speaker is not None and speaker in heard:
            heard[speaker].extend(pcm)

    transport.listen(on_audio)

    # A second thread does nothing but notice silence. A turn ends because somebody stopped
    # talking, and nothing arrives to tell you that.
    if not state.get("voice_turns"):
        state["voice_turns"] = True
        threading.Thread(target=_watch_turns, args=(state,), daemon=True).start()

    state["voice_listening"] = True
    return {"listening": True}


def _watch_turns(state):
    """Ends turns on silence and turns each one into a transcript.

    Separate from the audio thread on purpose: recognition takes as long as it takes, and doing it
    where the packets arrive would stop draining the socket while somebody is still speaking.
    """
    engine = voice_engines.find_stt()

    while state.get("voice_listening"):
        time.sleep(0.25)

        session = state.get("voice")
        heard = state.get("voice_audio")

        if session is None or heard is None:
            continue

        for action, detail in session.tick(int(time.monotonic() * 1000)):
            if action != "utterance_ended":
                heard.pop(detail.get("user_id"), None)
                continue

            speaker = detail["user_id"]
            audio = bytes(heard.pop(speaker, b""))

            if not audio:
                continue

            if voice_engines.is_silence(audio):
                # Not asked at all. A recogniser handed near-silence answers with the most common
                # phrase in its training data — "Thank you.", "Thanks for watching!" — and the only
                # reliable way not to believe that is not to ask.
                continue

            try:
                transcript = voice_engines.transcribe(engine, _wav(audio))
            except Exception as unheard:
                # The message. A recogniser that cannot run and one that heard nothing produce the
                # same exception type and need entirely different fixes.
                report("voice.not_understood", {
                    "speaker_id": speaker,
                    "reason": "%s: %s" % (type(unheard).__name__, str(unheard)[:200]),
                })
                continue

            if not transcript.strip() or voice_engines.is_hallucination(transcript):
                # It got through the energy gate and still came back as one of the things a
                # recogniser says when it heard nothing. Answering it would be answering nobody.
                continue

            decision = utterance_heard(
                state, speaker, transcript, int(time.monotonic() * 1000))

            # The raw audio is gone by here. What leaves this function is words, and only because
            # somebody asked Aurora to listen.
            if decision["speak"] and conversation_window(state) is not None:
                time.sleep((decision.get("delay_ms") or 0) / 1000.0)

                waiting = state.setdefault("voice_pending", [])

                # Only the most recent few. A conversation that got ahead of whoever is answering
                # is one where the old lines are no longer worth saying — answering a question
                # from a minute ago is worse than having missed it.
                waiting.append({
                    "speaker_id": speaker,
                    "transcript": transcript,
                    "reason": decision["reason"],
                    "at_ms": int(time.monotonic() * 1000),
                })

                del waiting[:-3]

                report("voice.wants_to_answer", {
                    "speaker_id": speaker,
                    "transcript": transcript,
                    "reason": decision["reason"],
                })


def _wav(pcm):
    """Turns Discord's audio into what a speech recogniser expects.

    Discord carries 48kHz stereo; whisper.cpp wants 16kHz mono. Handing it the wrong rate does not
    fail — it transcribes, confidently, into words nobody said. That is worse than an error, and it
    is what "Araratasmovir." was.

    Every third frame is not a resample, it is decimation, and the difference matters. The source
    carries content up to 24kHz; throwing away two samples in three folds everything above 8kHz
    back into the speech band as noise. Whisper answered that with repetition loops and by
    detecting Polish — the sound of a signal that resembles no language at all.

    Averaging each group of three first is a box filter: crude, one line, and it removes the
    aliasing this was suffering from. An earlier comment here argued the filter was unnecessary
    because speech is narrow-band. That was the wrong question — what matters is what the *source*
    contains, not what the speech does.
    """
    frames = len(pcm) // 4
    mono = bytearray()

    for frame in range(0, frames - 2, 3):
        total = 0

        for offset in range(3):
            left, right = struct.unpack_from("<hh", pcm, (frame + offset) * 4)
            total += (left + right) // 2

        mono += struct.pack("<h", max(-32768, min(32767, total // 3)))

    channels, rate, bits = 1, 16000, 16
    block = channels * bits // 8

    return (
        b"RIFF" + struct.pack("<I", 36 + len(mono)) + b"WAVEfmt "
        + struct.pack("<IHHIIHH", 16, 1, channels, rate, rate * block, block, bits)
        + b"data" + struct.pack("<I", len(mono)) + bytes(mono))


def voice_speak(state, args, nonce=None):
    session = state.get("voice")

    if session is None:
        raise Refused(E_NOT_IN_CALL, "Aurora is not in a voice channel")

    if state.get("voice_muted"):
        raise Refused(E_VOICE_UNAVAILABLE, "Aurora is muted")

    ready = voice_engines.readiness()

    if not ready["can_speak"]:
        raise Refused(E_VOICE_UNAVAILABLE, "speaking needs: " + "; ".join(ready["missing"]))

    speech = session.begin_speaking()

    if speech is None:
        # Somebody has the floor. Refused rather than queued: by the time they finish, what Aurora
        # was going to say may no longer be the right thing to say.
        raise Refused(
            "floor_taken",
            "somebody is speaking; Aurora does not talk over people")

    audio = voice_engines.synthesise(voice_engines.find_tts(), args["text"])
    transport = state.get("voice_transport")

    if transport is None:
        # The session is real and the audio is real; what is missing is the leg that carries it
        # into Discord. Reported as unknown rather than failed, because saying "failed" about a
        # transport that may be half-connected is a guess (docs/adr/0068).
        session.stop_speaking("no_transport")
        raise Unknown("the voice transport is not connected; nothing was heard")

    frames = transport.play(_pcm_from_wav(audio), speech)
    finished = session.finished_speaking(speech)

    return {
        "spoke": True,
        "characters": len(args["text"]),
        "speech_id": speech,
        "frames": frames,

        # False when somebody interrupted. Recording it as finished would say Aurora delivered a
        # whole sentence it was cut off in the middle of.
        "completed": finished,
    }


def utterance_heard(state, user_id, text, at_ms):
    """One finished utterance, and what Aurora does about it.

    Called for every transcript. Almost every call ends in silence, which is the whole point: a
    system that answers every sentence is recognisable within thirty seconds, and the tell is not
    what it says but that it says something every time.
    """
    conversation = state.get("conversation")
    session = state.get("voice")

    decision = (
        conversation.heard(user_id, text, at_ms)
        if conversation is not None
        else {"speak": False, "reason": "not_conversing", "delay_ms": None})

    # Reported whatever the decision. Aurora hears the whole room; what it does about any of it is
    # a separate question, and hiding the ones it stays quiet for would make the room look emptier
    # than it is.
    report("voice.heard", {
        "guild_id": session.guild_id if session else None,
        "channel_id": session.channel_id if session else None,
        "speaker_id": user_id,
        "transcript": text,
        "at_ms": at_ms,
        "aurora_would_speak": decision["speak"],
        "reason": decision["reason"],
        "suggested_delay_ms": decision.get("delay_ms"),
    })

    return decision


def speak_in_conversation(state, text, invited):
    """Says something inside an open window, spending one of its utterances.

    Separate from `discord.voice.speak` on purpose. That capability is a single sentence somebody
    approved; this is Aurora taking a turn in a conversation the owner already agreed to, and the
    difference is what the window is counting.
    """
    window = conversation_window(state)

    if window is None:
        raise Refused("no_conversation", "there is no open conversation window")

    session = state.get("voice")
    transport = state.get("voice_transport")

    if session is None or transport is None:
        raise Refused(E_NOT_IN_CALL, "Aurora is not in a voice channel")

    if state.get("voice_muted"):
        raise Refused(E_VOICE_UNAVAILABLE, "Aurora is muted")

    speech = session.begin_speaking()

    if speech is None:
        # Somebody has the floor. Not queued: by the time they finish, what Aurora was going to
        # say may no longer be the right thing to say.
        raise Refused("floor_taken", "somebody is speaking; Aurora does not talk over people")

    # Answered. Cleared before the audio plays rather than after, so a long sentence does not leave
    # the same line waiting and get answered twice.
    state["voice_pending"] = []

    window["remaining"] -= 1

    audio = voice_engines.synthesise(voice_engines.find_tts(), text)
    frames = transport.play(_pcm_from_wav(audio), speech)
    completed = session.finished_speaking(speech)

    conversation = state.get("conversation")

    if conversation is not None:
        conversation.spoke(int(time.monotonic() * 1000), invited=invited)

    return {
        "spoke": True,
        "completed": completed,
        "frames": frames,
        "utterances_left": window["remaining"],
    }


def _pcm_from_wav(audio):
    """The samples out of a WAV, without a dependency to read one.

    The local speech programs write WAV files. Opus wants raw 48kHz stereo 16-bit samples, and the
    difference is a header this skips past.
    """
    if audio[:4] != b"RIFF":
        return audio

    at = 12

    while at + 8 <= len(audio):
        chunk = audio[at:at + 4]
        (size,) = struct.unpack("<I", audio[at + 4:at + 8])

        if chunk == b"data":
            return audio[at + 8:at + 8 + size]

        at += 8 + size + (size % 2)

    return audio


def voice_converse(state, args, nonce=None):
    """Opens a bounded window in which Aurora may speak when it is spoken to.

    The widest grant in this plugin, and shaped accordingly. Approving every sentence is not a
    conversation — nobody would sit at a keyboard clicking yes while their friends talk — so the
    owner approves the conversation instead, once. What keeps that honest is that the window is
    small by construction: it expires, it counts what it spends, and the three ways to end it
    early (leave, stop, mute) are the capabilities that ask nobody's permission.
    """
    session = state.get("voice")

    if session is None:
        raise Refused(E_NOT_IN_CALL, "Aurora is not in a voice channel")

    ready = voice_engines.readiness()

    if not ready["can_speak"]:
        raise Refused(E_VOICE_UNAVAILABLE, "speaking needs: " + "; ".join(ready["missing"]))

    gateway = state.get("gateway")
    identity = gateway.status() if gateway else {}

    state["conversation"] = Conversation(
        identity.get("bot_name") or "aurora", identity.get("bot_user_id"))

    window = {
        "until_ms": time.monotonic() * 1000 + args["minutes"] * 60_000,
        "remaining": args["max_utterances"],
        "granted_utterances": args["max_utterances"],
        "minutes": args["minutes"],
    }

    state["conversation_window"] = window

    report("voice.conversation_opened", {
        "guild_id": session.guild_id,
        "channel_id": session.channel_id,
        "minutes": args["minutes"],
        "max_utterances": args["max_utterances"],
    })

    return {
        "conversing": True,
        "minutes": args["minutes"],
        "max_utterances": args["max_utterances"],
    }


def conversation_window(state):
    """The live window, or None. Expiry is checked here so nothing has to sweep."""
    window = state.get("conversation_window")

    if window is None:
        return None

    if time.monotonic() * 1000 > window["until_ms"] or window["remaining"] <= 0:
        # Dead by construction rather than by a background job, the same way a consent session is
        # (docs/adr/0010): a window that stops matching cannot be used again.
        state.pop("conversation_window", None)
        state.pop("conversation", None)

        report("voice.conversation_closed", {
            "reason": "expired" if window["remaining"] > 0 else "spent",
        })

        return None

    return window


def close_conversation(state, reason):
    if state.pop("conversation_window", None) is not None:
        state.pop("conversation", None)
        report("voice.conversation_closed", {"reason": reason})


def voice_stop(state, args, nonce=None):
    session = state.get("voice")

    if session is None:
        return {"stopped": False}

    stopped = session.stop_speaking("asked")
    transport = state.get("voice_transport")

    if transport is not None:
        transport.stop()

    return {"stopped": stopped}


def voice_mute(state, args, nonce=None):
    state["voice_muted"] = True

    # Muting ends the window rather than pausing it. A grant that survives being switched off is a
    # grant somebody has to remember to revoke twice.
    close_conversation(state, "muted")
    voice_stop(state, args)
    return {"muted": True}


def voice_unmute(state, args, nonce=None):
    state["voice_muted"] = False
    return {"muted": False}


def gateway_connect(state, args, nonce=None):
    """Signs in to the Gateway, which is how Aurora becomes visible in Discord.

    An effect, and declared as one: the bot shows as online to everybody in every server it is in,
    and starts receiving what people write. That is a change other people can see, so it is not
    something Aurora does because it felt like listening.
    """
    api = state.get("api")

    if api is None:
        raise Refused(E_NO_CREDENTIALS, "no bot token was supplied")

    gateway = state.get("gateway")

    if gateway is None:
        gateway = Gateway(state["token"], gateway_url(api), report)
        state["gateway"] = gateway

    gateway.start()

    # Waited for, briefly, so the answer says what actually happened rather than "starting".
    for _ in range(50):
        if gateway.state in ("connected", "disconnected"):
            break
        time.sleep(0.1)

    if gateway.state != "connected":
        raise Refused(
            E_NETWORK, "could not sign in to the Gateway: %s" % (gateway.detail or gateway.state))

    return gateway.status()


def gateway_disconnect(state, args, nonce=None):
    gateway = state.get("gateway")

    if gateway is not None:
        gateway.stop()

    return {"state": "disconnected"}


def gateway_status(state, args):
    gateway = state.get("gateway")

    if gateway is not None:
        return gateway.status()

    # Not built yet, which is not the same as having no credentials — the gateway is created when
    # somebody connects, because being visible in Discord is an effect and waits for its approval.
    return {
        "state": "disconnected",
        "detail": "not connected"
                  if state.get("api") is not None
                  else "no bot token was supplied",
    }


READS = {
    "discord.voice.list_channels": voice_list_channels,
    "discord.guilds.list": guilds_list,
    "discord.guilds.get": guilds_get,
    "discord.channels.list": channels_list,
    "discord.channels.get": channels_get,
    "discord.messages.list": messages_list,
    "discord.messages.get": messages_get,
    "discord.members.list": members_list,
    "discord.users.get": users_get,
    "discord.threads.list": threads_list,
    "discord.threads.get": threads_get,
}

WRITES = {
    "discord.messages.send": messages_send,
    "discord.messages.reply": messages_reply,
    "discord.messages.edit": messages_edit,
    "discord.messages.delete": messages_delete,
    "discord.reactions.add": reactions_add,
    "discord.reactions.remove": reactions_remove,
    "discord.threads.create": threads_create,
    "discord.channels.create": channels_create,
    "discord.channels.edit": channels_edit,
    "discord.threads.archive": threads_archive,
}


# ---------------------------------------------------------------------------
# the protocol
# ---------------------------------------------------------------------------


# The gateway speaks from its own thread while the main loop answers calls. Two writers on one
# pipe interleave halfway through a line, and Aurora would drop both as unparseable.
_saying = threading.Lock()


def say(frame):
    with _saying:
        sys.stdout.write(json.dumps(frame) + "\n")
        sys.stdout.flush()


def report(kind, payload):
    """What the gateway calls when something happened on Discord.

    An event frame, which Aurora publishes as an external observation. Never a result, and never
    anything Aurora treats as a request: the plugin has no way to ask Aurora to do something, by
    design.
    """
    say({"kind": "event", "type": kind, "payload": payload})


# Capabilities about the connection itself rather than about Discord's API.
GATEWAY_READS = {
    "discord.gateway.status": gateway_status,
    "discord.voice.status": voice_status,
    "discord.voice.pending": voice_pending,
}

GATEWAY_WRITES = {
    "discord.voice.converse": voice_converse,
    "discord.gateway.connect": gateway_connect,
    "discord.gateway.disconnect": gateway_disconnect,
    "discord.voice.join": voice_join,
    "discord.voice.leave": voice_leave,
    "discord.voice.listen": voice_listen,
    "discord.voice.speak": voice_speak,
    "discord.voice.stop": voice_stop,
    "discord.voice.mute": voice_mute,
    "discord.voice.unmute": voice_unmute,
}


def handle(state, frame):
    capability = frame.get("capability", "")
    args = frame.get("input") or {}
    nonce = frame.get("idempotency_key")

    if capability in GATEWAY_READS:
        return GATEWAY_READS[capability](state, args)
    if capability in GATEWAY_WRITES:
        return GATEWAY_WRITES[capability](state, args, nonce)

    api = state.get("api")

    if api is None:
        raise Refused(E_NO_CREDENTIALS, "no bot token was supplied")

    if capability in READS:
        return READS[capability](api, args)
    if capability in WRITES:
        return WRITES[capability](api, args, nonce)

    raise Refused(E_UNSUPPORTED, "this plugin does not offer '%s'" % capability)


def gateway_url(api):
    """Where the Gateway lives, asked rather than assumed.

    Discord publishes this at /gateway/bot and it is not derivable from the API host — the API is
    discord.com and the gateway is somewhere else entirely. Guessing produced a 404 on the upgrade,
    which reads as the endpoint being wrong rather than as the URL never having been looked up.
    """
    answer = api.request("GET", "/gateway/bot")
    url = (answer or {}).get("url")

    if not url:
        raise Refused(E_DISCORD, "Discord did not say where its gateway is")

    return url


def main():
    state = {"api": None, "gateway": None}

    for line in sys.stdin:
        try:
            frame = json.loads(line)
        except ValueError:
            continue

        kind = frame.get("kind")

        if kind == "hello":
            token = (frame.get("secrets") or {}).get("bot_token", "")
            if not token:
                # Aurora refuses to start a service whose secret is missing, so reaching here means
                # something else went wrong. Said once, without the value that is not there.
                say({"kind": "ready", "degraded": True, "reason": E_NO_CREDENTIALS})
            else:
                try:
                    base = api_base(frame.get("endpoints") or [])
                    state["api"] = Discord(token, base)
                    state["token"] = token

                    # Built, not started. Being visible in Discord is an effect, and an effect
                    # waits for the capability that declares it. The gateway's address is looked
                    # up when connecting, not now: this is startup, and a failed lookup here would
                    # stop the plugin answering read-only calls that need no gateway at all.
                    state["gateway"] = None
                    say({"kind": "ready"})
                except Refused as misconfigured:
                    say({"kind": "ready", "degraded": True, "reason": misconfigured.code})

        elif kind == "call":
            answer = {"kind": "result", "id": frame.get("id")}

            try:
                answer.update({"ok": True, "output": handle(state, frame)})
            except Refused as refused:
                answer.update({"ok": False, "refusal": refused.code,
                               "detail": refused.detail})
                if refused.retry_after:
                    answer["retry_after_seconds"] = round(refused.retry_after, 1)
            except Unknown as unsure:
                # The one answer that is neither success nor failure.
                answer.update({"ok": False, "outcome": "unknown", "detail": unsure.detail})
            except KeyError as missing:
                answer.update({"ok": False, "refusal": E_INPUT,
                               "detail": "missing field %s" % missing})
            except Exception as unexpected:
                # The type only. The message could contain anything the interpreter picked up,
                # including a URL with a token in it.
                answer.update({"ok": False, "refusal": E_DISCORD,
                               "detail": "unexpected %s" % type(unexpected).__name__})

            say(answer)

        elif kind == "shutdown":
            # Leaving first, so a shutdown never leaves Aurora sitting silently in somebody's call.
            if state.get("voice"):
                voice_leave(state, {})
            if state.get("gateway"):
                state["gateway"].stop()
            break


if __name__ == "__main__":
    main()
