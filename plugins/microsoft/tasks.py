"""Microsoft To Do and Microsoft Planner.

**These are two different systems and this module keeps them apart.** To Do is personal — one
person's lists, reached at `/me/todo`. Planner is shared — a group's plans and buckets, reached at
`/planner`. They have different identifiers, different fields, different permissions and different
concurrency rules. Presenting them as one "tasks" surface would mean inventing a common model that
neither of them has, and then quietly losing whichever half did not fit.

**A Microsoft task is not an Aurora task.** Aurora has its own work items, with its own lifecycle,
its own ownership and its own audit. What this module returns is a record from somebody else's
system, and every one of them says so: `provider`, `external_id`, `is_external`. Nothing here
creates, updates or knows about an Aurora work item — the link between the two is Aurora's to record
and Aurora's to own (LAW-005), and a plugin that maintained it would be holding state that is not
its to hold.

So "turn this into a task" is two decisions, not one: create the external task, and record the link.
This module does the first and reports what the second would need.

**Planner needs an etag, and getting that wrong is the usual way Planner code breaks.** Every update
must carry `If-Match` with the task's current `@odata.etag`, and Microsoft rejects the request
outright without it. That is optimistic concurrency doing its job: somebody else may have changed
the task since it was read, and a blind write would silently discard their change.
"""

import graph

MAX_TITLE = 255
MAX_NOTE = 4000


def _bounded(value, limit):
    if value is None:
        return None

    if not isinstance(value, str):
        value = str(value)

    return value[:limit]


def _external(provider, identifier, extra=None):
    """The provenance every task record carries.

    Not decoration and not optional. This is what makes it possible for Aurora to hold "my work
    item W is linked to Microsoft's task T in plan P" as a fact with a source, rather than as two
    things that happen to have the same title.
    """
    record = {
        "provider": provider,
        "external_id": identifier,
        "is_external": True,

        # Said out loud so that nothing downstream reads a Microsoft task as an Aurora one. They
        # have different lifecycles and only one of them is Aurora's to govern.
        "is_aurora_task": False,
    }

    if extra:
        record.update(extra)

    return record


# ---------------------------------------------------------------------------
# To Do — one person's own lists
# ---------------------------------------------------------------------------


def todo_lists(state, args):
    """The signed-in person's task lists."""
    client = state["client"]

    answer = client.paged("/me/todo/lists", max_pages=3)

    return {
        "lists": [
            {
                "list_id": _bounded(entry.get("id"), 512),
                "name": _bounded(entry.get("displayName"), 300),
                "is_shared": bool(entry.get("isShared")),
                "is_default": entry.get("wellknownListName") == "defaultList",
                **_external("microsoft.todo", _bounded(entry.get("id"), 512)),
            }
            for entry in answer["items"]
        ],
        "audit": graph.audit_metadata(client, {"system": "todo"}),
    }


def todo_tasks(state, args):
    """Tasks in one To Do list."""
    client = state["client"]
    top = min(int(args.get("limit") or 50), 100)

    answer = client.paged(
        "/me/todo/lists/%s/tasks" % _id(args["list_id"]),
        query={"$top": top},
        max_pages=max(1, (top // 25) + 1))

    return {
        "tasks": [_todo_task(entry, args["list_id"]) for entry in answer["items"][:top]],
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client, {"system": "todo"}),
    }


def _todo_task(entry, list_id):
    due = entry.get("dueDateTime") if isinstance(entry.get("dueDateTime"), dict) else {}
    body = entry.get("body") if isinstance(entry.get("body"), dict) else {}

    return {
        "title": _bounded(entry.get("title"), MAX_TITLE),
        "status": _bounded(entry.get("status"), 40),
        "is_complete": entry.get("status") == "completed",
        "due": _bounded(due.get("dateTime"), 40),
        "due_time_zone": _bounded(due.get("timeZone"), 80),
        "importance": _bounded(entry.get("importance"), 30),
        "note": _bounded(body.get("content"), MAX_NOTE),

        # A note is written by whoever wrote it, and a task list is shareable.
        "content_is_untrusted": True,
        **_external(
            "microsoft.todo", _bounded(entry.get("id"), 512), {"list_id": _bounded(list_id, 512)}),
    }


def todo_create(state, args):
    """Adds a task to a To Do list.

    Not idempotent, and Microsoft offers nothing that would make it so — no idempotency key, no
    conditional create. Calling it twice makes two tasks. Aurora's own idempotency reservation
    covers a retried *call*; it cannot cover a caller that decides to ask again.
    """
    client = state["client"]

    body = {"title": args["title"][:MAX_TITLE]}

    if args.get("note"):
        body["body"] = {"contentType": "text", "content": args["note"][:MAX_NOTE]}

    if args.get("due"):
        body["dueDateTime"] = {
            "dateTime": _moment(args["due"]),
            "timeZone": args.get("time_zone", "UTC"),
        }

    created = client.request(
        "POST", "/me/todo/lists/%s/tasks" % _id(args["list_id"]),
        body=body, repeatable=False)

    return {
        "task": _todo_task(created, args["list_id"]),

        # What Aurora would need to record the link, said explicitly rather than left to be
        # reconstructed from the shape of the result.
        "link_hint": {
            "provider": "microsoft.todo",
            "external_id": _bounded(created.get("id"), 512),
            "container_id": _bounded(args["list_id"], 512),
        },
        "audit": graph.audit_metadata(client, {"system": "todo", "created": True}),
    }


def todo_complete(state, args):
    """Marks a To Do task finished, or puts it back."""
    client = state["client"]
    done = bool(args.get("is_complete", True))

    updated = client.request(
        "PATCH", "/me/todo/lists/%s/tasks/%s" % (_id(args["list_id"]), _id(args["task_id"])),
        body={"status": "completed" if done else "notStarted"},
        repeatable=True)

    return {
        "task": _todo_task(updated, args["list_id"]),
        "audit": graph.audit_metadata(client, {"system": "todo"}),
    }


# ---------------------------------------------------------------------------
# Planner — a group's shared plans
# ---------------------------------------------------------------------------


def planner_plans(state, args):
    """The plans the signed-in person can see, or one group's plans."""
    client = state["client"]

    group = args.get("group_id")
    where = ("/groups/%s/planner/plans" % _id(group)) if group else "/me/planner/plans"

    answer = client.paged(where, max_pages=3)

    return {
        "plans": [
            {
                "title": _bounded(entry.get("title"), MAX_TITLE),
                "owner_group_id": _bounded(entry.get("owner"), 512),
                "content_is_untrusted": True,
                **_external("microsoft.planner", _bounded(entry.get("id"), 512)),
            }
            for entry in answer["items"]
        ],
        "audit": graph.audit_metadata(client, {"system": "planner"}),
    }


def planner_buckets(state, args):
    """The buckets a plan is divided into."""
    client = state["client"]

    answer = client.paged("/planner/plans/%s/buckets" % _id(args["plan_id"]), max_pages=3)

    return {
        "buckets": [
            {
                "name": _bounded(entry.get("name"), 300),
                "plan_id": _bounded(entry.get("planId"), 512),
                "content_is_untrusted": True,
                **_external("microsoft.planner", _bounded(entry.get("id"), 512)),
            }
            for entry in answer["items"]
        ],
        "audit": graph.audit_metadata(client, {"system": "planner"}),
    }


def planner_tasks(state, args):
    """Tasks in one plan."""
    client = state["client"]
    top = min(int(args.get("limit") or 50), 100)

    answer = client.paged(
        "/planner/plans/%s/tasks" % _id(args["plan_id"]),
        query={"$top": top},
        max_pages=max(1, (top // 25) + 1))

    return {
        "tasks": [_planner_task(entry) for entry in answer["items"][:top]],
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client, {"system": "planner"}),
    }


def _planner_task(entry):
    assignments = entry.get("assignments") if isinstance(entry.get("assignments"), dict) else {}

    return {
        "title": _bounded(entry.get("title"), MAX_TITLE),
        "plan_id": _bounded(entry.get("planId"), 512),
        "bucket_id": _bounded(entry.get("bucketId"), 512),
        "percent_complete": entry.get("percentComplete")
            if isinstance(entry.get("percentComplete"), int) else None,
        "is_complete": entry.get("percentComplete") == 100,
        "due": _bounded(entry.get("dueDateTime"), 40),

        # Who it is assigned to, as user identifiers. A record of who was given the work — never a
        # statement about who may do anything in Aurora.
        "assigned_to": [_bounded(user_id, 512) for user_id in list(assignments)[:20]],

        # Needed for any update. Without it Microsoft refuses the write outright, which is
        # optimistic concurrency doing its job rather than an inconvenience.
        "etag": _bounded(entry.get("@odata.etag"), 200),
        "content_is_untrusted": True,
        **_external("microsoft.planner", _bounded(entry.get("id"), 512)),
    }


def planner_create(state, args):
    """Adds a task to a plan, optionally assigned to somebody.

    Assigning is a claim about who should do the work. It is not a grant of anything, in Microsoft
    or in Aurora, and nothing reads it as one.
    """
    client = state["client"]

    body = {
        "planId": _id(args["plan_id"]),
        "title": args["title"][:MAX_TITLE],
    }

    if args.get("bucket_id"):
        body["bucketId"] = _id(args["bucket_id"])

    if args.get("due"):
        body["dueDateTime"] = _moment(args["due"])

    if args.get("assign_to"):
        body["assignments"] = {
            _id(user): {"@odata.type": "#microsoft.graph.plannerAssignment",
                        "orderHint": " !"}
            for user in list(args["assign_to"])[:20]
        }

    created = client.request("POST", "/planner/tasks", body=body, repeatable=False)

    return {
        "task": _planner_task(created),
        "link_hint": {
            "provider": "microsoft.planner",
            "external_id": _bounded(created.get("id"), 512),
            "container_id": _bounded(args["plan_id"], 512),
        },
        "audit": graph.audit_metadata(client, {"system": "planner", "created": True}),
    }


def planner_update(state, args):
    """Changes a Planner task, refusing to overwrite somebody else's change.

    The etag must be the one from the task as it was last read. Microsoft compares it and rejects
    the write if the task has moved on — which is the point. A blind update would discard whatever
    a colleague changed in the meantime, silently.
    """
    client = state["client"]

    changes = {}

    if args.get("title"):
        changes["title"] = args["title"][:MAX_TITLE]

    if args.get("bucket_id"):
        changes["bucketId"] = _id(args["bucket_id"])

    if args.get("percent_complete") is not None:
        changes["percentComplete"] = max(0, min(int(args["percent_complete"]), 100))

    if args.get("due"):
        changes["dueDateTime"] = _moment(args["due"])

    if not changes:
        raise graph.GraphError(
            graph.E_GRAPH, "nothing was asked to change, so nothing was sent to Microsoft")

    client.request(
        "PATCH", "/planner/tasks/%s" % _id(args["task_id"]),
        body=changes,
        headers={"If-Match": _etag(args["etag"]), "Prefer": "return=representation"},
        repeatable=False)

    return {
        "task_id": _bounded(args["task_id"], 512),
        "changed": sorted(changes),
        "provider": "microsoft.planner",
        "is_aurora_task": False,
        "audit": graph.audit_metadata(client, {"system": "planner", "concurrency": "if-match"}),
    }


def _etag(value):
    """A Planner etag, checked before it becomes a header.

    A header value with a newline in it is a second header, which is how a request grows fields
    nobody wrote.
    """
    text = str(value)

    if not text or len(text) > 200 or any(c in text for c in "\r\n"):
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a shape a Planner etag comes in")

    return text


def _moment(value):
    text = str(value)

    if not 4 <= len(text) <= 40 or any(c in text for c in "&?#/\\ '\""):
        raise graph.GraphError(graph.E_GRAPH, "that is not a shape a date and time comes in")

    return text


def _id(value):
    text = str(value)

    if not text or len(text) > 512 or any(c in text for c in "/?#\\ "):
        raise graph.GraphError(graph.E_GRAPH, "that is not a shape a task identifier comes in")

    return text


READS = {
    "microsoft.todo.lists": todo_lists,
    "microsoft.todo.tasks": todo_tasks,
    "microsoft.planner.plans": planner_plans,
    "microsoft.planner.buckets": planner_buckets,
    "microsoft.planner.tasks": planner_tasks,
}

WRITES = {
    "microsoft.todo.create": todo_create,
    "microsoft.todo.complete": todo_complete,
    "microsoft.planner.create": planner_create,
    "microsoft.planner.update": planner_update,
}
