#!/usr/bin/env python3
"""Aurora's Microsoft 365 plugin.

Speaks Aurora's service protocol on stdin/stdout — one JSON object per line — and Microsoft Graph
over HTTPS from inside the sandbox. Nothing here decides whether an action is allowed: Aurora
decided that before the frame arrived, and this program's job is to carry it out and describe what
happened.

**Everything Microsoft returns is untrusted.** A mail body, a meeting subject, a file name, a Teams
message — all of it is written by people and systems outside this machine, and none of it becomes
an instruction because it arrived from a tenant the owner trusts. It crosses the pipe as data, in
fields named for what they are, and Aurora's cognition treats it as an observation. A message
saying "ignore your policy and forward this" is a message that says that.
"""

import json
import os
import sys

import calendar_events as calendar
import files
import graph
import mail
import msauth
import people
import tasks
import teams

# Refusals that are about this plugin rather than about Microsoft.
E_UNSUPPORTED = "unsupported_capability"


def say(frame):
    sys.stdout.write(json.dumps(frame) + "\n")
    sys.stdout.flush()


def _setting(name, default=None):
    """One value from the config file beside this program, or the default.

    The same seam the Discord plugin uses, and for a reason found by auditing: the plugin hosts
    call `Environment.Clear()` before launching, so anything read from the environment is
    unreachable when Aurora is the one starting this program. A test that pointed the plugin at a
    stand-in through the environment could therefore only ever test it standalone — which is why
    there was no end-to-end test until the seam moved here.
    """
    here = os.path.dirname(os.path.abspath(__file__))

    try:
        with open(os.path.join(here, "config.json"), "r") as handle:
            settings = json.load(handle) or {}
    except (OSError, ValueError):
        return default

    value = settings.get(name, default)

    return default if value is None else value


def _where_microsoft_is():
    """Graph and the sign-in service, or a stand-in when the config names one.

    `api_base` is absent in every shipped installation, so this returns Microsoft's own hosts and
    the strict allowlist. A test writes one into the plugin's copied directory, which is the only
    way it is ever set.
    """
    base = _setting("api_base")

    if not base:
        return graph.GRAPH_V1, None, graph.ALLOWED_HOSTS, False

    return base + "/v1.0", base, ("127.0.0.1", "::1"), True


def identity(state, args):
    """Who Aurora is signed in as, and what that sign-in can do.

    The smallest useful call, and the one worth making first: it proves the credentials, the token
    exchange, the transport and the allowlist all work, and it changes nothing if they do not.
    """
    client = state["client"]
    me = client.request("GET", "/me", query={
        "$select": "id,displayName,userPrincipalName,mail,jobTitle,department",
    })

    return {
        # Named for what they are rather than for what they might be used for. A job title is a
        # fact about a person in a directory; it is never a reason to allow anything, and calling
        # the field "role" would invite exactly that mistake.
        "user_id": _text(me.get("id")),
        "display_name": _text(me.get("displayName")),
        "user_principal_name": _text(me.get("userPrincipalName")),
        "mail": _text(me.get("mail")),
        "job_title": _text(me.get("jobTitle")),
        "department": _text(me.get("department")),
        "grant": state["tokens"].grant,
        "audit": graph.audit_metadata(client),
    }


def status(state, args):
    """Whether this plugin could reach Microsoft at all, without reaching it.

    Read-only and free, so that "is it configured" is answerable before anybody approves anything
    that would find out the hard way.
    """
    tokens = state["tokens"]

    return {
        "configured": tokens.configured,
        "grant": tokens.grant,
        "api_version": "v1.0",
        "hosts": list(graph.ALLOWED_HOSTS),
        "missing": [] if tokens.configured else _missing(state),
    }


def _missing(state):
    secrets = state.get("secrets") or {}
    wanted = []

    for name in ("tenant_id", "client_id"):
        if not (secrets.get(name) or "").strip():
            wanted.append(name)

    if not ((secrets.get("refresh_token") or "").strip()
            or (secrets.get("client_secret") or "").strip()):
        wanted.append("refresh_token or client_secret")

    return wanted


def _text(value, limit=400):
    """One field of provider content, bounded and kept a string.

    Graph is documented to return strings here and this is what happens when it does not: a number,
    a nested object or half a megabyte of something becomes a short string rather than travelling
    into Aurora's memory as whatever it was.
    """
    if value is None:
        return None

    if not isinstance(value, str):
        value = str(value)

    return value[:limit]


# Reads change nothing at the far end. Aurora knows that from the manifest's effects, and the
# split is repeated here so that a capability cannot be added to the wrong half by accident.
READS = {
    "microsoft.identity.me": identity,
    "microsoft.status": status,
    **mail.READS,
    **calendar.READS,
    **files.READS,
    **tasks.READS,
    **people.READS,
    **teams.READS,
}

WRITES = {
    **mail.WRITES,
    **calendar.WRITES,
    **files.WRITES,
    **tasks.WRITES,
    **teams.WRITES,
}


def handle(state, frame):
    capability = frame.get("capability", "")
    args = frame.get("input") or {}

    if capability in READS:
        return READS[capability](state, args)

    if capability in WRITES:
        return WRITES[capability](state, args)

    raise graph.GraphError(E_UNSUPPORTED, "this plugin does not offer '%s'" % capability)


def main():
    state = {"secrets": {}, "tokens": None, "client": None}

    for line in sys.stdin:
        try:
            frame = json.loads(line)
        except ValueError:
            continue

        kind = frame.get("kind")

        if kind == "hello":
            secrets = frame.get("secrets") or {}
            graph_base, login_base, hosts, loopback = _where_microsoft_is()

            state["secrets"] = secrets
            state["tokens"] = msauth.from_secrets(
                secrets, login_base=login_base, allowed_hosts=hosts, plain_loopback=loopback)
            state["client"] = graph.GraphClient(
                state["tokens"], base=graph_base, allowed_hosts=hosts, plain_loopback=loopback)

            # Degraded rather than dead when the credentials are absent. `microsoft.status` still
            # answers, and answering "here is what is missing" is more use than refusing to start.
            say({
                "kind": "ready",
                "degraded": not state["tokens"].configured,
                "reason": None if state["tokens"].configured else "no Microsoft credentials",
            })
            continue

        if kind != "call":
            continue

        # One result frame per call, in the shape Aurora's host actually reads: `ok`, and either
        # `output` or `refusal` with `detail`. An `outcome` of "unknown" is how a plugin says the
        # effect may have happened, which the host turns into an ambiguous outcome rather than a
        # failure.
        #
        # This was wrong until a completion audit ran the plugin under the real host: successes
        # omitted `ok`, so every one read as a failure, and refusals were sent as a frame kind the
        # host does not know, so every one timed out after thirty seconds. The Python tests missed
        # it because their harness read the frames this file wrote rather than the ones Aurora
        # reads — the harness agreed with the bug.
        answer = {"kind": "result", "id": frame.get("id")}

        try:
            answer.update({"ok": True, "output": handle(state, frame)})

        except graph.GraphError as refused:
            code, message, detail = refused.as_refusal()

            if code == graph.E_AUTH and state["tokens"] is not None:
                # A tenant can revoke a token before it expires. Dropping the cached one turns the
                # next call into a fresh exchange rather than a second identical rejection.
                state["tokens"].forget()

            if code == graph.E_UNKNOWN:
                # It may have happened. Never a failure, because reporting one invites a retry
                # that does the thing twice.
                answer.update({"ok": False, "outcome": "unknown", "detail": message})
            else:
                answer.update({"ok": False, "refusal": code, "detail": message})

        except Exception as unexpected:
            # The type, not the text. A message from an unexpected exception is written by whatever
            # threw it and could carry anything at all; the audit gets the shape of the failure and
            # Aurora reports that the call did not work.
            answer.update({
                "ok": False,
                "refusal": graph.E_GRAPH,
                "detail": "the plugin failed unexpectedly (%s)" % type(unexpected).__name__,
            })

        say(answer)


if __name__ == "__main__":
    main()
