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

# Refusals that are about this plugin rather than about Microsoft.
E_UNSUPPORTED = "unsupported_capability"


def say(frame):
    sys.stdout.write(json.dumps(frame) + "\n")
    sys.stdout.flush()


def _where_microsoft_is():
    """Graph and the sign-in service, or a stand-in when one is named in the environment.

    This is a test seam and it is safe because of something Aurora does rather than something this
    program promises: both plugin hosts call `Environment.Clear()` before launching, so a plugin's
    environment contains exactly what Aurora put there and nothing of the owner's shell. Aurora
    never sets these. A test harness that starts this program directly does.

    The alternative — testing the protocol without a subprocess — would test a different program
    from the one that ships.
    """
    base = os.environ.get("AURORA_MICROSOFT_BASE")

    if not base:
        return graph.GRAPH_V1, None, graph.ALLOWED_HOSTS, False

    loopback = os.environ.get("AURORA_MICROSOFT_ALLOW_LOOPBACK") == "1"

    return base + "/v1.0", base, ("127.0.0.1", "::1"), loopback


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
}

WRITES = {
    **mail.WRITES,
    **calendar.WRITES,
    **files.WRITES,
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

        nonce = frame.get("id")

        try:
            say({"kind": "result", "id": nonce, "output": handle(state, frame)})

        except graph.GraphError as refused:
            code, message, detail = refused.as_refusal()

            if code == graph.E_AUTH and state["tokens"] is not None:
                # A tenant can revoke a token before it expires. Dropping the cached one turns the
                # next call into a fresh exchange rather than a second identical rejection.
                state["tokens"].forget()

            say({
                "kind": "error", "id": nonce, "code": code,
                "message": message, "detail": detail,
            })

        except Exception as unexpected:
            # The type, not the text. A message from an unexpected exception is written by whatever
            # threw it and could carry anything at all; the audit gets the shape of the failure and
            # Aurora reports that the call did not work.
            say({
                "kind": "error", "id": nonce,
                "code": graph.E_GRAPH,
                "message": "the plugin failed unexpectedly (%s)" % type(unexpected).__name__,
                "detail": {},
            })


if __name__ == "__main__":
    main()
