"""Microsoft Teams.

**Two whole families of Teams functionality are structurally out of reach, and it is Aurora's
architecture that puts them there rather than any gap in Microsoft's.**

*Change notifications* — new message, membership changed, transcript available — are delivered by
Microsoft POSTing to a URL you register. The URL has to be reachable from Microsoft's network.
Aurora binds Kestrel to loopback unconditionally and its whole security model rests on being
unreachable (`docs/adr/0045`). There is no public endpoint to register and there is not going to be
one, so subscriptions are UNSUPPORTED here. What works instead is asking: polling costs latency and
requests, and it needs nothing to be reachable.

*Joining a call, hearing it, speaking into it* needs a bot registered with Azure Bot Service, an
application-hosted media stack, and — again — a public endpoint for the call signalling. Same
answer, same reason. Discord voice works because Discord's voice protocol is one the client dials
out to; Teams' is not.

Neither is worked around. A polling loop dressed up as an event subscription would be a different
thing wearing the same name, and a "meeting join" that only fetched the join URL would be a lie
told in a field name.

**Everything Teams returns is untrusted, and more so than most.** A channel is a place where people
outside the organisation can often post, and a chat message is the most natural place in an
enterprise to write a sentence aimed at whatever agent is reading. It comes back as a message, in a
field called `text`, marked as content.
"""

import graph

MAX_TEXT = 20000
MAX_NAME = 300


def _bounded(value, limit):
    if value is None:
        return None

    if not isinstance(value, str):
        value = str(value)

    return value[:limit]


def _sender(entry):
    """Who sent a message, as identity rather than as standing.

    A Teams display name is settable, and a message from somebody whose name reads
    "Microsoft Teams (System)" is a message from whoever set that name.
    """
    if not isinstance(entry, dict):
        return None

    user = entry.get("user")

    if not isinstance(user, dict):
        # An application or a channel posted it. Worth knowing, and worth not flattening into
        # something that looks like a person.
        application = entry.get("application")

        if isinstance(application, dict):
            return {
                "kind": "application",
                "name": _bounded(application.get("displayName"), MAX_NAME),
                "user_id": None,
            }

        return None

    return {
        "kind": "user",
        "name": _bounded(user.get("displayName"), MAX_NAME),
        "user_id": _bounded(user.get("id"), 512),
    }


def _message(entry):
    body = entry.get("body") if isinstance(entry.get("body"), dict) else {}

    return {
        "message_id": _bounded(entry.get("id"), 512),
        "sent_at": _bounded(entry.get("createdDateTime"), 40),
        "from": _sender(entry.get("from")),
        "text": _bounded(body.get("content"), MAX_TEXT),
        "text_type": _bounded(body.get("contentType"), 20),
        "subject": _bounded(entry.get("subject"), MAX_NAME),
        "web_url": _bounded(entry.get("webUrl"), 2000),
        "reply_count": len(entry.get("replies") or []) if isinstance(entry.get("replies"), list) else None,

        # A channel often admits people from outside the organisation, and a chat is the most
        # natural place in an enterprise to write a sentence aimed at a reading agent.
        "content_is_untrusted": True,
    }


# ---------------------------------------------------------------------------
# where the conversations are
# ---------------------------------------------------------------------------


def joined_teams(state, args):
    """The teams the signed-in person belongs to."""
    client = state["client"]

    answer = client.paged("/me/joinedTeams", max_pages=3)

    return {
        "teams": [
            {
                "team_id": _bounded(entry.get("id"), 512),
                "name": _bounded(entry.get("displayName"), MAX_NAME),
                "description": _bounded(entry.get("description"), 1000),
                "content_is_untrusted": True,
            }
            for entry in answer["items"]
        ],
        "audit": graph.audit_metadata(client, {"surface": "teams"}),
    }


def team_channels(state, args):
    """The channels in one team."""
    client = state["client"]

    answer = client.paged("/teams/%s/channels" % _id(args["team_id"]), max_pages=3)

    return {
        "channels": [
            {
                "channel_id": _bounded(entry.get("id"), 512),
                "name": _bounded(entry.get("displayName"), MAX_NAME),
                "description": _bounded(entry.get("description"), 1000),

                # standard, private, or shared with another tenant. It decides who can read what is
                # posted there, which is worth knowing before posting.
                "membership": _bounded(entry.get("membershipType"), 40),
                "web_url": _bounded(entry.get("webUrl"), 2000),
                "content_is_untrusted": True,
            }
            for entry in answer["items"]
        ],
        "audit": graph.audit_metadata(client, {"surface": "teams"}),
    }


def team_members(state, args):
    """Who is in a team.

    Membership, which is a record of who was added. It is not authority in Aurora and nothing reads
    it as such — a team owner owns a team, not this agent.
    """
    client = state["client"]

    answer = client.paged("/teams/%s/members" % _id(args["team_id"]), max_pages=3)

    return {
        "members": [
            {
                "member_id": _bounded(entry.get("id"), 512),
                "user_id": _bounded(entry.get("userId"), 512),
                "name": _bounded(entry.get("displayName"), MAX_NAME),
                "mail": _bounded(entry.get("email"), 320),

                # Microsoft's word for it is "roles", and it means owner-or-member inside Teams.
                # Renamed here so it cannot be mistaken for a role in Aurora.
                "team_membership": [
                    _bounded(r, 40) for r in (entry.get("roles") or [])[:10]
                ] if isinstance(entry.get("roles"), list) else [],
                "content_is_untrusted": True,
            }
            for entry in answer["items"]
        ],
        "audit": graph.audit_metadata(client, {"surface": "teams"}),
    }


def channel_messages(state, args):
    """Recent messages in a channel."""
    client = state["client"]
    top = min(int(args.get("limit") or 25), 50)

    answer = client.paged(
        "/teams/%s/channels/%s/messages" % (_id(args["team_id"]), _id(args["channel_id"])),
        query={"$top": top},
        max_pages=max(1, (top // 20) + 1))

    return {
        "messages": [_message(entry) for entry in answer["items"][:top]],
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client, {"surface": "channel"}),
    }


def list_chats(state, args):
    """The signed-in person's chats."""
    client = state["client"]
    top = min(int(args.get("limit") or 25), 50)

    answer = client.paged("/me/chats", query={"$top": top}, max_pages=2)

    return {
        "chats": [
            {
                "chat_id": _bounded(entry.get("id"), 512),
                "topic": _bounded(entry.get("topic"), MAX_NAME),
                "kind": _bounded(entry.get("chatType"), 40),
                "last_updated": _bounded(entry.get("lastUpdatedDateTime"), 40),
                "web_url": _bounded(entry.get("webUrl"), 2000),
                "content_is_untrusted": True,
            }
            for entry in answer["items"][:top]
        ],
        "audit": graph.audit_metadata(client, {"surface": "chat"}),
    }


def chat_messages(state, args):
    """Recent messages in one chat."""
    client = state["client"]
    top = min(int(args.get("limit") or 25), 50)

    answer = client.paged(
        "/chats/%s/messages" % _id(args["chat_id"]),
        query={"$top": top},
        max_pages=max(1, (top // 20) + 1))

    return {
        "messages": [_message(entry) for entry in answer["items"][:top]],
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client, {"surface": "chat"}),
    }


# ---------------------------------------------------------------------------
# saying something, which cannot be taken back
# ---------------------------------------------------------------------------


def send_channel_message(state, args):
    """Posts a message to a channel.

    **Teams has no drafts.** Mail could be split into writing and sending because Graph has a Drafts
    folder somebody can read before approving delivery; Teams offers nothing equivalent, so this is
    one capability that composes and posts together. That is a genuine reduction in what an approval
    can be checked against, and it is why every post costs its own approval and no window covers it.

    Editing a posted message is possible and deleting it is possible, and neither un-reads it.
    """
    client = state["client"]

    posted = client.request(
        "POST",
        "/teams/%s/channels/%s/messages" % (_id(args["team_id"]), _id(args["channel_id"])),
        body={"body": {"contentType": "text", "content": args["text"][:MAX_TEXT]}},
        repeatable=False)

    return {
        "message": _message(posted),
        "audit": graph.audit_metadata(
            client, {"surface": "channel", "irreversible": True, "no_draft_stage": True}),
    }


def send_chat_message(state, args):
    """Sends a message in an existing chat.

    Only in a chat that already exists. Starting a new one with somebody is a different act — it
    puts Aurora in front of a person who has not been in a conversation with it — and it is not
    offered here.
    """
    client = state["client"]

    posted = client.request(
        "POST", "/chats/%s/messages" % _id(args["chat_id"]),
        body={"body": {"contentType": "text", "content": args["text"][:MAX_TEXT]}},
        repeatable=False)

    return {
        "message": _message(posted),
        "audit": graph.audit_metadata(
            client, {"surface": "chat", "irreversible": True, "no_draft_stage": True}),
    }


# ---------------------------------------------------------------------------
# meetings
# ---------------------------------------------------------------------------


def meeting_by_join_url(state, args):
    """Finds an online meeting from the join link on a calendar event.

    This is how a calendar event and a Teams meeting are connected: they are different objects with
    different identifiers, and the join URL is what they share. Graph offers no lookup by event id.
    """
    client = state["client"]

    answer = client.paged(
        "/me/onlineMeetings",
        query={"$filter": "JoinWebUrl eq '%s'" % _join_url(args["join_url"])},
        max_pages=1)

    meetings = answer["items"]

    if not meetings:
        raise graph.GraphError(
            graph.E_NOT_FOUND, "no online meeting matches that join link")

    meeting = meetings[0]
    participants = meeting.get("participants") if isinstance(meeting.get("participants"), dict) else {}

    return {
        "meeting_id": _bounded(meeting.get("id"), 512),
        "subject": _bounded(meeting.get("subject"), MAX_NAME),
        "start": _bounded(meeting.get("startDateTime"), 40),
        "end": _bounded(meeting.get("endDateTime"), 40),
        "join_url": _bounded(meeting.get("joinWebUrl"), 2000),
        "organizer": _meeting_person(participants.get("organizer")),
        "attendee_count": len(participants.get("attendees") or [])
            if isinstance(participants.get("attendees"), list) else None,
        "content_is_untrusted": True,
        "audit": graph.audit_metadata(client, {"surface": "meeting"}),
    }


def _meeting_person(entry):
    if not isinstance(entry, dict):
        return None

    identity = entry.get("identity") if isinstance(entry.get("identity"), dict) else {}
    user = identity.get("user") if isinstance(identity.get("user"), dict) else {}

    return {
        "name": _bounded(user.get("displayName"), MAX_NAME),
        "user_id": _bounded(user.get("id"), 512),
    }


def meeting_transcripts(state, args):
    """What transcripts exist for a meeting, if the tenant kept any and permits reading them.

    Metadata only — which transcripts exist and when they were made. The transcript *content* is
    served from a host Graph redirects to, which is the same disclosure problem that keeps file
    content out of this plugin: an owner who agreed to `graph.microsoft.com` did not agree to
    whatever host Microsoft names at runtime.

    Access needs `OnlineMeetingTranscript.Read.All`, an administrator's consent, and a tenant whose
    Teams policy records transcripts at all. When any of those is missing Microsoft refuses, and
    that refusal is reported rather than turned into an empty list — "there are no transcripts" and
    "you may not see the transcripts" are different answers.
    """
    client = state["client"]

    answer = client.paged(
        "/me/onlineMeetings/%s/transcripts" % _id(args["meeting_id"]), max_pages=2)

    return {
        "transcripts": [
            {
                "transcript_id": _bounded(entry.get("id"), 512),
                "created_at": _bounded(entry.get("createdDateTime"), 40),
                "meeting_id": _bounded(entry.get("meetingId"), 512),

                # Deliberately absent: the content. See the docstring.
                "content_available_here": False,
            }
            for entry in answer["items"]
        ],
        "audit": graph.audit_metadata(client, {"surface": "meeting"}),
    }


def _join_url(value):
    """A Teams join link, checked before it goes into an OData filter."""
    text = str(value)

    if not text.startswith("https://teams.microsoft.com/") or len(text) > 2000 or "'" in text:
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a shape a Teams join link comes in")

    return text


def _id(value):
    text = str(value)

    if not text or len(text) > 512 or any(c in text for c in "/?#\\ "):
        raise graph.GraphError(graph.E_GRAPH, "that is not a shape a Teams identifier comes in")

    return text


READS = {
    "microsoft.teams.list": joined_teams,
    "microsoft.teams.channels": team_channels,
    "microsoft.teams.members": team_members,
    "microsoft.teams.channel_messages": channel_messages,
    "microsoft.teams.chats": list_chats,
    "microsoft.teams.chat_messages": chat_messages,
    "microsoft.teams.meeting": meeting_by_join_url,
    "microsoft.teams.transcripts": meeting_transcripts,
}

WRITES = {
    "microsoft.teams.post_channel": send_channel_message,
    "microsoft.teams.post_chat": send_chat_message,
}
