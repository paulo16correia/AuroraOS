"""The directory: who works here, what they do, and who they report to.

**None of this is authorization, and the naming is the first line of that defence.**

A directory is the most tempting authority source in an organisation, because it looks like one. It
says who is a director, who manages whom, who sits in Finance. Every one of those is a fact about a
record in Entra, maintained by whoever maintains it, and none of them is a statement about what
Aurora may do. Aurora's authority comes from the owner, through approvals and policy, and from
nowhere else — a rule that only holds if nothing here is shaped like a permission.

So: no field is called `role`, `authorized`, `permission`, `can`, `may` or `level`. A manager is
`manager`, a title is `job_title`, and a presence status is `presence`. What somebody's job title
implies about what they should be allowed to do is a judgement for a person, made somewhere else.

Everything returned is written by whoever maintains the directory. A display name is untrusted like
any other provider content — "Ada Lovelace (IT Support, verified)" is a display name somebody can
set.
"""

import graph

USER_FIELDS = "id,displayName,userPrincipalName,mail,jobTitle,department,officeLocation"


def _bounded(value, limit):
    if value is None:
        return None

    if not isinstance(value, str):
        value = str(value)

    return value[:limit]


def _person(entry):
    """One directory entry, in fields named for what they are."""
    return {
        "user_id": _bounded(entry.get("id"), 512),
        "display_name": _bounded(entry.get("displayName"), 300),
        "user_principal_name": _bounded(entry.get("userPrincipalName"), 320),
        "mail": _bounded(entry.get("mail"), 320),

        # Facts about a directory record. Not authority, not seniority Aurora acts on, and not an
        # input to any decision Aurora makes about what it may do.
        "job_title": _bounded(entry.get("jobTitle"), 300),
        "department": _bounded(entry.get("department"), 300),
        "office_location": _bounded(entry.get("officeLocation"), 300),

        "source": "microsoft.directory",
        "content_is_untrusted": True,
    }


def search_users(state, args):
    """Finds people in the organisation's directory by name or address."""
    client = state["client"]
    top = min(int(args.get("limit") or 20), 50)

    # $filter with startswith rather than $search: $search on /users needs the ConsistencyLevel
    # header and eventual-consistency semantics, and this is a lookup rather than a ranking.
    term = args["query"].replace("'", "''")[:100]

    answer = client.paged(
        "/users",
        query={
            "$filter": (
                "startswith(displayName,'%s') or startswith(mail,'%s') "
                "or startswith(userPrincipalName,'%s')" % (term, term, term)),
            "$select": USER_FIELDS,
            "$top": top,
        },
        max_pages=2)

    return {
        "people": [_person(entry) for entry in answer["items"][:top]],
        "audit": graph.audit_metadata(client, {"directory": "users"}),
    }


def relevant_people(state, args):
    """The people Microsoft considers relevant to the signed-in person.

    A different question from a directory search: this is ranked by who they actually work with,
    which is what "who is involved in this" usually means. It is still a ranking of colleagues and
    still not a statement about anybody's authority.
    """
    client = state["client"]
    top = min(int(args.get("limit") or 20), 50)

    query = {"$top": top}

    if args.get("query"):
        query["$search"] = '"%s"' % args["query"].replace('"', "")

    answer = client.paged("/me/people", query=query, max_pages=2)

    people = []

    for entry in answer["items"][:top]:
        addresses = entry.get("scoredEmailAddresses")
        address = None

        if isinstance(addresses, list) and addresses and isinstance(addresses[0], dict):
            address = addresses[0].get("address")

        people.append({
            "user_id": _bounded(entry.get("id"), 512),
            "display_name": _bounded(entry.get("displayName"), 300),
            "mail": _bounded(address, 320),
            "job_title": _bounded(entry.get("jobTitle"), 300),
            "department": _bounded(entry.get("department"), 300),
            "source": "microsoft.people",
            "content_is_untrusted": True,
        })

    return {"people": people, "audit": graph.audit_metadata(client, {"directory": "people"})}


def read_person(state, args):
    """One person's directory entry."""
    client = state["client"]

    entry = client.request(
        "GET", "/users/%s" % _id(args["user_id"]), query={"$select": USER_FIELDS})

    return {"person": _person(entry), "audit": graph.audit_metadata(client)}


def read_manager(state, args):
    """Who somebody reports to, according to the directory.

    A reporting line, and nothing else. It is not a delegation, not an approval chain and not a
    reason for Aurora to do anything on anybody's behalf — Aurora's approvals come from its owner,
    who is a person at a keyboard rather than a node in an org chart.
    """
    client = state["client"]

    entry = client.request(
        "GET", "/users/%s/manager" % _id(args["user_id"]), query={"$select": USER_FIELDS})

    return {
        "manager": _person(entry),
        "reports_to_is_not_authority": True,
        "audit": graph.audit_metadata(client),
    }


def read_presence(state, args):
    """Whether somebody is available, according to Teams.

    Needs `Presence.Read.All`, which an administrator grants. Availability is a fact about a status
    indicator — somebody showing as Available has not agreed to anything.
    """
    client = state["client"]

    entry = client.request("GET", "/users/%s/presence" % _id(args["user_id"]))

    return {
        "user_id": _bounded(args["user_id"], 512),
        "presence": _bounded(entry.get("availability"), 60),
        "activity": _bounded(entry.get("activity"), 60),
        "source": "microsoft.teams",
        "audit": graph.audit_metadata(client),
    }


def _id(value):
    text = str(value)

    if not text or len(text) > 512 or any(c in text for c in "/?#\\ "):
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a shape a directory identifier comes in")

    return text


READS = {
    "microsoft.people.search": search_users,
    "microsoft.people.relevant": relevant_people,
    "microsoft.people.read": read_person,
    "microsoft.people.manager": read_manager,
    "microsoft.people.presence": read_presence,
}

WRITES = {}
