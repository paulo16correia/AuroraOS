"""OneDrive and SharePoint.

**A remote file is not a local file, and nothing here pretends otherwise.** Aurora already has
`files.read_sandbox` and `files.write_sandbox`, which operate on one directory on this machine under
rules that took an ADR to get right. These capabilities operate on somebody's cloud storage, which
has different owners, different permissions, different provenance and a different blast radius. They
are named differently, they return a different shape, and every item carries where it came from:
which drive, which item, which version, who last touched it.

**Content is untrusted.** A document is written by whoever wrote it and shared by whoever shared it,
and a file in a corporate library is a comfortable place to leave text addressed at a reading agent.
Everything returned is marked as content and bounded in length.

**Reading a file's bytes is not implemented, and the reason is a decision Aurora already made.**

Graph does not serve file content from `graph.microsoft.com`. It answers `/content` with a redirect
to a pre-authenticated URL on a tenant-specific host — `contoso.sharepoint.com`, `…files.1drv.com`.
Those cannot be enumerated before the tenant is known, so a manifest cannot name them.

I wrote the download anyway: metadata first, then the link followed with no credential at all, to a
host matching Microsoft's documented content suffixes, size-bounded, no redirects. Then the manifest
reader refused `*.sharepoint.com` with the sentence the whole plugin model rests on:

> No wildcards, schemes, ports or paths: the owner is agreeing to each host by name.

Which is the better argument. My reasoning was about the credential — and dropping the credential
does answer the leak — but it never answered the disclosure question. An owner who agreed to
`graph.microsoft.com` at install did not agree to "and whatever host Microsoft names at runtime",
and no amount of care inside the plugin turns that into informed consent.

So file content is UNSUPPORTED here. What is offered is everything that identifies, finds and
organises a document — which is what "find the relevant documents for this meeting" actually needs —
and reading the bytes waits for someone to design disclosure for a host that is not known in
advance. That is a change to the manifest contract, not a change to this file.
"""

import urllib.parse

import graph

MAX_UPLOAD_BYTES = 4 * 1024 * 1024

ITEM_FIELDS = (
    "id,name,size,webUrl,createdDateTime,lastModifiedDateTime,file,folder,"
    "parentReference,createdBy,lastModifiedBy"
)


def _bounded(value, limit):
    if value is None:
        return None

    if not isinstance(value, str):
        value = str(value)

    return value[:limit]


def _who(entry):
    """Who created or last changed something, as a fact and not as an authority."""
    if not isinstance(entry, dict):
        return None

    user = entry.get("user")

    if not isinstance(user, dict):
        return None

    return {
        "name": _bounded(user.get("displayName"), 200),
        "address": _bounded(user.get("email"), 320),
    }


def _item(entry):
    """One drive item, with where it came from attached.

    Provenance is not decoration. A file summary that says only "quarterly-report.docx" is
    indistinguishable from a local file of the same name, and Aurora would then be holding two
    different things under one description.
    """
    parent = entry.get("parentReference") if isinstance(entry.get("parentReference"), dict) else {}
    file_facet = entry.get("file") if isinstance(entry.get("file"), dict) else None
    folder_facet = entry.get("folder") if isinstance(entry.get("folder"), dict) else None

    return {
        "item_id": _bounded(entry.get("id"), 512),
        "name": _bounded(entry.get("name"), 400),
        "is_folder": folder_facet is not None,
        "child_count": folder_facet.get("childCount") if folder_facet else None,
        "size_bytes": entry.get("size") if isinstance(entry.get("size"), int) else None,
        "content_type": _bounded(
            file_facet.get("mimeType") if file_facet else None, 200),
        "created_at": _bounded(entry.get("createdDateTime"), 40),
        "modified_at": _bounded(entry.get("lastModifiedDateTime"), 40),
        "created_by": _who(entry.get("createdBy")),
        "modified_by": _who(entry.get("lastModifiedBy")),
        "web_url": _bounded(entry.get("webUrl"), 2000),

        # Which storage, and where inside it. Without these an item id is a string that resolves
        # differently depending on a drive nobody wrote down.
        "drive_id": _bounded(parent.get("driveId"), 512),
        "parent_path": _bounded(parent.get("path"), 1000),

        # Said on every item: this is somebody's cloud storage, not the sandbox on this machine.
        "source": "microsoft",
        "is_remote": True,
    }


def _drive(args):
    """Which drive to work in, as a URL prefix.

    Three shapes, because Microsoft has three: the signed-in person's own OneDrive, a named drive,
    and a SharePoint site's default document library. Anything else is refused rather than guessed.
    """
    drive_id = args.get("drive_id")
    site_id = args.get("site_id")

    if drive_id:
        return "/drives/%s" % _id(drive_id)

    if site_id:
        return "/sites/%s/drive" % _id(site_id)

    return "/me/drive"


# ---------------------------------------------------------------------------
# reading
# ---------------------------------------------------------------------------


def list_items(state, args):
    """What is in a folder."""
    client = state["client"]
    top = min(int(args.get("limit") or 50), 100)

    folder = args.get("folder_id")
    where = ("/items/%s/children" % _id(folder)) if folder else "/root/children"

    answer = client.paged(
        _drive(args) + where,
        query={"$select": ITEM_FIELDS, "$top": top},
        max_pages=max(1, (top // 25) + 1))

    items = [_item(entry) for entry in answer["items"][:top]]

    return {
        "items": items,
        "count": len(items),
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client),
    }


def search_items(state, args):
    """Files matching a search, across a drive."""
    client = state["client"]
    top = min(int(args.get("limit") or 25), 100)

    # Microsoft's own search over the drive. The term sits inside search(q='…') in the *path*, so
    # it needs both: the quoting characters removed, or it would end the expression early and the
    # rest would be read as more expression — and then percent-encoding, or an ordinary search for
    # "quarterly report" is a URL with a space in its path that urllib refuses to build at all.
    cleaned = args["query"].replace("'", "").replace(")", "").replace("(", "")[:200]
    term = urllib.parse.quote(cleaned, safe="")

    answer = client.paged(
        _drive(args) + "/root/search(q='%s')" % term,
        query={"$select": ITEM_FIELDS, "$top": top},
        max_pages=max(1, (top // 25) + 1))

    items = [_item(entry) for entry in answer["items"][:top]]

    return {
        "items": items,
        "count": len(items),
        "truncated": answer["truncated"],
        "audit": graph.audit_metadata(client),
    }


def read_metadata(state, args):
    """One item's details, without its content."""
    client = state["client"]

    entry = client.request(
        "GET", _drive(args) + "/items/%s" % _id(args["item_id"]),
        query={"$select": ITEM_FIELDS})

    return {"item": _item(entry), "audit": graph.audit_metadata(client)}


def list_versions(state, args):
    """The versions Microsoft is keeping of one item."""
    client = state["client"]

    answer = client.paged(
        _drive(args) + "/items/%s/versions" % _id(args["item_id"]),
        max_pages=2)

    return {
        "versions": [
            {
                "version_id": _bounded(v.get("id"), 100),
                "modified_at": _bounded(v.get("lastModifiedDateTime"), 40),
                "size_bytes": v.get("size") if isinstance(v.get("size"), int) else None,
                "modified_by": _who(v.get("lastModifiedBy")),
            }
            for v in answer["items"]
        ],
        "audit": graph.audit_metadata(client),
    }



# ---------------------------------------------------------------------------
# writing
# ---------------------------------------------------------------------------


def upload_text(state, args):
    """Writes a small text file, creating it or replacing what is there.

    Replaces. Microsoft keeps the previous version, which is what makes this reversible — but the
    file somebody had open has changed under them, and that is worth an approval every time.
    """
    client = state["client"]
    content = args["text"]

    if len(content.encode("utf-8")) > MAX_UPLOAD_BYTES:
        raise graph.GraphError(
            graph.E_GRAPH,
            "that is larger than the %d bytes this uploads in one request" % MAX_UPLOAD_BYTES)

    parent = args.get("folder_id")
    where = ("/items/%s:/%s:" % (_id(parent), _name(args["name"]))) if parent \
        else "/root:/%s:" % _name(args["name"])

    entry = client.request(
        "PUT", _drive(args) + where + "/content",
        body=None, repeatable=False,
        headers={"Content-Type": "text/plain"},
        raw_body=content.encode("utf-8"))

    return {"item": _item(entry), "audit": graph.audit_metadata(client)}


def create_folder(state, args):
    """Makes a folder."""
    client = state["client"]

    parent = args.get("folder_id")
    where = ("/items/%s/children" % _id(parent)) if parent else "/root/children"

    entry = client.request(
        "POST", _drive(args) + where,
        body={
            "name": _name(args["name"]),
            "folder": {},
            # Rename rather than replace. "fail" would make a second attempt an error, and
            # "replace" would quietly discard whatever was already there.
            "@microsoft.graph.conflictBehavior": "rename",
        },
        repeatable=False)

    return {"item": _item(entry), "audit": graph.audit_metadata(client)}


def move_item(state, args):
    """Moves an item into another folder."""
    client = state["client"]

    entry = client.request(
        "PATCH", _drive(args) + "/items/%s" % _id(args["item_id"]),
        body={"parentReference": {"id": _id(args["folder_id"])}},
        repeatable=False)

    return {"item": _item(entry), "audit": graph.audit_metadata(client)}


def rename_item(state, args):
    """Renames an item, and changes nothing else."""
    client = state["client"]

    entry = client.request(
        "PATCH", _drive(args) + "/items/%s" % _id(args["item_id"]),
        body={"name": _name(args["name"])},
        repeatable=False)

    return {"item": _item(entry), "audit": graph.audit_metadata(client)}


def copy_item(state, args):
    """Copies an item. Microsoft does it in the background and answers before it is finished.

    So the result says "started", not "copied". Reporting a copy as complete when Microsoft has only
    accepted the request would be reporting something that has not happened yet.
    """
    client = state["client"]

    body = {"parentReference": {"id": _id(args["folder_id"])}}

    if args.get("name"):
        body["name"] = _name(args["name"])

    client.request(
        "POST", _drive(args) + "/items/%s/copy" % _id(args["item_id"]),
        body=body, repeatable=False)

    return {
        "started": True,
        "item_id": _bounded(args["item_id"], 512),

        # Microsoft returns 202 with a monitor URL on a host outside the allowlist. Following it is
        # not implemented, so what is known is that the copy was accepted.
        "completed": False,
        "audit": graph.audit_metadata(client, {"asynchronous": True}),
    }


def delete_item(state, args):
    """Moves an item to the recycle bin.

    Not a permanent delete, and there is no capability here that does one. Graph offers
    `permanentDelete`; it is not wired up, because "it is in the recycle bin" is recoverable and
    "it is gone" is a sentence nobody should be able to reach through an agent.
    """
    client = state["client"]

    client.request(
        "DELETE", _drive(args) + "/items/%s" % _id(args["item_id"]),
        repeatable=False)

    return {
        "item_id": _bounded(args["item_id"], 512),
        "moved_to_recycle_bin": True,
        "audit": graph.audit_metadata(client, {"recoverable": True}),
    }


def _name(value):
    """A file name, checked so it cannot become part of the path around it."""
    text = str(value).strip()

    if not text or len(text) > 400 or any(c in text for c in "/\\:?#%"):
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a name a file can have")

    return text


def _id(value):
    text = str(value)

    if not text or len(text) > 512 or any(c in text for c in "/?#\\ "):
        raise graph.GraphError(
            graph.E_GRAPH, "that is not a shape a drive identifier comes in")

    return text


READS = {
    "microsoft.files.list": list_items,
    "microsoft.files.search": search_items,
    "microsoft.files.metadata": read_metadata,
    "microsoft.files.versions": list_versions,
}

WRITES = {
    "microsoft.files.upload_text": upload_text,
    "microsoft.files.create_folder": create_folder,
    "microsoft.files.move": move_item,
    "microsoft.files.rename": rename_item,
    "microsoft.files.copy": copy_item,
    "microsoft.files.delete": delete_item,
}
