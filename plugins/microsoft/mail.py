"""Outlook mail.

**Nothing here composes and sends in one step, and that is the design rather than an omission.**

Graph offers `POST /me/sendMail`, which takes a message body and delivers it. It is one call and it
is the wrong shape for a governed agent: approving it means approving whatever text the model
produced in the same breath, unread. So this module offers no such capability. Sending always
operates on a draft that already exists, which means:

- the draft is created by one capability, with its own effect and its own approval;
- the owner can read exactly what it says, through an ordinary read capability, before deciding;
- the approval to send is an approval to send *that* content, not a blank cheque on the next thing
  composed.

`reply` and `forward` follow the same rule: they use Graph's `createReply` and `createForward`,
which produce drafts, never the sibling calls that deliver immediately.

Everything Microsoft returns here is untrusted. A subject line and a message body are written by
whoever sent them — frequently by somebody outside the organisation entirely — and mail is the
single most likely place for text addressed at a reading agent rather than at a person. It crosses
the pipe as data, in fields named for what it is, bounded in length.
"""

import graph

# What a mailbox call reads back. Named explicitly rather than taking Graph's default, because the
# default includes the full body on every message in a list and that is both slow and far more of
# the owner's correspondence than a list needs.
LIST_FIELDS = (
    "id,conversationId,subject,from,toRecipients,receivedDateTime,isRead,hasAttachments,"
    "importance,bodyPreview,webLink"
)

READ_FIELDS = LIST_FIELDS + ",ccRecipients,body,internetMessageId"

# Bounds on provider content. A body is the owner's mail and can legitimately be long; it is still
# bounded, because the alternative is letting a correspondent decide how much of Aurora's memory one
# message occupies.
MAX_BODY = 20000
MAX_PREVIEW = 1000
MAX_SUBJECT = 500
MAX_RECIPIENTS = 50


def _address(entry):
    """One name-and-address pair, flattened and bounded.

    Both halves are attacker-controlled: a display name is whatever the sender put in it, and has
    been used to make an address look like a different one for as long as mail has existed. They
    stay separate fields here so that nothing downstream has to unpick a rendered string.
    """
    if not isinstance(entry, dict):
        return None

    address = entry.get("emailAddress")

    if not isinstance(address, dict):
        return None

    return {
        "name": _bounded(address.get("name"), 200),
        "address": _bounded(address.get("address"), 320),
    }


def _addresses(entries):
    if not isinstance(entries, list):
        return []

    found = [_address(entry) for entry in entries[:MAX_RECIPIENTS]]
    return [entry for entry in found if entry]


def _bounded(value, limit):
    if value is None:
        return None

    if not isinstance(value, str):
        value = str(value)

    return value[:limit]


def _summary(message):
    """A message as a list shows it: enough to choose one, not enough to be reading the mailbox."""
    return {
        "message_id": _bounded(message.get("id"), 500),
        "conversation_id": _bounded(message.get("conversationId"), 500),
        "subject": _bounded(message.get("subject"), MAX_SUBJECT),
        "from": _address(message.get("from")),
        "to": _addresses(message.get("toRecipients")),
        "received_at": _bounded(message.get("receivedDateTime"), 40),
        "is_read": bool(message.get("isRead")),
        "has_attachments": bool(message.get("hasAttachments")),
        "importance": _bounded(message.get("importance"), 20),
        "preview": _bounded(message.get("bodyPreview"), MAX_PREVIEW),
        "web_link": _bounded(message.get("webLink"), 2000),
    }


def _full(message):
    """One message, with its body.

    The body's content type travels with it. Aurora is told whether it is holding HTML or text
    rather than being left to guess, because guessing is how markup ends up rendered somewhere it
    should have been escaped.
    """
    summary = _summary(message)
    body = message.get("body") if isinstance(message.get("body"), dict) else {}

    summary.update({
        "cc": _addresses(message.get("ccRecipients")),
        "body": _bounded(body.get("content"), MAX_BODY),
        "body_type": _bounded(body.get("contentType"), 20),
        "internet_message_id": _bounded(message.get("internetMessageId"), 500),

        # Said out loud on every message that carries a body, because this is the field most likely
        # to contain text written to be read by an agent.
        "content_is_untrusted": True,
    })

    return summary


# ---------------------------------------------------------------------------
# reading
# ---------------------------------------------------------------------------


def list_messages(state, args):
    """Recent messages from one folder."""
    client = state["client"]
    folder = args.get("folder") or "inbox"
    top = min(int(args.get("limit") or 25), 100)

    answer = client.paged(
        "/me/mailFolders/%s/messages" % _folder(folder),
        query={
            "$select": LIST_FIELDS,
            "$top": top,
            "$orderby": "receivedDateTime desc",
        },
        max_pages=max(1, (top // 25) + 1),
    )

    messages = [_summary(m) for m in answer["items"][:top]]

    return {
        "messages": messages,
        "count": len(messages),
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client, {"folder": folder}),
    }


def search_messages(state, args):
    """Messages matching a search, across the mailbox."""
    client = state["client"]
    top = min(int(args.get("limit") or 25), 100)

    # $search rather than $filter: Microsoft's own relevance ranking over subject, body and
    # participants, which is what somebody asking "find the thread about the contract" means.
    # $orderby is not accepted alongside it and asking for both is an error, not a preference.
    answer = client.paged(
        "/me/messages",
        query={
            "$search": '"%s"' % args["query"].replace('"', ""),
            "$select": LIST_FIELDS,
            "$top": top,
        },
        max_pages=max(1, (top // 25) + 1),
    )

    messages = [_summary(m) for m in answer["items"][:top]]

    return {
        "messages": messages,
        "count": len(messages),
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client),
    }


def read_message(state, args):
    """One message, including its body."""
    client = state["client"]

    message = client.request(
        "GET", "/me/messages/%s" % _id(args["message_id"]),
        query={"$select": READ_FIELDS})

    return {
        "message": _full(message),
        "audit": graph.audit_metadata(client),
    }


def list_attachments(state, args):
    """What is attached to a message, by name and size.

    Metadata only. Downloading content is a separate question and is not implemented: Graph serves
    file content from a host that is deliberately not on this plugin's allowlist, so fetching one
    means an explicit credential-free request to somewhere else, and that deserves its own
    capability and its own approval rather than arriving as a side effect of listing.
    """
    client = state["client"]

    answer = client.paged(
        "/me/messages/%s/attachments" % _id(args["message_id"]),
        query={"$select": "id,name,contentType,size,isInline"},
        max_pages=2)

    return {
        "attachments": [
            {
                "attachment_id": _bounded(a.get("id"), 500),
                "name": _bounded(a.get("name"), 300),
                "content_type": _bounded(a.get("contentType"), 200),
                "size_bytes": a.get("size") if isinstance(a.get("size"), int) else None,
                "is_inline": bool(a.get("isInline")),
            }
            for a in answer["items"]
        ],
        "audit": graph.audit_metadata(client),
    }


# ---------------------------------------------------------------------------
# writing, which is never sending
# ---------------------------------------------------------------------------


def create_draft(state, args):
    """Writes a draft into the mailbox. Delivers nothing.

    The draft is a real message in the Drafts folder, which the owner can open in Outlook and read
    exactly as it will be sent. That is the point of the split: an approval to send is then an
    approval to send text somebody could have looked at.
    """
    client = state["client"]

    body = {
        "subject": args["subject"][:MAX_SUBJECT],
        "body": {
            "contentType": args.get("body_type", "Text"),
            "content": args["body"][:MAX_BODY],
        },
        "toRecipients": _recipients(args.get("to")),
    }

    if args.get("cc"):
        body["ccRecipients"] = _recipients(args["cc"])

    draft = client.request("POST", "/me/messages", body=body, repeatable=False)

    return _draft_result(client, draft, "created")


def create_reply(state, args):
    """A draft reply to a message, quoting it the way Outlook would."""
    client = state["client"]

    draft = client.request(
        "POST", "/me/messages/%s/createReply" % _id(args["message_id"]),
        body={"comment": args.get("comment", "")[:MAX_BODY]},
        repeatable=False)

    return _draft_result(client, draft, "created")


def create_forward(state, args):
    """A draft forwarding a message to somebody else."""
    client = state["client"]

    draft = client.request(
        "POST", "/me/messages/%s/createForward" % _id(args["message_id"]),
        body={
            "comment": args.get("comment", "")[:MAX_BODY],
            "toRecipients": _recipients(args.get("to")),
        },
        repeatable=False)

    return _draft_result(client, draft, "created")


def send_draft(state, args):
    """Delivers a draft that already exists. The only capability in this plugin that sends mail.

    It takes an identifier and nothing else — no subject, no body, no recipients. Everything about
    what is delivered was decided by an earlier call that had its own approval, and can be read
    before this one is approved.

    Not repeatable. A send that times out may already have delivered, and Graph offers no
    idempotency key for this: reporting it as failed would invite a retry that sends twice, so it
    comes back as an unknown outcome and stays that way until somebody looks.
    """
    client = state["client"]
    message_id = _id(args["message_id"])

    client.request("POST", "/me/messages/%s/send" % message_id, repeatable=False)

    return {
        "message_id": message_id,
        "sent": True,

        # The draft is gone from Drafts once it is sent, so asking again gets a 404 rather than a
        # second delivery. That is not idempotency Aurora can rely on, and it is worth knowing.
        "audit": graph.audit_metadata(client, {"irreversible": True}),
    }


def move_message(state, args):
    """Moves a message to another folder. Reversible by moving it back."""
    client = state["client"]

    moved = client.request(
        "POST", "/me/messages/%s/move" % _id(args["message_id"]),
        body={"destinationId": _folder(args["folder"])},
        repeatable=False)

    return {
        # Graph gives the message a new id in its new folder. Returning the old one would hand
        # back an identifier that no longer resolves.
        "message_id": _bounded(moved.get("id"), 500),
        "folder": _bounded(args["folder"], 200),
        "audit": graph.audit_metadata(client),
    }


def mark_read(state, args):
    """Marks a message read or unread."""
    client = state["client"]

    client.request(
        "PATCH", "/me/messages/%s" % _id(args["message_id"]),
        body={"isRead": bool(args["is_read"])},
        repeatable=True)

    return {
        "message_id": _bounded(args["message_id"], 500),
        "is_read": bool(args["is_read"]),
        "audit": graph.audit_metadata(client),
    }


def _draft_result(client, draft, what):
    return {
        "draft_id": _bounded(draft.get("id"), 500),
        "state": what,
        "subject": _bounded(draft.get("subject"), MAX_SUBJECT),
        "to": _addresses(draft.get("toRecipients")),
        "web_link": _bounded(draft.get("webLink"), 2000),

        # Said in the result rather than only in the manifest, because this is what a person reads
        # when deciding whether to approve the send that would follow.
        "sent": False,
        "audit": graph.audit_metadata(client),
    }


def _recipients(addresses):
    if not addresses:
        return []

    return [
        {"emailAddress": {"address": str(address)[:320]}}
        for address in list(addresses)[:MAX_RECIPIENTS]
    ]


def _id(value):
    """A Graph identifier, checked before it is put in a URL.

    Message ids are long base64-ish strings and arrive from Aurora, which got them from an earlier
    call to this plugin. Checked anyway: an identifier is the one place a path is built from
    something that came from outside, and `..` in one would be a request to somewhere else.
    """
    text = str(value)

    if not text or len(text) > 512 or any(c in text for c in "/?#\\ "):
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a shape an Outlook identifier comes in")

    return text


def _folder(name):
    """A folder, by well-known name or by id."""
    text = str(name or "inbox")

    known = {
        "inbox": "inbox",
        "drafts": "drafts",
        "sentitems": "sentitems",
        "sent": "sentitems",
        "deleteditems": "deleteditems",
        "deleted": "deleteditems",
        "archive": "archive",
        "junkemail": "junkemail",
        "junk": "junkemail",
    }

    lowered = text.lower()

    if lowered in known:
        return known[lowered]

    return _id(text)


READS = {
    "microsoft.mail.list": list_messages,
    "microsoft.mail.search": search_messages,
    "microsoft.mail.read": read_message,
    "microsoft.mail.attachments": list_attachments,
}

WRITES = {
    "microsoft.mail.draft": create_draft,
    "microsoft.mail.draft_reply": create_reply,
    "microsoft.mail.draft_forward": create_forward,
    "microsoft.mail.send_draft": send_draft,
    "microsoft.mail.move": move_message,
    "microsoft.mail.mark_read": mark_read,
}
