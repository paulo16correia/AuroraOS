"""The Microsoft foundation, against a stand-in that behaves like a provider on a bad day.

Every test here runs a real HTTP request over a real socket to a server on loopback. What is faked
is who answers, and nothing else — the client under test is the one the plugin uses.

The refusal tests matter most, and the interesting half of each is that **nothing was sent**. The
stand-in records every request that reaches it, so a test can tell "the plugin refused" from "the
plugin asked and was told no". Those look the same to a caller and are nothing alike in a review.
"""

import json
import unittest

import graph
import msauth
from fake_graph import FakeGraph


class StubTokens:
    """A token source that hands out a fixed token and counts how often it was asked."""

    def __init__(self, token="test-access-token"):
        self.token = token
        self.asks = 0
        self.forgotten = 0

    def access_token(self):
        self.asks += 1
        return self.token

    def forget(self):
        self.forgotten += 1


def client_for(service, tokens=None, sleep=None):
    """A client pointed at the stand-in, allowed loopback because it was built that way."""
    waited = []

    return graph.GraphClient(
        tokens or StubTokens(),
        base=service.base,
        allowed_hosts=("127.0.0.1",),
        plain_loopback=True,
        sleep=sleep or waited.append,
    ), waited


class HostRules(unittest.TestCase):
    def test_a_host_outside_the_allowlist_is_refused_before_anything_is_sent(self):
        with FakeGraph() as service:
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as refused:
                client.request("GET", "/steal", base="https://evil.example")

            self.assertEqual(graph.E_REFUSED_HOST, refused.exception.code)
            self.assertIn("evil.example", refused.exception.message)
            self.assertEqual([], service.seen)

    def test_plain_http_to_a_real_host_is_refused(self):
        # A production client: no loopback exception, so http:// is refused wherever it points.
        client = graph.GraphClient(StubTokens())

        with self.assertRaises(graph.GraphError) as refused:
            client.request("GET", "/me", base="http://graph.microsoft.com/v1.0")

        self.assertEqual(graph.E_REFUSED_HOST, refused.exception.code)
        self.assertIn("TLS", refused.exception.message)

    def test_a_credential_passed_as_an_ordinary_header_is_refused(self):
        with FakeGraph() as service:
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as refused:
                client.request("GET", "/me", headers={"Authorization": "Bearer sneaky"})

            self.assertEqual(graph.E_REFUSED_HOST, refused.exception.code)
            self.assertEqual([], service.seen)

    def test_the_default_allowlist_is_microsofts_two_hosts_and_nothing_else(self):
        # Read as an assertion about the shipped default rather than about a constructed client:
        # a plugin that reached a third host would be one nobody agreed to at install.
        self.assertEqual(
            ("graph.microsoft.com", "login.microsoftonline.com"), graph.ALLOWED_HOSTS)


class Credentials(unittest.TestCase):
    def test_the_token_is_attached_once_every_check_has_passed(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 200, {"displayName": "Ada"})
            client, _ = client_for(service)

            answer = client.request("GET", "/me")

            self.assertEqual("Ada", answer["displayName"])
            self.assertEqual(
                "Bearer test-access-token", service.seen[0]["headers"]["authorization"])

    def test_a_refused_request_never_asks_for_a_token(self):
        with FakeGraph() as service:
            tokens = StubTokens()
            client, _ = client_for(service, tokens)

            with self.assertRaises(graph.GraphError):
                client.request("GET", "/steal", base="https://evil.example")

            # The point of fetching the credential last: a request pointed somewhere it may not go
            # never causes the token to exist on that path at all.
            self.assertEqual(0, tokens.asks)


class Redaction(unittest.TestCase):
    def test_a_bearer_token_is_removed_from_text(self):
        cleaned = graph.redact("failed with Authorization: Bearer abcdefghijklmnop")

        self.assertNotIn("abcdefghijklmnop", cleaned)
        self.assertIn("[redacted]", cleaned)

    def test_a_credential_in_a_query_string_is_removed(self):
        cleaned = graph.redact("GET /token?refresh_token=0.AXkAsecretvalue&scope=mail")

        self.assertNotIn("0.AXkAsecretvalue", cleaned)
        self.assertIn("scope=mail", cleaned)

    def test_a_credential_quoted_in_prose_is_removed_by_exact_value(self):
        # The case that found this: Microsoft's sign-in service answers an expired grant with
        # "AADSTS70008: The refresh token 0.AXkA... has expired." — the credential in ordinary
        # prose, in no URL and with no Bearer in front of it. Every shape-based rule misses it.
        held = "0.AXkAthisIsARefreshTokenValue"
        cleaned = graph.redact(
            "AADSTS70008: The refresh token %s has expired." % held, known=(held,))

        self.assertNotIn(held, cleaned)
        self.assertIn("AADSTS70008", cleaned)

    def test_a_short_value_is_not_redacted_into_uselessness(self):
        # A four-character secret would match half the message and hide the error rather than the
        # credential.
        cleaned = graph.redact("the tenant refused the request", known=("the",))

        self.assertEqual("the tenant refused the request", cleaned)

    def test_a_jwt_anywhere_at_all_is_removed(self):
        jwt = "eyJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwcyJ9.c2lnbmF0dXJlaGVyZQ"
        cleaned = graph.redact("the tenant rejected %s outright" % jwt)

        self.assertNotIn(jwt, cleaned)

    def test_a_provider_error_carrying_a_token_is_redacted_before_it_leaves(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 400, {
                "error": {
                    "code": "invalidRequest",
                    "message": "the token Bearer abcdefghijklmnopqrs was malformed",
                },
            })

            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me")

            # Microsoft writes this message. It reaches Aurora's audit, so it goes through
            # redaction on the way rather than being trusted to contain nothing.
            self.assertNotIn("abcdefghijklmnopqrs", failed.exception.message)


class Errors(unittest.TestCase):
    def test_unauthorised_is_an_authentication_failure(self):
        self._expect(401, graph.E_AUTH)

    def test_forbidden_is_a_denial(self):
        self._expect(403, graph.E_DENIED)

    def test_missing_is_not_found(self):
        self._expect(404, graph.E_NOT_FOUND)

    def test_anything_else_is_a_graph_failure(self):
        self._expect(500, graph.E_GRAPH)

    def _expect(self, status, code):
        with FakeGraph() as service:
            service.answer("GET", "/me", status, {
                "error": {"code": "someCode", "message": "because"}})
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me", attempts=1)

            self.assertEqual(code, failed.exception.code)
            self.assertEqual(status, failed.exception.status)

    def test_microsofts_own_request_id_comes_back(self):
        with FakeGraph() as service:
            service.answer(
                "GET", "/me", 403,
                {"error": {"code": "accessDenied", "message": "no"}},
                {"request-id": "8f2c-graph-side"})

            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me", attempts=1)

            # Without it, a conversation with Microsoft support about one specific call is a
            # conversation about approximately when something went wrong.
            code, message, detail = failed.exception.as_refusal()
            self.assertEqual("8f2c-graph-side", detail["request_id"])

    def test_the_refusal_that_crosses_the_pipe_carries_no_headers_or_urls(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 403, {"error": {"code": "accessDenied", "message": "no"}})
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me", attempts=1)

            code, message, detail = failed.exception.as_refusal()

            self.assertEqual(graph.E_DENIED, code)
            self.assertEqual({"status", "request_id"} & set(detail), set(detail) - {"status"} | {"status"})
            self.assertNotIn("authorization", json.dumps(detail).lower())
            self.assertNotIn("127.0.0.1", json.dumps(detail))


class MalformedResponses(unittest.TestCase):
    def test_a_body_that_is_not_json_is_reported_as_malformed(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 200, "<html>we are having trouble</html>")
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me")

            # Its own category. A provider that changed under us is a different problem from one
            # that refused, and reporting it as a parser exception hides which happened.
            self.assertEqual(graph.E_MALFORMED, failed.exception.code)

    def test_truncated_json_is_reported_as_malformed(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 200, '{"displayName": "Ada"')
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me")

            self.assertEqual(graph.E_MALFORMED, failed.exception.code)

    def test_a_json_array_where_an_object_belongs_is_reported_as_malformed(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 200, "[1, 2, 3]")
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me")

            self.assertEqual(graph.E_MALFORMED, failed.exception.code)

    def test_an_error_envelope_of_the_wrong_shape_still_produces_a_clean_refusal(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 403, {"error": ["not", "documented"]})
            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as failed:
                client.request("GET", "/me", attempts=1)

            # This path runs exactly when the far end is behaving unexpectedly, so it may not
            # assume the documented shape.
            self.assertEqual(graph.E_DENIED, failed.exception.code)
            self.assertIn("without saying why", failed.exception.message)

    def test_an_empty_body_on_a_no_content_reply_is_not_malformed(self):
        with FakeGraph() as service:
            service.answer("DELETE", "/me/messages/1", 204, "")
            client, _ = client_for(service)

            self.assertEqual({}, client.request("DELETE", "/me/messages/1"))


class Throttling(unittest.TestCase):
    def test_retry_after_is_obeyed_and_then_the_call_succeeds(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 429, {}, {"Retry-After": "2"})
            service.answer("GET", "/me", 200, {"displayName": "Ada"})

            waited = []
            client, _ = client_for(service, sleep=waited.append)

            answer = client.request("GET", "/me")

            self.assertEqual("Ada", answer["displayName"])

            # What Microsoft asked for, not what the plugin felt like. A backoff that ignores
            # Retry-After is how a client earns a longer one.
            self.assertEqual([2], waited)
            self.assertEqual(2, client.throttled_seconds)

    def test_a_long_throttle_is_reported_rather_than_slept_through(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 429, {}, {"Retry-After": "600"})

            waited = []
            client, _ = client_for(service, sleep=waited.append)

            with self.assertRaises(graph.GraphError) as throttled:
                client.request("GET", "/me")

            # Ten minutes is a real answer, and holding a capability call open for it is not.
            self.assertEqual(graph.E_THROTTLED, throttled.exception.code)
            self.assertEqual(600, throttled.exception.retry_after)
            self.assertEqual([], waited)

    def test_retries_stop_at_the_limit(self):
        with FakeGraph() as service:
            service.always(429, {}, {"Retry-After": "1"})

            waited = []
            client, _ = client_for(service, sleep=waited.append)

            with self.assertRaises(graph.GraphError):
                client.request("GET", "/me", attempts=3)

            self.assertEqual(3, len(service.seen))

    def test_a_send_is_not_repeated_when_the_failure_does_not_say_it_did_not_happen(self):
        with FakeGraph() as service:
            service.answer("POST", "/me/sendMail", 504, {})
            service.answer("POST", "/me/sendMail", 202, {})

            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError):
                client.request("POST", "/me/sendMail", body={}, repeatable=False)

            # A gateway timeout is silent about whether the mail went. Sending it twice is worse
            # than telling the caller it did not obviously work once.
            self.assertEqual(1, len(service.seen))


class Pagination(unittest.TestCase):
    def test_next_links_are_followed_and_the_items_come_back_together(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/messages", 200, {
                "value": [{"id": "1"}],
                "@odata.nextLink": service.base + "/me/messages?page=2",
            })
            service.answer("GET", "/me/messages", 200, {"value": [{"id": "2"}]})

            client, _ = client_for(service)
            answer = client.paged("/me/messages")

            self.assertEqual(["1", "2"], [m["id"] for m in answer["items"]])
            self.assertEqual(2, answer["pages"])
            self.assertFalse(answer["truncated"])

    def test_a_next_link_pointing_somewhere_else_is_refused(self):
        with FakeGraph() as service:
            service.answer("GET", "/me/messages", 200, {
                "value": [{"id": "1"}],
                "@odata.nextLink": "https://evil.example/collect",
            })

            client, _ = client_for(service)

            with self.assertRaises(graph.GraphError) as refused:
                client.paged("/me/messages")

            # A nextLink is a URL the provider chose. Following it unchecked would let the far end
            # pick the next host, and the token would go with it.
            self.assertEqual(graph.E_REFUSED_HOST, refused.exception.code)

    def test_paging_stops_at_the_page_limit_and_says_it_was_truncated(self):
        with FakeGraph() as service:
            service.always(200, {
                "value": [{"id": "x"}],
                "@odata.nextLink": service.base + "/me/messages?page=next",
            })

            client, _ = client_for(service)
            answer = client.paged("/me/messages", max_pages=3)

            # An unbounded walk over a busy mailbox is a call that finishes some time next week.
            self.assertEqual(3, answer["pages"])
            self.assertTrue(answer["truncated"])


class AuditMetadata(unittest.TestCase):
    def test_the_metadata_names_the_provider_and_the_waiting_and_nothing_else(self):
        with FakeGraph() as service:
            service.answer("GET", "/me", 429, {}, {"Retry-After": "1"})
            service.answer("GET", "/me", 200, {"displayName": "Ada"})

            client, _ = client_for(service, sleep=lambda seconds: None)
            client.request("GET", "/me")

            metadata = graph.audit_metadata(client, {"mailbox_count": 3})

            self.assertEqual("microsoft.graph", metadata["provider"])
            self.assertEqual("v1.0", metadata["api_version"])
            self.assertEqual(1, metadata["throttled_seconds"])
            self.assertEqual(3, metadata["mailbox_count"])

            # Nothing credential-shaped, because this goes straight into an audit record.
            self.assertNotIn("token", json.dumps(metadata).lower())


class Authentication(unittest.TestCase):
    def test_a_plugin_with_no_credentials_says_so_rather_than_asking(self):
        source = msauth.TokenSource("", "", None, None)

        self.assertFalse(source.configured)

        with self.assertRaises(graph.GraphError) as refused:
            source.access_token()

        self.assertEqual(graph.E_AUTH, refused.exception.code)
        self.assertIn("aurora secret set", refused.exception.message)

    def test_a_refused_grant_reports_microsofts_reason_without_the_credential(self):
        with FakeGraph() as service:
            service.answer("POST", "/tenant-1/oauth2/v2.0/token", 400, {
                "error": "invalid_grant",
                "error_description":
                    "AADSTS70008: The refresh token 0.AXkAsecretvalue has expired.",
            })

            source = _source_against(service, refresh_token="0.AXkAsecretvalue")

            with self.assertRaises(graph.GraphError) as refused:
                source.access_token()

            self.assertEqual(graph.E_AUTH, refused.exception.code)

            # AADSTS codes are the most useful thing Microsoft says and the description can quote
            # the request back — so it is redacted rather than trusted.
            self.assertIn("AADSTS70008", refused.exception.message)
            self.assertNotIn("0.AXkAsecretvalue", refused.exception.message)

    def test_a_token_is_reused_until_it_is_nearly_expired(self):
        with FakeGraph() as service:
            service.always(200, {"access_token": "first", "expires_in": 3600})

            clock = [1000.0]
            source = _source_against(service, now=lambda: clock[0])

            self.assertEqual("first", source.access_token())
            self.assertEqual("first", source.access_token())

            self.assertEqual(1, len(service.seen))
            self.assertEqual(1, source.refreshes)

    def test_a_token_is_renewed_before_it_expires_rather_than_after(self):
        with FakeGraph() as service:
            service.answer("POST", "/tenant-1/oauth2/v2.0/token", 200,
                           {"access_token": "first", "expires_in": 300})
            service.answer("POST", "/tenant-1/oauth2/v2.0/token", 200,
                           {"access_token": "second", "expires_in": 300})

            clock = [1000.0]
            source = _source_against(service, now=lambda: clock[0])

            self.assertEqual("first", source.access_token())

            # Past the early-refresh margin but still inside the stated lifetime. A token that
            # expires between the check and the call produces a 401 that reads as a permissions
            # problem and is not one.
            clock[0] += 200

            self.assertEqual("second", source.access_token())

    def test_a_rotated_refresh_token_is_kept_in_memory_and_not_written_down(self):
        with FakeGraph() as service:
            service.always(200, {
                "access_token": "first", "expires_in": 60, "refresh_token": "rotated-value"})

            source = _source_against(service, refresh_token="original-value")
            source.access_token()

            sent = json.dumps(service.seen[0]["body"])
            self.assertIn("original-value", sent)

            source.forget()
            source.access_token()

            # The rotated one is used for the next exchange, and nothing anywhere wrote it to disk.
            self.assertIn("rotated-value", service.seen[1]["body"])

    def test_client_credentials_are_used_when_there_is_no_refresh_token(self):
        with FakeGraph() as service:
            service.always(200, {"access_token": "app-only", "expires_in": 3600})

            source = msauth.TokenSource(
                "tenant-1", "client-1", refresh_token=None, client_secret="a-secret",
                opener=_opener_for(service))

            self.assertEqual("client_credentials", source.grant)
            self.assertEqual("app-only", source.access_token())
            self.assertIn("grant_type=client_credentials", service.seen[0]["body"])


def _opener_for(service):
    """Redirects the identity endpoint at the stand-in without touching the host rule."""
    import urllib.request

    class Rewrite(urllib.request.BaseHandler):
        def https_request(self, request):
            return request

    opener = urllib.request.build_opener()
    original_open = opener.open

    def open_rewritten(request, **kwargs):
        url = request.full_url.replace("https://login.microsoftonline.com", service.base)
        rewritten = urllib.request.Request(
            url, data=request.data, method=request.get_method())

        for name, value in request.header_items():
            rewritten.add_header(name, value)

        return original_open(rewritten, **kwargs)

    opener.open = open_rewritten
    return opener


def _source_against(service, refresh_token="a-refresh-token", now=None):
    return msauth.TokenSource(
        "tenant-1", "client-1", refresh_token=refresh_token,
        opener=_opener_for(service), now=now)


if __name__ == "__main__":
    unittest.main()
