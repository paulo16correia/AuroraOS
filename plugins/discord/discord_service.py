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
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

from gateway import Gateway

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


def gateway_connect(state, args, nonce=None):
    """Signs in to the Gateway, which is how Aurora becomes visible in Discord.

    An effect, and declared as one: the bot shows as online to everybody in every server it is in,
    and starts receiving what people write. That is a change other people can see, so it is not
    something Aurora does because it felt like listening.
    """
    gateway = state.get("gateway")

    if gateway is None:
        raise Refused(E_NO_CREDENTIALS, "no bot token was supplied")

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
    return gateway.status() if gateway else {"state": "disconnected", "detail": "no credentials"}


READS = {
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
GATEWAY_READS = {"discord.gateway.status": gateway_status}
GATEWAY_WRITES = {
    "discord.gateway.connect": gateway_connect,
    "discord.gateway.disconnect": gateway_disconnect,
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


def gateway_url(base):
    """Where the Gateway lives, derived from the API base so a stand-in works unchanged."""
    parts = urllib.parse.urlparse(base)
    scheme = "wss" if parts.scheme == "https" else "ws"
    return "%s://%s/gateway" % (scheme, parts.netloc)


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

                    # Built, not started. Being visible in Discord is an effect, and an effect
                    # waits for the capability that declares it.
                    state["gateway"] = Gateway(token, gateway_url(base), report)
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
            if state.get("gateway"):
                state["gateway"].stop()
            break


if __name__ == "__main__":
    main()
