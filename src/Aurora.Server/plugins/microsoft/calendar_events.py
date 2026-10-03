"""Outlook calendar.

Three things about a calendar make it different from a mailbox, and the shape here follows from
them.

**Writing to a calendar reaches other people.** Creating an event with attendees emails them;
updating one emails them again; cancelling one tells them it is off. There is no draft equivalent —
Graph has no "write this meeting but tell nobody yet" — so every write here declares that it may
notify, and cancelling declares itself irreversible, because an attendee who has read "cancelled"
cannot be made to un-read it.

**A calendar is a list of claims about other people.** An attendee list says who was invited; an
organizer says who arranged it; a response status says who accepted. None of that is authority.
Aurora's fields are named for what they are, so that nothing downstream can mistake "is on the
invitation" for "may approve this".

**Recurrence is not one event.** Graph returns a series master, its occurrences and its exceptions
as different things, and asking for a date range is a different call from asking for events. Listing
here uses `calendarView`, which expands a series into the occurrences that actually fall in the
window — because "what do I have on Tuesday" is a question about occurrences.
"""

import graph

LIST_FIELDS = (
    "id,subject,start,end,isAllDay,isCancelled,organizer,attendees,location,"
    "onlineMeeting,isOnlineMeeting,seriesMasterId,type,webLink,showAs,sensitivity"
)

READ_FIELDS = LIST_FIELDS + ",body,bodyPreview,recurrence,responseStatus"

MAX_BODY = 20000
MAX_SUBJECT = 500
MAX_ATTENDEES = 100


def _bounded(value, limit):
    if value is None:
        return None

    if not isinstance(value, str):
        value = str(value)

    return value[:limit]


def _when(value):
    """A Graph date-time-and-zone pair, kept as two fields.

    Flattening them into one string is how a meeting ends up an hour out: the value is local to the
    zone beside it, and dropping the zone makes it look like something it is not.
    """
    if not isinstance(value, dict):
        return None

    return {
        "date_time": _bounded(value.get("dateTime"), 40),
        "time_zone": _bounded(value.get("timeZone"), 80),
    }


def _person(entry):
    """One name-and-address pair from an event, flattened and bounded.

    Both halves are written by somebody else. A display name that reads like a different address is
    as old as mail and works exactly as well on a meeting invitation.
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


def _attendees(entries):
    """Who was invited, and what they said.

    Deliberately not called anything like "participants who may act". An attendee list is a record
    of who was invited to a meeting. It is never a permission, and Aurora has no code that reads it
    as one — the naming here is the first line of that.
    """
    if not isinstance(entries, list):
        return []

    people = []

    for entry in entries[:MAX_ATTENDEES]:
        if not isinstance(entry, dict):
            continue

        who = _person(entry)

        if who is None:
            continue

        status = entry.get("status") if isinstance(entry.get("status"), dict) else {}

        people.append({
            **who,
            "invited_as": _bounded(entry.get("type"), 20),
            "response": _bounded(status.get("response"), 30),
        })

    return people


def _summary(event):
    location = event.get("location") if isinstance(event.get("location"), dict) else {}
    online = event.get("onlineMeeting") if isinstance(event.get("onlineMeeting"), dict) else {}

    return {
        "event_id": _bounded(event.get("id"), 512),
        "subject": _bounded(event.get("subject"), MAX_SUBJECT),
        "start": _when(event.get("start")),
        "end": _when(event.get("end")),
        "is_all_day": bool(event.get("isAllDay")),
        "is_cancelled": bool(event.get("isCancelled")),

        # The person who arranged it. A fact about the meeting, and not a reason to allow anything.
        "organizer": _person(event.get("organizer")),
        "attendees": _attendees(event.get("attendees")),
        "location": _bounded(location.get("displayName"), 300),
        "is_online_meeting": bool(event.get("isOnlineMeeting")),
        "join_url": _bounded(online.get("joinUrl"), 2000),

        # Which of the three kinds of thing a recurring series produces this is.
        "occurrence_type": _bounded(event.get("type"), 30),
        "series_master_id": _bounded(event.get("seriesMasterId"), 512),
        "shows_as": _bounded(event.get("showAs"), 30),
        "sensitivity": _bounded(event.get("sensitivity"), 30),
        "web_link": _bounded(event.get("webLink"), 2000),
    }


def _full(event):
    summary = _summary(event)
    body = event.get("body") if isinstance(event.get("body"), dict) else {}

    summary.update({
        "body": _bounded(body.get("content"), MAX_BODY),
        "body_type": _bounded(body.get("contentType"), 20),
        "is_recurring": isinstance(event.get("recurrence"), dict),

        # An invitation body is written by whoever sent it, and a meeting invitation is a
        # perfectly ordinary way to put text in front of somebody who did not ask for it.
        "content_is_untrusted": True,
    })

    return summary


# ---------------------------------------------------------------------------
# reading
# ---------------------------------------------------------------------------


def list_events(state, args):
    """What is on the calendar between two moments.

    `calendarView` rather than `/events`, because a recurring series stored as one master would
    otherwise come back as one row that says nothing about Tuesday. The view expands it into the
    occurrences that actually fall inside the window, which is what the question means.
    """
    client = state["client"]
    top = min(int(args.get("limit") or 50), 100)

    answer = client.paged(
        "/me/calendarView",
        query={
            "startDateTime": _moment(args["start"]),
            "endDateTime": _moment(args["end"]),
            "$select": LIST_FIELDS,
            "$top": top,
            "$orderby": "start/dateTime",
        },
        max_pages=max(1, (top // 25) + 1),
    )

    events = [_summary(e) for e in answer["items"][:top]]

    return {
        "events": events,
        "count": len(events),
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client, {"window_days": _days(args)}),
    }


def search_events(state, args):
    """Events matching a search."""
    client = state["client"]
    top = min(int(args.get("limit") or 25), 100)

    answer = client.paged(
        "/me/events",
        query={
            "$search": '"%s"' % args["query"].replace('"', ""),
            "$select": LIST_FIELDS,
            "$top": top,
        },
        max_pages=max(1, (top // 25) + 1),
    )

    events = [_summary(e) for e in answer["items"][:top]]

    return {
        "events": events,
        "count": len(events),
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client),
    }


def read_event(state, args):
    """One event, with its body, its attendees and whether it recurs."""
    client = state["client"]

    event = client.request(
        "GET", "/me/events/%s" % _id(args["event_id"]),
        query={"$select": READ_FIELDS})

    return {"event": _full(event), "audit": graph.audit_metadata(client)}


def find_conflicts(state, args):
    """What already occupies a proposed window.

    A read, and it stays a read: it reports overlaps and books nothing. Free time is decided by the
    person, because "you are free then" and "you are willing then" are different sentences and only
    one of them is in a calendar.
    """
    client = state["client"]
    start, end = _moment(args["start"]), _moment(args["end"])

    answer = client.paged(
        "/me/calendarView",
        query={
            "startDateTime": start,
            "endDateTime": end,
            "$select": LIST_FIELDS,
            "$top": 50,
            "$orderby": "start/dateTime",
        },
        max_pages=2,
    )

    # An event marked free is on the calendar without claiming the time — a reminder, a
    # placeholder, somebody's working-hours block. Counting it as a conflict makes the answer
    # useless on any real calendar.
    clashes = [
        _summary(event) for event in answer["items"]
        if not event.get("isCancelled")
        and (event.get("showAs") or "busy").lower() not in ("free", "workingelsewhere")
    ]

    return {
        "start": start,
        "end": end,
        "conflicts": clashes,
        "is_free": len(clashes) == 0,
        "audit": graph.audit_metadata(client),
    }


def free_busy(state, args):
    """When other people are busy, without saying what they are doing.

    Needs `Calendars.Read.Shared` and a tenant that permits it. Microsoft returns availability
    codes and, depending on the organisation's settings, may or may not return subjects — this
    asks for neither and reports only the codes, because "is Rob free at three" does not require
    knowing what Rob is doing at three.
    """
    client = state["client"]

    answer = client.request(
        "POST", "/me/calendar/getSchedule",
        body={
            "schedules": [str(a)[:320] for a in args["people"][:20]],
            "startTime": {"dateTime": _moment(args["start"]), "timeZone": "UTC"},
            "endTime": {"dateTime": _moment(args["end"]), "timeZone": "UTC"},
            "availabilityViewInterval": min(int(args.get("interval_minutes") or 30), 1440),
        },
        repeatable=True,
    )

    schedules = []

    for entry in (answer.get("value") or [])[:20]:
        if not isinstance(entry, dict):
            continue

        schedules.append({
            "address": _bounded(entry.get("scheduleId"), 320),
            # A string of digits, one per interval: 0 free, 1 tentative, 2 busy, 3 out of office.
            "availability": _bounded(entry.get("availabilityView"), 500),
            "error": _bounded((entry.get("error") or {}).get("message")
                              if isinstance(entry.get("error"), dict) else None, 300),
        })

    return {
        "schedules": schedules,
        "interval_minutes": min(int(args.get("interval_minutes") or 30), 1440),
        "audit": graph.audit_metadata(client),
    }


# ---------------------------------------------------------------------------
# writing, which reaches other people
# ---------------------------------------------------------------------------


def create_event(state, args):
    """Puts a meeting on the calendar, and invites whoever is named.

    There is no draft. Graph has no way to write a meeting and tell nobody yet, so with attendees
    this delivers invitations the moment it succeeds — which is why it declares that it notifies
    and why it costs an approval every time.
    """
    client = state["client"]
    attendees = args.get("attendees") or []

    body = {
        "subject": args["subject"][:MAX_SUBJECT],
        "start": {"dateTime": _moment(args["start"]), "timeZone": args.get("time_zone", "UTC")},
        "end": {"dateTime": _moment(args["end"]), "timeZone": args.get("time_zone", "UTC")},
    }

    if args.get("body"):
        body["body"] = {"contentType": "Text", "content": args["body"][:MAX_BODY]}

    if args.get("location"):
        body["location"] = {"displayName": str(args["location"])[:300]}

    if attendees:
        body["attendees"] = [
            {"emailAddress": {"address": str(a)[:320]}, "type": "required"}
            for a in attendees[:MAX_ATTENDEES]
        ]

    if args.get("online_meeting"):
        # Graph creates the Teams meeting and puts its join link on the event. The alternative —
        # creating an online meeting separately and pasting the link into the body — produces an
        # event Outlook does not recognise as a meeting.
        body["isOnlineMeeting"] = True
        body["onlineMeetingProvider"] = "teamsForBusiness"

    event = client.request("POST", "/me/events", body=body, repeatable=False)

    return {
        "event": _summary(event),
        "invited": len(attendees),
        "audit": graph.audit_metadata(
            client, {"notified_people": len(attendees) > 0}),
    }


def update_event(state, args):
    """Changes an event, and tells the attendees it changed.

    Only the fields named are sent. A replace-shaped update would blank the body, the location and
    the attendee list of anything that did not mention them.
    """
    client = state["client"]
    changes = {}

    if args.get("subject"):
        changes["subject"] = args["subject"][:MAX_SUBJECT]

    if args.get("start"):
        changes["start"] = {
            "dateTime": _moment(args["start"]), "timeZone": args.get("time_zone", "UTC")}

    if args.get("end"):
        changes["end"] = {
            "dateTime": _moment(args["end"]), "timeZone": args.get("time_zone", "UTC")}

    if args.get("body"):
        changes["body"] = {"contentType": "Text", "content": args["body"][:MAX_BODY]}

    if args.get("location"):
        changes["location"] = {"displayName": str(args["location"])[:300]}

    if not changes:
        raise graph.GraphError(
            graph.E_GRAPH, "nothing was asked to change, so nothing was sent to Microsoft")

    event = client.request(
        "PATCH", "/me/events/%s" % _id(args["event_id"]), body=changes, repeatable=False)

    return {
        "event": _summary(event),
        "changed": sorted(changes),
        "audit": graph.audit_metadata(client, {"notified_people": True}),
    }


def cancel_event(state, args):
    """Calls off a meeting and tells everyone it is off.

    Irreversible in the way that matters. The event can be recreated; the message that landed in
    everybody's mailbox cannot be recalled, and somebody has already read it.
    """
    client = state["client"]
    event_id = _id(args["event_id"])

    client.request(
        "POST", "/me/events/%s/cancel" % event_id,
        body={"comment": (args.get("comment") or "")[:2000]},
        repeatable=False)

    return {
        "event_id": event_id,
        "cancelled": True,
        "audit": graph.audit_metadata(client, {"notified_people": True, "irreversible": True}),
    }


def _moment(value):
    """An ISO 8601 moment, checked for shape before it goes into a query string.

    Not parsed into a datetime and back: Graph accepts more forms than Python does, and a
    round-trip through one library's idea of ISO 8601 is a way to change what was asked for.
    """
    text = str(value)

    if not 4 <= len(text) <= 40 or any(c in text for c in "&?#/\\ '\""):
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a shape a date and time comes in")

    return text


def _days(args):
    """A rough size for the audit, without parsing dates."""
    return len(str(args.get("start", ""))[:10] + str(args.get("end", ""))[:10]) // 10


def _id(value):
    text = str(value)

    if not text or len(text) > 512 or any(c in text for c in "/?#\\ "):
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a shape a calendar identifier comes in")

    return text


READS = {
    "microsoft.calendar.list": list_events,
    "microsoft.calendar.search": search_events,
    "microsoft.calendar.read": read_event,
    "microsoft.calendar.conflicts": find_conflicts,
    "microsoft.calendar.free_busy": free_busy,
}

WRITES = {
    "microsoft.calendar.create": create_event,
    "microsoft.calendar.update": update_event,
    "microsoft.calendar.cancel": cancel_event,
}
