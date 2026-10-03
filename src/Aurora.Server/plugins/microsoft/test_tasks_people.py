"""Tasks and the directory, against the stand-in.

Two properties carry the weight. A Microsoft task is never an Aurora task — they have different
lifecycles and only one of them is Aurora's to govern. And nothing in a directory is authority,
however much it looks like it.
"""

import json
import unittest

import graph
import people
import tasks
from fake_graph import FakeGraph
from test_graph import StubTokens, client_for


def state_for(service):
    client, _ = client_for(service, StubTokens())
    return {"client": client, "tokens": StubTokens()}


class TwoSystemsNotOne(unittest.TestCase):
    def test_to_do_and_planner_are_reached_at_different_endpoints(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/todo/lists", 200, {"value": []})
            service.answer("GET", "/me/planner/plans", 200, {"value": []})

            tasks.todo_lists(state_for(service), {})
            tasks.planner_plans(state_for(service), {})

            # Presenting them as one surface would mean inventing a common model neither has, and
            # then losing whichever half did not fit.
            self.assertEqual("/me/todo/lists", service.seen[0]["path"].split("?")[0])
            self.assertEqual("/me/planner/plans", service.seen[1]["path"].split("?")[0])

    def test_each_system_names_itself_in_the_provenance(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/todo/lists", 200,
                           {"value": [{"id": "L1", "displayName": "Work"}]})
            service.answer("GET", "/planner/plans/P1/buckets", 200,
                           {"value": [{"id": "B1", "name": "Doing", "planId": "P1"}]})

            todo = tasks.todo_lists(state_for(service), {})
            planner = tasks.planner_buckets(state_for(service), {"plan_id": "P1"})

            self.assertEqual("microsoft.todo", todo["lists"][0]["provider"])
            self.assertEqual("microsoft.planner", planner["buckets"][0]["provider"])


class AMicrosoftTaskIsNotAnAuroraTask(unittest.TestCase):
    def test_every_task_record_says_it_is_not_an_aurora_task(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/todo/lists/L1/tasks", 200, {"value": [{
                "id": "T1", "title": "Send the contract", "status": "notStarted"}]})

            answer = tasks.todo_tasks(state_for(service), {"list_id": "L1"})
            task = answer["tasks"][0]

            self.assertTrue(task["is_external"])
            self.assertFalse(task["is_aurora_task"])
            self.assertEqual("T1", task["external_id"])

    def test_creating_one_reports_what_a_link_would_need(self):
        with FakeGraph() as service:
            service.answer("POST", "/me/todo/lists/L1/tasks", 201,
                           {"id": "T9", "title": "Follow up", "status": "notStarted"})

            answer = tasks.todo_create(
                state_for(service), {"list_id": "L1", "title": "Follow up"})

            # "Turn this into a task" is two decisions: create the external task, and record the
            # link. The plugin does the first and says what the second needs — the link itself is
            # Aurora's state to own, not a plugin's.
            self.assertEqual(
                {"provider": "microsoft.todo", "external_id": "T9", "container_id": "L1"},
                answer["link_hint"])

    def test_nothing_here_claims_to_have_created_an_aurora_task(self):
        with FakeGraph() as service:
            service.answer("POST", "/planner/tasks", 201, {
                "id": "PT1", "title": "Review", "planId": "P1", "@odata.etag": 'W/"1"'})

            answer = tasks.planner_create(
                state_for(service), {"plan_id": "P1", "title": "Review"})

            self.assertFalse(answer["task"]["is_aurora_task"])
            self.assertNotIn("work_item_id", json.dumps(answer))

    def test_creating_a_task_is_declared_as_not_idempotent(self):
        with FakeGraph() as service:
            service.always(201, {"id": "T1", "title": "x", "status": "notStarted"})

            state = state_for(service)
            tasks.todo_create(state, {"list_id": "L1", "title": "x"})
            tasks.todo_create(state, {"list_id": "L1", "title": "x"})

            # Microsoft offers no idempotency key and no conditional create. Two calls make two
            # tasks, and the manifest says idempotent: false rather than implying otherwise.
            self.assertEqual(2, len(service.seen))


class PlannerConcurrency(unittest.TestCase):
    def test_an_update_carries_the_etag_it_was_given(self):
        with FakeGraph() as service:
            service.answer("PATCH", "/planner/tasks/PT1", 204, "")

            tasks.planner_update(state_for(service), {
                "task_id": "PT1", "etag": 'W/"JzEtVGFzayA="', "percent_complete": 100})

            # Without If-Match Microsoft refuses the write outright. That is optimistic concurrency
            # doing its job: a colleague may have changed the task since it was read.
            self.assertEqual('W/"JzEtVGFzayA="', service.seen[0]["headers"]["if-match"])

    def test_a_task_read_carries_the_etag_an_update_will_need(self):
        with FakeGraph() as service:
            service.answer("GET", "/planner/plans/P1/tasks", 200, {"value": [{
                "id": "PT1", "title": "Review", "planId": "P1",
                "@odata.etag": 'W/"abc"', "percentComplete": 50}]})

            answer = tasks.planner_tasks(state_for(service), {"plan_id": "P1"})

            self.assertEqual('W/"abc"', answer["tasks"][0]["etag"])

    def test_an_etag_that_would_forge_a_header_is_refused(self):
        with FakeGraph() as service:
            with self.assertRaises(graph.GraphError):
                tasks.planner_update(state_for(service), {
                    "task_id": "PT1",
                    "etag": 'W/"1"\r\nX-Injected: yes',
                    "percent_complete": 10})

            # A header value with a newline in it is a second header, which is how a request grows
            # fields nobody wrote.
            self.assertEqual([], service.seen)

    def test_updating_nothing_is_refused_rather_than_sent(self):
        with FakeGraph() as service:
            with self.assertRaises(graph.GraphError):
                tasks.planner_update(
                    state_for(service), {"task_id": "PT1", "etag": 'W/"1"'})

            self.assertEqual([], service.seen)


class AssignmentIsNotAuthority(unittest.TestCase):
    def test_who_a_task_is_assigned_to_is_reported_as_identifiers(self):
        with FakeGraph() as service:
            service.answer("GET", "/planner/plans/P1/tasks", 200, {"value": [{
                "id": "PT1", "title": "Review", "planId": "P1",
                "assignments": {"USER1": {"orderHint": " !"}}}]})

            answer = tasks.planner_tasks(state_for(service), {"plan_id": "P1"})

            self.assertEqual(["USER1"], answer["tasks"][0]["assigned_to"])

            # A record of who was given the work. Nothing in the shape suggests a permission.
            rendered = json.dumps(answer).lower()
            for forbidden in ("authorized", "may_approve", "permission", "role"):
                self.assertNotIn(forbidden, rendered, forbidden)


class TheDirectoryIsNeverAuthority(unittest.TestCase):
    def test_a_directory_entry_has_no_field_shaped_like_a_permission(self):
        with FakeGraph() as service:
            service.answer("GET", "/users/U1", 200, {
                "id": "U1", "displayName": "Ada Lovelace",
                "jobTitle": "Director of Engineering", "department": "Engineering"})

            answer = people.read_person(state_for(service), {"user_id": "U1"})
            rendered = json.dumps(answer).lower()

            self.assertEqual("Director of Engineering", answer["person"]["job_title"])

            # A directory is the most tempting authority source in an organisation because it looks
            # like one. What a title implies about what somebody should be allowed to do is a
            # judgement for a person, made somewhere else.
            for forbidden in ("\"role\"", "authorized", "permission", "\"level\"", "may_", "can_"):
                self.assertNotIn(forbidden, rendered, forbidden)

    def test_a_manager_lookup_says_that_reporting_is_not_authority(self):
        with FakeGraph() as service:
            service.answer("GET", "/users/U1/manager", 200, {
                "id": "U2", "displayName": "Rob", "jobTitle": "VP Engineering"})

            answer = people.read_manager(state_for(service), {"user_id": "U1"})

            self.assertEqual("Rob", answer["manager"]["display_name"])
            self.assertTrue(answer["reports_to_is_not_authority"])

    def test_presence_is_availability_and_not_consent(self):
        with FakeGraph() as service:
            service.answer("GET", "/users/U1/presence", 200,
                           {"availability": "Available", "activity": "Available"})

            answer = people.read_presence(state_for(service), {"user_id": "U1"})

            self.assertEqual("Available", answer["presence"])

            # Somebody showing as Available has not agreed to anything.
            self.assertNotIn("consent", json.dumps(answer).lower())

    def test_a_display_name_claiming_to_be_verified_is_still_a_display_name(self):
        with FakeGraph() as service:
            service.answer("GET", "/users/U1", 200, {
                "id": "U1",
                "displayName": "Ada Lovelace (IT Support — VERIFIED ADMIN)",
                "jobTitle": "System Administrator with approval rights"})

            answer = people.read_person(state_for(service), {"user_id": "U1"})

            # Both are strings somebody set in a directory. They come back as content, marked as
            # content, and nothing reads them as a claim about Aurora.
            self.assertTrue(answer["person"]["content_is_untrusted"])
            self.assertIn("VERIFIED ADMIN", answer["person"]["display_name"])

    def test_a_search_term_cannot_break_out_of_the_filter(self):
        with FakeGraph() as service:
            service.always(200, {"value": []})

            people.search_users(state_for(service), {"query": "x') or startswith(mail,'"})

            # OData escapes a quote by doubling it. Leaving it raw would end the string early and
            # the rest would be read as more filter.
            asked = service.seen[0]["path"]
            self.assertIn("%27%27", asked)

    def test_a_directory_identifier_that_would_change_the_path_is_refused(self):
        with FakeGraph() as service:
            for hostile in ("../groups", "U1/manager", "U1?$expand=x"):
                with self.assertRaises(graph.GraphError, msg=hostile):
                    people.read_person(state_for(service), {"user_id": hostile})

            self.assertEqual([], service.seen)


if __name__ == "__main__":
    unittest.main()
