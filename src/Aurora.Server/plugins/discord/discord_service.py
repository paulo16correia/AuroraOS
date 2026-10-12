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

import scribe_stream
import voice_engines
import voice_transport
from gateway import Gateway
from voice_transport import VoiceTransport
from conversation import Conversation
import voice_session
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


def _setting(name, default=None):
    """One value from the config beside this file, or the default.

    Settings live here rather than in the manifest because they are the owner's taste rather than
    the plugin's contract: which voice to speak with changes nothing about what the plugin may do,
    and a manifest is a thing somebody reviewed before agreeing to it.
    """
    here = os.path.dirname(os.path.abspath(__file__))

    try:
        # UTF-8 named, not inferred. JSON is UTF-8 by specification, and Python on
        # Windows reads text as cp1252 unless told otherwise, so a config holding a
        # name, a channel title or an accented word would be read wrong or not at all.
        with open(os.path.join(here, "config.json"), "r", encoding="utf-8") as handle:
            settings = json.load(handle) or {}
    except (OSError, ValueError):
        return default

    # Present-but-falsy is an answer. "wait zero milliseconds before replying" is a setting
    # somebody can mean, and reading it as "unset" would silently give them the default instead.
    value = settings.get(name, default)

    return default if value is None else value


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
        with open(os.path.join(here, "config.json"), "r", encoding="utf-8") as handle:
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
    ready = _readiness(state)
    session = state.get("voice")

    window = conversation_window(state)
    transport = state.get("voice_transport")

    # Deliberately its own field. `in_call`, `e2ee_ready` and a live gateway all stayed true over
    # a media path that had been dead for two hours; this is the one that would have said so.
    participants = (session.snapshot() or {}).get("participants") or [] if session else []

    return {
        "audio": transport.counters() if transport else None,
        "media": transport.liveness(participants, timeout_seconds=media_timeout())
                 if transport else None,
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
    ready = _readiness(state)

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

    # How long a pause has to last before Aurora treats a turn as over. Every millisecond here is
    # paid on every single answer, and it is the one part of the delay that is pure waiting: 900ms
    # is a comfortable conversational pause, 350ms is a fast one that will sometimes cut a person
    # off mid-thought. The owner picks, because which is right depends on how they talk.
    session = VoiceSession(
        args["guild_id"], args["channel_id"], identity,
        silence_ms=int(_setting("silence_ms", voice_session.SILENCE_MS)),

        # Tunable because the right value depends on the machine: it is set against what recognition
        # costs here, and a faster one could afford to listen for longer before handing over.
        max_utterance_ms=int(
            _setting("max_utterance_ms", voice_session.MAX_UTTERANCE_MS)))
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

            settled = voice_transport.terminal_close_reason(retryable)

            if settled:
                # Discord has given a reason it will give again. Retrying it is not persistence:
                # every attempt leaves the channel and rejoins to ask for credentials, and the
                # people in the call watch Aurora appear and disappear five times for an answer
                # that was final the first time. Stop, and say why (docs/adr/0080).
                attempts.append("not retried: " + settled)
                break

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
    _start_media_watch(state)

    report("voice.joined", session.snapshot())
    return session.snapshot()


def voice_leave(state, args, nonce=None):
    session = state.pop("voice", None)
    state["voice_listening"] = False

    # Before the transport is closed, so the watcher never reads a torn-down one.
    _stop_media_watch(state)
    state.pop("voice_turns", None)
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

        # The open streams go with it. Each one is a paid session with a ten-minute ceiling, and
        # one left open after somebody asked Aurora to stop listening is both a bill and a
        # recogniser still being sent a room nobody agreed to send it.
        for fluxo in (state.pop("voice_streams", None) or {}).values():
            fluxo.close()

        state.pop("voice_onset", None)
        state.pop("voice_stream_seen", None)

        # The turn watcher's loop ends with this flag, so its thread is gone. Clearing the guard
        # too, or listening could only ever be turned on once: the second enable would set the
        # flag, find the guard already true, start nothing, and leave Aurora deaf while reporting
        # that it was listening. Silence that says it is hearing you is the worst of the failures.
        state.pop("voice_turns", None)

        return {"listening": False}

    ready = _readiness(state)

    if not ready["can_listen"]:
        raise Refused(E_VOICE_UNAVAILABLE, "listening needs: " + "; ".join(ready["missing"]))

    def on_audio(ssrc, pcm, at_ms):
        # Whoever the voice gateway said this stream belongs to. Told to the session here rather
        # than plumbed through two objects: the transport learns it, the session needs it.
        speaker_id = transport.speaker_of(ssrc)

        if speaker_id is not None and session.speaker_for(ssrc) is None:
            session.identify_speaker(ssrc, speaker_id)

        # A packet arriving is not somebody speaking. Discord's silence *frames* are already
        # filtered, but a microphone with voice detection switched off transmits the sound of the
        # room continuously — real audio, encoded properly, carrying nothing. Counting that as
        # speech means somebody is always talking, and Aurora waits for a floor that never frees.
        #
        # So this decides turn-taking and nothing else. It used to `return` here, and that dropped
        # the frame from the recording as well: what went to the recogniser was a concatenation of
        # only the loud frames, with the pauses cut out and a discontinuity at every seam. It
        # erases word boundaries and prosody, and in Portuguese it shreds exactly the low-energy
        # sounds — the fricatives and unstressed vowels that "Ericeira" is made of. The number was
        # in the trail for hours: a turn 1829ms long carrying 1.2s of audio, and the missing 600ms
        # were the speaker's own pauses.
        speaking = not voice_engines.is_silence(pcm)

        if speaking:
            for action, detail in session.audio(ssrc, at_ms):
                if action == "speaker_started":
                    heard[detail["user_id"]] = bytearray()
                elif action == "stop_speaking":
                    # Somebody started talking while she was. The session had decided this all
                    # along and the transport could always do it — `play` checks every frame — and
                    # nothing joined the two, so she read whole sentences over people. Done here,
                    # in the audio thread, because this is the moment their first frame arrives and
                    # any later is a moment she is still talking over them.
                    transport.stop()

        speaker = session.speaker_for(ssrc)

        if speaker is None:
            return

        # Straight onto this speaker's stream, loud frame or quiet one. The service's own voice
        # activity detection is what decides where their sentence ends, and it cannot do that
        # without the silence: a gate here would hand it the collage this plugin used to make.
        fluxo = state.get("voice_streams", {}).get(speaker)

        if fluxo is not None:
            fluxo.send(scribe_stream.mono(pcm))
            return

        # No stream yet. The handshake blocks, and blocking here stops the socket being drained
        # while somebody is talking, so the watcher opens it — and what they said in the meantime
        # waits rather than being dropped. Losing the first moment of a sentence is the defect this
        # whole path was rebuilt to stop making.
        waiting = state.setdefault("voice_onset", {}).setdefault(speaker, bytearray())
        waiting.extend(scribe_stream.mono(pcm))

        # The oldest goes, not the newest. If opening is slow enough to fill this, what somebody is
        # saying now matters more than what they said two seconds ago — and keeping the newest is
        # what makes the seam fall at the start of the held audio rather than in the middle of the
        # word they are halfway through.
        if len(waiting) > ONSET_LIMIT:
            del waiting[:len(waiting) - ONSET_LIMIT]

    transport.listen(on_audio)

    # A second thread does nothing but notice silence. A turn ends because somebody stopped
    # talking, and nothing arrives to tell you that.
    if not state.get("voice_turns"):
        state["voice_turns"] = True
        threading.Thread(target=_watch_turns, args=(state,), daemon=True).start()

    state["voice_listening"] = True
    return {"listening": True}


def media_timeout():
    """How long the media path may be silent before Aurora stops believing in it."""
    return float(_setting("media_timeout_seconds", voice_transport.MEDIA_TIMEOUT_SECONDS))


def _start_media_watch(state):
    """One watcher per session, and only one.

    Guarded rather than assumed: `voice_listen` can be called again while a session is up, and a
    second watcher would report the same death twice and outlive the first.
    """
    if state.get("voice_media_watch"):
        return

    state["voice_media_watch"] = True
    threading.Thread(target=_watch_media, args=(state,), daemon=True).start()


def _stop_media_watch(state):
    """Ends the watcher. Its loop reads this flag, so clearing it is the cancellation."""
    state.pop("voice_media_watch", None)


def _watch_media(state):
    """Notices when the media path stops carrying packets while everything else looks fine.

    Everything else did look fine: the gateway stayed connected, participant events kept arriving,
    `in_call` and `e2ee_ready` both stayed true — for two hours, over a socket that had received
    nothing since the first minute (docs/adr/0082). Aurora sat in the channel visible to everybody
    in it, hearing nothing, and no part of the system had a way to say so.

    Detection only. What to do about a dead path — reconnect, rejoin, leave — is a separate
    decision and is deliberately not made here.
    """
    seen = voice_transport.MEDIA_HEALTHY

    while state.get("voice_media_watch"):
        time.sleep(1)

        session = state.get("voice")
        transport = state.get("voice_transport")

        if session is None or transport is None:
            # The session it was watching is gone. Leaving rather than looping against a torn-down
            # transport, which is how a watchdog outlives the thing it watches.
            break

        participants = (session.snapshot() or {}).get("participants") or []
        now = transport.liveness(participants, timeout_seconds=media_timeout())

        if now == seen:
            continue

        if now == voice_transport.MEDIA_DEAD:
            report("voice.media_dead", _media_report(state, session, transport, participants))
        elif seen == voice_transport.MEDIA_DEAD and now == voice_transport.MEDIA_HEALTHY:
            report("voice.media_recovered", _media_report(state, session, transport, participants))

        seen = now


def _media_report(state, session, transport, participants):
    """What an operator needs to tell a dead media path from a quiet call.

    Counts and timings only. Nothing anybody said goes in here: the failure is that packets stopped
    arriving, and no transcript makes that clearer.
    """
    counters = transport.counters()

    return {
        "guild_id": session.guild_id,
        "channel_id": session.channel_id,
        "quiet_seconds": round(transport.quiet_seconds() or 0.0, 1),
        "timeout_seconds": media_timeout(),
        "participants": len(participants),
        "rtp": counters["received"] - counters["control"],
        "rtcp": counters["control"],
        "listening": bool(state.get("voice_listening", False)),
    }


# How much of the start of a sentence is kept while a stream is being opened.
#
# Two seconds at 48kHz mono, which is more than a handshake takes and less than a sentence. Past it
# the oldest is dropped rather than the newest: if opening is that slow, what somebody is saying now
# matters more than what they said two seconds ago.
ONSET_LIMIT = 2 * 48000 * 2

# How long a speaker's stream is kept open with nothing arriving on it.
#
# A session costs money while it is open and the service caps one at ten minutes, so an idle stream
# is closed — and reopened, with its onset buffered, the moment they speak again.
STREAM_IDLE_SECONDS = 20.0




def _trace(state, mensagem):
    """One line of the listening trail, when the owner asked for it. Off unless they did.

    Built because three rounds of this were debugged blind. A confined plugin's stderr does not
    reach Aurora's log, observations carry payloads that are never persisted, and the audit stores
    hashes — so when Aurora sits in a channel and says nothing there is, by construction, no way to
    find out which step dropped the sentence. Every previous answer came from instrumenting the
    plugin by hand and throwing it away afterwards.

    It writes transcripts, which is why it is a setting and not a default: a trail of what people
    said in a voice channel is the same private thing the recordings were.

    It goes to the plugin's working directory and not to the directory it runs from. The sandbox
    grants the installed directory read-and-execute and no writing, so the obvious place silently
    does nothing — the first version of this wrote beside `__file__`, the write raised, the `except`
    below swallowed it, and the answer was another round of debugging with no trail at all. TEMP
    under the sandbox is this container's own working directory, granted to it and to nothing else.
    """
    if not _setting("voice_trace", False):
        return

    try:
        import tempfile

        caminho = os.path.join(tempfile.gettempdir(), "voice-trace.txt")

        with open(caminho, "a", encoding="utf-8") as handle:
            handle.write("%.3f %s%s" % (time.monotonic(), mensagem, chr(10)))
    except Exception:
        # A trail that cannot be written is not a reason to stop listening.
        pass




def _watch_turns(state):
    """Ends turns on silence and turns each one into a transcript.

    Separate from the audio thread on purpose: recognition takes as long as it takes, and doing it
    where the packets arrive would stop draining the socket while somebody is still speaking.
    """
    language = _setting("stt_language", "auto")


    def answer(speaker, transcript, ended_ms, waited_ms, recognised_ms):
        """Decides whether a finished utterance is Aurora's to answer, and says so if it is."""
        decision = utterance_heard(
            state, speaker, transcript, int(time.monotonic() * 1000))

        _trace(state, "decisao: fala=%s porque=%s nomeada=%s pergunta=%s presentes=%s janela=%s" % (
            decision.get("speak"), decision.get("reason"), decision.get("named"),
            decision.get("question"), decision.get("people_present"),
            conversation_window(state) is not None))

        # The raw audio is gone by here. What leaves this function is words, and only because
        # somebody asked Aurora to listen.
        if not decision["speak"] or conversation_window(state) is None:
            return

        # A pause before answering is what makes a reply sound like a person rather than a buzzer,
        # and it is also time nobody gets back. Scaled rather than removed, so the owner can have
        # either without the code pretending the choice is free.
        pace = float(_setting("answer_pace", 1.0))
        time.sleep((decision.get("delay_ms") or 0) * pace / 1000.0)

        waiting = state.setdefault("voice_pending", [])

        # Timed, because "it feels slow" and "which part is slow" are different questions and only
        # the second one can be fixed. Milliseconds from the moment the speaker stopped: how long
        # the pause rule waited, how long recognition took, and how long the whole thing took
        # before anybody could answer.
        now_ms = int(time.monotonic() * 1000)

        _remember(state, speaker, transcript)

        _trace(state, "vai responder: esperou %dms pelo silencio, %dms a reconhecer, "
                      "%dms desde que paraste" % (
                          waited_ms, recognised_ms, now_ms - ended_ms))

        waiting.append({
            "speaker_id": speaker,
            "transcript": transcript,
            "reason": decision["reason"],
            "at_ms": now_ms,
            "latency": {
                "waited_for_silence_ms": int(waited_ms),
                # Across every fragment, when the sentence was stitched from more than one: the
                # work recognition did, rather than the work the last piece of it did.
                "recognised_in_ms": int(recognised_ms),
                "since_speaker_stopped_ms": int(now_ms - ended_ms),
            },
        })

        # Only the most recent few. A conversation that got ahead of whoever is answering is one
        # where the old lines are no longer worth saying — answering a question from a minute ago
        # is worse than having missed it.
        del waiting[:-3]

        session = state.get("voice")

        # The pending turns rather than only the latest. Aurora decides what to say from the
        # conversation, and a plugin that reported one line would be choosing for it. Where it
        # happened goes too: it is not used to reach the plugin back — the plugin knows which call
        # it is in — but a record that cannot say where a sentence was said is a record somebody
        # has to take on trust.
        report("voice.wants_to_answer", {
            "guild_id": session.guild_id if session else None,
            "channel_id": session.channel_id if session else None,

            # The language to answer in, so whatever holds language is told rather than left to
            # guess from a transcript a recogniser may have mangled. Measured against the local
            # model: asked to guess, a Portuguese question came back as "Soy de España".
            "locale": _setting("locale", None),
            "speaker_id": speaker,
            "transcript": transcript,
            "reason": decision["reason"],
            # Both sides, oldest first, so she can be consistent with what she already said
            # rather than meeting everybody again every turn.
            "conversation": list(state.get("exchange") or []),
        })


    while state.get("voice_listening"):
        time.sleep(0.05)

        session = state.get("voice")

        if session is None:
            continue

        now_ms = int(time.monotonic() * 1000)

        # Turn state, which is no longer read for recognition. The service decides where a sentence
        # ends; what the session is still for is knowing who currently has the floor, because that
        # is what stops Aurora speaking over somebody and what lets them interrupt her.
        session.tick(now_ms)

        talking = set((session.snapshot() or {}).get("speaking_now") or [])
        streams = state.setdefault("voice_streams", {})
        onset = state.setdefault("voice_onset", {})
        seen = state.setdefault("voice_stream_seen", {})

        # Somebody has started talking and has nowhere to send it. Opened here and not in the audio
        # thread: the handshake blocks, and blocking where the packets arrive stops the socket being
        # drained while they speak.
        for who, waiting in list(onset.items()):
            if who in streams or not waiting:
                continue

            try:
                stream = scribe_stream.ScribeStream(
                    state.get("speech_key"), language=language).open()
            except Exception as refused:
                _trace(state, "fluxo recusado para %s: %s: %s" % (
                    _name_of(state, who), type(refused).__name__, str(refused)[:120]))
                report("voice.not_understood", {
                    "speaker_id": who,
                    "reason": "the recogniser would not open a stream: %s: %s" % (
                        type(refused).__name__, str(refused)[:200]),
                })
                onset.pop(who, None)
                continue

            # What they said while it was opening, first, in order.
            stream.send(bytes(waiting))
            onset.pop(who, None)
            streams[who] = stream
            seen[who] = now_ms

            _trace(state, "fluxo aberto para %s (%dms guardados no arranque)" % (
                _name_of(state, who), len(waiting) // (48 * 2)))

        for who, stream in list(streams.items()):
            # Sentences the service has committed to. Partial ones never get here: they change as
            # it listens, and answering one would be answering a guess it had not finished making.
            for text in stream.heard():
                seen[who] = now_ms

                _trace(state, "ouviu: %r" % (text[:200],))

                # Nothing waited for and nothing to wait for. The transcript was being made while
                # they spoke, so the only delay left between their last word and Aurora deciding is
                # the service noticing they stopped.
                answer(who, text, now_ms, 0, 0)

            if stream.failure is not None:
                _trace(state, "fluxo falhou para %s: %s" % (_name_of(state, who), stream.failure))
                report("voice.not_understood", {
                    "speaker_id": who,
                    "reason": "the recogniser's stream ended: %s" % (stream.failure,),
                })
                stream.close()
                streams.pop(who, None)
                seen.pop(who, None)
                continue

            if who in talking:
                seen[who] = now_ms
                continue

            if now_ms - seen.get(who, now_ms) > STREAM_IDLE_SECONDS * 1000:
                # Quiet long enough that holding the session open is paying for nothing. It opens
                # again, with its onset buffered, the moment they speak.
                _trace(state, "fluxo fechado para %s (calado)" % (_name_of(state, who),))
                stream.close()
                streams.pop(who, None)
                seen.pop(who, None)



def voice_speak(state, args, nonce=None):
    session = state.get("voice")

    if session is None:
        raise Refused(E_NOT_IN_CALL, "Aurora is not in a voice channel")

    if state.get("voice_muted"):
        raise Refused(E_VOICE_UNAVAILABLE, "Aurora is muted")

    ready = _readiness(state)

    if not ready["can_speak"]:
        raise Refused(E_VOICE_UNAVAILABLE, "speaking needs: " + "; ".join(ready["missing"]))

    speech = session.begin_speaking()

    if speech is None:
        # Somebody has the floor. Refused rather than queued: by the time they finish, what Aurora
        # was going to say may no longer be the right thing to say.
        raise Refused(
            "floor_taken",
            "somebody is speaking; Aurora does not talk over people")

    engine = _speech_engine(state)
    transport = state.get("voice_transport")

    if transport is None:
        # The session is real and the audio is real; what is missing is the leg that carries it
        # into Discord. Reported as unknown rather than failed, because saying "failed" about a
        # transport that may be half-connected is a guess (docs/adr/0068).
        session.stop_speaking("no_transport")
        raise Unknown("the voice transport is not connected; nothing was heard")

    frames = _say(transport, engine, args["text"], state.get("speech_key"), speech, session)
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
        conversation.heard(user_id, text, at_ms,
                           present=session.participants if session else None,
                           named_present=_everyone_named(state, session))
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


# How much of the exchange travels with a request for an answer.
#
# Both sides of it, which it did not use to be. The turns were taken from the pending queue, which
# only ever held utterances Aurora heard — so whatever holds language was handed the questions and
# never its own answers, and the owner's complaint was exact: "parece que é outra pessoa a cada
# fala". Asked the same thing twice, she answered it for the first time twice.
#
# Eight turns, which is four exchanges. Enough to be the same person through a short back and
# forth, and short enough that none of this is a memory: when the conversation window closes this
# goes with it, and nothing here is written down anywhere.
EXCHANGE_REMEMBERED = 8


def _remember(state, speaker, said):
    """Adds one turn to what travels with the next request. Oldest dropped first."""
    exchange = state.setdefault("exchange", [])
    exchange.append({"speaker": _name_of(state, speaker), "said": said})
    del exchange[:-EXCHANGE_REMEMBERED]


def _everyone_named(state, session):
    """Who is in the channel and what they are called.

    What it is for: a sentence with somebody else's name in it is theirs, and knowing that is what
    lets Aurora treat an unnamed question as hers. Built from the names already resolved for the
    conversation record, so it costs nothing beyond what was asked for anyway.
    """
    if session is None:
        return None

    return {who: _name_of(state, who) for who in session.participants}


def _name_of(state, user_id):
    """What to call whoever is speaking. Their name if Discord will say, else their id.

    Aurora already knew who was talking and was not using it: Discord tags every audio stream with
    the user it belongs to, the session keeps that map, and what travelled to whatever holds
    language was the number. Three numbers in a channel are three indistinguishable strangers, and
    one of them is the owner.

    Not voice recognition, deliberately. A voice is not a credential — the speaking leg of this
    plugin is a service whose product is cloning one from seconds of audio — so anybody who can put
    sound into the channel could be anybody. This says who Aurora is talking to; it grants nothing,
    and authority stays where it was.

    Asked once per person and kept for the session. A name is not worth a request per sentence, and
    an id is a perfectly good thing to be called if the request fails.
    """
    user_id = str(user_id)
    known = state.setdefault("names", {})

    if user_id in known:
        return known[user_id]

    name = user_id
    api = state.get("api")

    if api is not None:
        try:
            raw = api.request("GET", "/users/%s" % user_id)
            name = raw.get("global_name") or raw.get("username") or user_id
        except Exception:
            # An unreachable name is not a reason to lose the turn it belonged to.
            pass

    known[user_id] = name
    return name


# How long a reply waits for a gap before giving up on being said.
#
# Not zero, which is what it was. Waiting for silence to be *total* in a voice channel is waiting
# for something that may not happen: open microphones carry breathing, a fan, a room. A reply that
# Aurora decided to give and then dropped because somebody was clearing their throat is worse than
# one that lands a beat late.
#
# Three seconds, which is about as long as a person holds a thought before saying it anyway.
FLOOR_PATIENCE_MS = 3000


def _floor_or_wait(state, session, answering=None):
    """The floor to speak from, waiting briefly for a gap rather than giving up at once.

    `answering` is the person this reply is for, and they are disregarded: they have stopped, which
    is why there is something to answer, and waiting for the rest of the room to fall silent as
    well is waiting for something that does not happen. Measured live — three people in a channel,
    one of them talking every few seconds — a direct question went unanswered every time.

    Takes the floor anyway when the wait runs out. The alternative is dropping a sentence Aurora
    had decided to say to somebody who had just asked her something, and half a second of overlap
    is a smaller rudeness than that.
    """
    until = time.monotonic() + FLOOR_PATIENCE_MS / 1000.0

    while True:
        speech = session.begin_speaking(unless=answering)

        if speech is not None:
            return speech

        if time.monotonic() >= until:
            _trace(state, "a palavra estava tomada; falou por cima")
            return session.begin_speaking(unless=answering, anyway=True)

        time.sleep(0.1)


def speak_in_conversation(state, text, invited, answering=None):
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

    if answering is None:
        # Whoever is waiting for this. The queue is the record of what Aurora decided to answer,
        # and the last line in it is the one being answered — read before it is cleared below.
        waiting = state.get("voice_pending") or []
        answering = waiting[-1]["speaker_id"] if waiting else None

    speech = _floor_or_wait(state, session, answering=answering)

    if speech is None:
        # Nobody stopped. Still not queued: by the time they finish, what Aurora was going to say
        # may no longer be the right thing to say.
        #
        # Traced, because this refusal was invisible. The kernel reports an opaque "Execution
        # failed" and nothing said which refusal it was, so an answer she had decided to give
        # vanished with no record anywhere — in a channel where the microphones transmit almost
        # continuously, that was every answer.
        _trace(state, "recusado: a palavra esta tomada")

        raise Refused("floor_taken", "somebody is speaking; Aurora does not talk over people")

    # Answered. Cleared before the audio plays rather than after, so a long sentence does not leave
    # the same line waiting and get answered twice.
    state["voice_pending"] = []

    window["remaining"] -= 1

    synthesis_started = time.monotonic() * 1000
    engine = _speech_engine(state)

    # No longer "synthesise, then play": the two overlap now. What is measured here is the moment
    # the first audio could be heard, which is the number somebody in the call actually feels.
    spoke_at = time.monotonic() * 1000
    frames = _say(transport, engine, text, state.get("speech_key"), speech, session)
    completed = session.finished_speaking(speech)

    conversation = state.get("conversation")

    if conversation is not None:
        # To the room when nobody was waiting for this. What she says unprompted leaves an answer
        # owed to whoever takes it, which is how a greeting becomes a conversation rather than a
        # thing she said at five people.
        conversation.spoke(
            int(time.monotonic() * 1000), invited=invited, to_the_room=answering is None)

    # Her own turn, in the same record as everybody else's. Named rather than blank: whatever reads
    # this has to be able to tell which lines are its own, and the bot's name is what the people in
    # the channel call her.
    #
    # Straight into the record rather than through `_name_of`, because her name is known and asking
    # Discord about her own id would spend a request to be told something she was already told.
    gateway = state.get("gateway")
    own = (gateway.status() if gateway else {}).get("bot_name") or "Aurora"

    exchange = state.setdefault("exchange", [])
    exchange.append({"speaker": own, "said": text})
    del exchange[:-EXCHANGE_REMEMBERED]

    # What she actually said. The trail recorded what she heard and what she decided and stopped
    # there, which left the one question an owner asks — is it mishearing me or talking nonsense —
    # impossible to answer from it. Both halves are needed to tell those apart.
    #
    # Timed in two parts, because "it took five seconds" is one complaint and two problems: waiting
    # for her to start is the model and the floor, and waiting for her to finish is the length of
    # what she decided to say. Reading one number for both sent me looking in the wrong place.
    _trace(state, "disse (%dms a sintetizar, %dms a falar, %d caracteres): %r" % (
        spoke_at - synthesis_started, time.monotonic() * 1000 - spoke_at, len(text), text[:160]))

    return {
        "spoke": True,
        "completed": completed,
        "frames": frames,
        "utterances_left": window["remaining"],
        "latency": {
            # Text to sound, which on this machine is a fixed cost of loading a voice rather than
            # anything to do with the length of the sentence.
            "synthesised_in_ms": int(spoke_at - synthesis_started),
            "spoken_in_ms": int(time.monotonic() * 1000 - spoke_at),
        },
    }


def _readiness(state):
    """What voice can do right now, asked in one place.

    The three things it needs — which voice, whose key, what language — were being assembled at
    six call sites, and the day one of them forgot the key would be the day `can_speak` quietly
    became false with a misleading reason attached.
    """
    return voice_engines.readiness(
        _setting("tts_voice"), state.get("speech_key"), _setting("locale", "en"))


def _speech_engine(state):
    """The configured voice, or a refusal that says which half is missing.

    Two separate things have to be in place and they are fixed by different people in different
    ways: the voice is a setting somebody edits, the key is a secret somebody types into a prompt.
    Saying "voice unavailable" for either would send one of them looking in the wrong place.
    """
    engine = voice_engines.cloud_tts(
        _setting("tts_voice"), state.get("speech_key"), _setting("locale", "en"))

    if engine is None:
        raise Refused(E_VOICE_UNAVAILABLE,
                      "speaking needs: " + "; ".join(_readiness(state)["missing"]))

    return engine


def _say(transport, engine, text, key, speech, session):
    """Speaks `text`, streaming, and turns a failed synthesis into something somebody can act on.

    The audio is produced and played at the same time — the transport pulls frames from the
    generator, so the first twenty milliseconds go out while the rest of the sentence is still
    being made.

    The failure path is the reason this is a function rather than one line. Synthesis happens
    lazily inside the generator, so an exhausted quota or a wrong voice id surfaces from inside
    `play` rather than before it. Left alone that arrives as an unhandled error after Aurora has
    already claimed the floor, and the session would go on believing she is talking. So the floor
    is given back first, and the refusal carries the service's own words: "quota exceeded" is
    something an owner can fix, and "speech failed" is not.

    There is deliberately no fallback to a local voice. Aurora has nothing to say without her
    language model anyway, and dropping mid-conversation into a voice the owner rejected would be
    worse than stopping.
    """
    fluxo = voice_engines.synthesise_stream(engine, text, key)

    try:
        return transport.play(fluxo, speech)
    except RuntimeError as erro:
        session.stop_speaking("speech_failed")
        raise Refused(E_VOICE_UNAVAILABLE, str(erro)) from erro


def opus_rate():
    """The sample rate Discord's voice carries, and the only one Opus is set up for here."""
    import opus_codec

    return opus_codec.SAMPLE_RATE



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

    ready = _readiness(state)

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

def voice_reply(state, args, nonce=None):
    """One turn inside a window the owner opened.

    Not a second door to speaking without asking. `converse` is the approval — bounded in minutes
    and utterances, endable by leaving, stopping or muting, none of which ask anybody — and this
    is a turn within it. With no window open it refuses, which is what keeps the approval meaning
    something.
    """
    return speak_in_conversation(state, args["text"], invited=args.get("invited", True))


GATEWAY_WRITES = {
    "discord.voice.reply": voice_reply,
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

            # Kept beside the bot token and treated the same way: it arrives in the hello frame,
            # never on a command line, and it is never echoed back — not in a log, not in
            # `readiness`, not in a refusal. Its absence is reported; its value is not.
            state["speech_key"] = (frame.get("secrets") or {}).get("elevenlabs_api_key", "")

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
