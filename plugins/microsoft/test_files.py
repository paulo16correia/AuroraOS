"""OneDrive and SharePoint, against the stand-in.

The property that matters is that a remote file never looks like a local one. Aurora already has
capabilities that read and write a directory on this machine; these read and write somebody's cloud
storage, and the two must not be confusable by anything downstream.
"""

import json
import unittest

import files
import graph
from fake_graph import FakeGraph
from test_graph import StubTokens, client_for


def _code_of(module):
    """A module with its leading docstring removed, for tests about what the code does."""
    return open(module.__file__).read().split('"""', 2)[2]


def state_for(service):
    client, _ = client_for(service, StubTokens())
    return {"client": client, "tokens": StubTokens()}


def item(identifier="IT1", name="quarterly-report.docx", folder=False):
    document = {
        "id": identifier,
        "name": name,
        "size": 90210,
        "webUrl": "https://contoso.sharepoint.com/Documents/" + name,
        "createdDateTime": "2026-08-01T09:00:00Z",
        "lastModifiedDateTime": "2026-09-01T14:22:00Z",
        "createdBy": {"user": {"displayName": "Rob", "email": "rob@example.com"}},
        "lastModifiedBy": {"user": {"displayName": "Ada", "email": "ada@example.com"}},
        "parentReference": {"driveId": "DRIVE1", "path": "/drive/root:/Documents"},
    }

    if folder:
        document["folder"] = {"childCount": 4}
    else:
        document["file"] = {"mimeType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document"}

    return document


class ARemoteFileIsNotALocalFile(unittest.TestCase):
    def test_every_item_says_where_it_came_from(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/drive/root/children", 200, {"value": [item()]})

            answer = files.list_items(state_for(service), {})
            entry = answer["items"][0]

            # A summary that said only "quarterly-report.docx" would be indistinguishable from a
            # local file of the same name, and Aurora would hold two different things under one
            # description.
            self.assertTrue(entry["is_remote"])
            self.assertEqual("microsoft", entry["source"])
            self.assertEqual("DRIVE1", entry["drive_id"])
            self.assertEqual("/drive/root:/Documents", entry["parent_path"])

    def test_who_last_changed_it_is_a_fact_and_not_an_authority(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/drive/items/IT1", 200, item())

            answer = files.read_metadata(state_for(service), {"item_id": "IT1"})
            rendered = json.dumps(answer)

            self.assertEqual("Ada", answer["item"]["modified_by"]["name"])

            for forbidden in ("owner_permission", "authorized", "may_approve", "role"):
                self.assertNotIn(forbidden, rendered.lower(), forbidden)

    def test_a_folder_is_told_apart_from_a_file(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/drive/root/children", 200,
                           {"value": [item(folder=True), item("IT2")]})

            answer = files.list_items(state_for(service), {})

            self.assertTrue(answer["items"][0]["is_folder"])
            self.assertEqual(4, answer["items"][0]["child_count"])
            self.assertFalse(answer["items"][1]["is_folder"])


class WhereItLooks(unittest.TestCase):
    def test_it_defaults_to_the_signed_in_persons_own_drive(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/drive/root/children", 200, {"value": []})

            files.list_items(state_for(service), {})

            self.assertEqual("/me/drive/root/children", service.seen[0]["path"].split("?")[0])

    def test_a_named_drive_is_used_when_one_is_given(self):
        with FakeGraph() as service:
            service.answer("GET", "/drives/DRIVE9/root/children", 200, {"value": []})

            files.list_items(state_for(service), {"drive_id": "DRIVE9"})

            self.assertEqual("/drives/DRIVE9/root/children", service.seen[0]["path"].split("?")[0])

    def test_a_sharepoint_site_uses_its_default_library(self):
        with FakeGraph() as service:
            service.answer("GET", "/sites/SITE1/drive/root/children", 200, {"value": []})

            files.list_items(state_for(service), {"site_id": "SITE1"})

            self.assertEqual(
                "/sites/SITE1/drive/root/children", service.seen[0]["path"].split("?")[0])

    def test_a_drive_identifier_that_would_change_the_path_is_refused(self):
        with FakeGraph() as service:
            for hostile in ("../../me/drive", "DRIVE1/root/children", "D?x=1"):
                with self.assertRaises(graph.GraphError, msg=hostile):
                    files.list_items(state_for(service), {"drive_id": hostile})

            self.assertEqual([], service.seen)

    def test_a_search_term_cannot_break_out_of_the_expression(self):
        with FakeGraph() as service:
            service.always(200, {"value": []})

            files.search_items(state_for(service), {"query": "x') OR name eq ('"})

            # The term sits inside search(q='…'). Two quotes delimit it and belong there; what
            # must not appear is a third, which would end the expression early and leave the rest
            # to be read as more expression.
            asked = service.seen[0]["path"]
            term = asked.split("search(q='", 1)[1].split("')", 1)[0]

            self.assertNotIn("'", term)
            self.assertNotIn("(", term)
            self.assertNotIn(")", term)

            # And encoded, not merely stripped: an ordinary two-word search would otherwise put a
            # space in a URL path, which urllib refuses to build.
            self.assertNotIn(" ", asked)


class ReadingContent(unittest.TestCase):
    def test_reading_a_files_bytes_is_not_offered(self):
        # Graph serves content from a tenant-specific host that a manifest cannot name, and Aurora's
        # rule is that an owner agrees to each host by name. Offering it anyway would mean reaching
        # somewhere nobody agreed to. See the module docstring.
        self.assertNotIn("microsoft.files.read_text", files.READS)
        self.assertNotIn("microsoft.files.read_text", files.WRITES)

    def test_nothing_in_the_module_reaches_a_content_host(self):
        # Only the module docstring may mention one, and only to explain why it is not reached.
        code = _code_of(files)

        self.assertNotIn("sharepoint.com", code)
        self.assertNotIn("1drv.com", code)


class Writing(unittest.TestCase):
    def test_uploading_sends_the_bytes_and_not_a_json_wrapper(self):
        with FakeGraph() as service:
            service.answer("PUT", "/me/drive/root:/notes.txt:/content", 201, item(name="notes.txt"))

            files.upload_text(
                state_for(service), {"name": "notes.txt", "text": "the plain content"})

            # Graph takes the file's bytes on this endpoint. A JSON envelope would be uploaded
            # verbatim and the file would contain the envelope.
            self.assertEqual("the plain content", service.seen[0]["body"])

    def test_an_upload_over_the_ceiling_is_refused_before_it_is_sent(self):
        with FakeGraph() as service:
            with self.assertRaises(graph.GraphError):
                files.upload_text(
                    state_for(service),
                    {"name": "big.txt", "text": "x" * (files.MAX_UPLOAD_BYTES + 1)})

            self.assertEqual([], service.seen)

    def test_a_name_that_would_become_part_of_the_path_is_refused(self):
        for hostile in ("../escape.txt", "a/b.txt", "x?y.txt", "with%20space.txt"):
            with self.assertRaises(graph.GraphError, msg=hostile):
                files._name(hostile)

    def test_an_ordinary_name_passes(self):
        self.assertEqual("Q3 report (final).docx", files._name("  Q3 report (final).docx  "))

    def test_creating_a_folder_renames_rather_than_replaces_on_a_clash(self):
        with FakeGraph() as service:
            service.answer("POST", "/me/drive/root/children", 201, item(name="Notes", folder=True))

            files.create_folder(state_for(service), {"name": "Notes"})

            body = json.loads(service.seen[0]["body"])

            # "fail" would make a second attempt an error; "replace" would quietly discard whatever
            # was already there.
            self.assertEqual("rename", body["@microsoft.graph.conflictBehavior"])

    def test_renaming_changes_only_the_name(self):
        with FakeGraph() as service:
            service.answer("PATCH", "/me/drive/items/IT1", 200, item(name="renamed.docx"))

            files.rename_item(state_for(service), {"item_id": "IT1", "name": "renamed.docx"})

            self.assertEqual({"name": "renamed.docx"}, json.loads(service.seen[0]["body"]))

    def test_copying_reports_that_it_was_started_and_not_that_it_finished(self):
        with FakeGraph() as service:
            service.answer("POST", "/me/drive/items/IT1/copy", 202, "")

            answer = files.copy_item(
                state_for(service), {"item_id": "IT1", "folder_id": "FOLDER1"})

            # Microsoft copies in the background and answers before it has finished. Reporting a
            # copy as complete when Microsoft has only accepted the request would report something
            # that has not happened.
            self.assertTrue(answer["started"])
            self.assertFalse(answer["completed"])
            self.assertTrue(answer["audit"]["asynchronous"])


class Deleting(unittest.TestCase):
    def test_deleting_moves_to_the_recycle_bin_and_says_so(self):
        with FakeGraph() as service:
            service.answer("DELETE", "/me/drive/items/IT1", 204, "")

            answer = files.delete_item(state_for(service), {"item_id": "IT1"})

            self.assertTrue(answer["moved_to_recycle_bin"])
            self.assertTrue(answer["audit"]["recoverable"])

    def test_nothing_here_deletes_permanently(self):
        with FakeGraph() as service:
            service.always(204, "")

            for handler in files.WRITES.values():
                try:
                    handler(state_for(service), {
                        "item_id": "IT1", "folder_id": "F1", "name": "n.txt", "text": "x"})
                except Exception:
                    pass

            # Graph offers permanentDelete. Asserted over what was actually requested rather than
            # over the source, which mentions it in order to say it is deliberately absent.
            # "It is in the recycle bin" is recoverable; "it is gone" is a sentence nobody should
            # be able to reach through an agent.
            for request in service.seen:
                self.assertNotIn("permanentDelete", request["path"])


if __name__ == "__main__":
    unittest.main()
