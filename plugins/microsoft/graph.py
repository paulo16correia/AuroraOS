"""Microsoft Graph, spoken from inside the sandbox.

This is the shared foundation every Microsoft capability family sits on — mail, calendar, files,
tasks, people, Teams. It is one file on purpose: the retry rule, the throttling rule and the
redaction rule are security-relevant, and six copies of them drift into six different rules.

What lives here, and what deliberately does not:

    the plugin owns    HTTP, authentication, retries, Retry-After, pagination, provider
                       schemas, provider errors, provider rate limits
    Aurora owns        authorization, approvals, audit, policy, resources, state

So nothing in this file decides whether something is allowed. It decides how to ask Microsoft, and
how to come back with an answer Aurora can govern.
"""

import json
import re
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request

# The only hosts this plugin will ever open a connection to.
#
# Not configuration. A capability that reads mail is one confused request away from posting it
# somewhere else, and the whole value of an allowlist is that it cannot be widened by whatever
# talked the plugin into the request. The sandbox is the boundary Aurora enforces; this is the
# plugin refusing to be the confused deputy inside it.
GRAPH_HOST = "graph.microsoft.com"
LOGIN_HOST = "login.microsoftonline.com"
ALLOWED_HOSTS = (GRAPH_HOST, LOGIN_HOST)

GRAPH_V1 = "https://graph.microsoft.com/v1.0"

# Beta is a different base URL and a different promise. Nothing reaches it unless a capability says
# so in as many words, because Microsoft may change or withdraw it without notice.
GRAPH_BETA = "https://graph.microsoft.com/beta"

# Refusal codes. These cross the pipe into Aurora, so they are stable strings rather than prose.
E_AUTH = "microsoft_auth_failed"
E_DENIED = "microsoft_denied"
E_NOT_FOUND = "microsoft_not_found"
E_THROTTLED = "microsoft_throttled"
E_GRAPH = "microsoft_graph_failed"
E_UNKNOWN = "microsoft_unknown_outcome"
E_MALFORMED = "microsoft_malformed_response"
E_REFUSED_HOST = "microsoft_refused_host"

# How long Aurora will sit out a throttle inside one call before handing the wait back.
LONGEST_WAIT_SECONDS = 20

# A ceiling on one response. A mailbox is larger than memory and a capability that reads mail does
# not need to be able to pull all of it in because the far end offered.
MAX_RESPONSE_BYTES = 8 * 1024 * 1024

# A ceiling on how many pages one call will walk. Without it, "list the messages" on a busy mailbox
# is an unbounded number of requests that finishes some time next week.
MAX_PAGES = 20


class GraphError(Exception):
    """A Graph call that did not produce an answer, in terms Aurora can act on.

    `code` is one of the E_* constants above and decides how Aurora treats it: denied is a policy
    matter, throttled is a resource matter, and unknown means the effect may have happened, which
    is the one that must never be reported as failure.
    """

    def __init__(self, code, message, status=0, request_id=None, retry_after=None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.status = status
        self.request_id = request_id
        self.retry_after = retry_after

    def as_refusal(self):
        """What crosses the pipe. No token, no header, no URL with a query string in it."""
        detail = {"status": self.status}

        if self.request_id:
            # Microsoft's own identifier for the call. It is what makes a support conversation
            # about one specific request possible afterwards, and it names nothing secret.
            detail["request_id"] = self.request_id

        if self.retry_after is not None:
            detail["retry_after_seconds"] = self.retry_after

        return self.code, self.message, detail


_SECRET_SHAPES = (
    # Bearer tokens, and the query parameters that carry a credential in a URL.
    re.compile(r"(?i)\b(bearer)\s+[A-Za-z0-9\-\._~\+/=]{8,}"),
    re.compile(r"(?i)([?&](?:access_token|refresh_token|code|client_secret|id_token)=)[^&\s]+"),
    # A JWT anywhere at all: three dot-separated base64url runs.
    re.compile(r"\beyJ[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]{8,}"),
)


def redact(text, known=()):
    """Removes credentials from text that is about to be recorded.

    Two layers, and the first is the one that actually works.

    **What we hold.** `known` is the secret values this process is carrying. They are removed by
    exact match, which is complete for those values however they are quoted. This exists because
    of a real case: Microsoft's sign-in service answers an expired grant with

        AADSTS70008: The refresh token 0.AXkA... has expired.

    — the credential in ordinary prose, in no URL and with no `Bearer` in front of it. Every
    shape-based rule below misses it, and the message goes into Aurora's audit.

    **What we do not hold.** The patterns catch credential-shaped text belonging to somebody else:
    a bearer header quoted back, a token in a query string, a JWT anywhere at all. Weaker by
    nature, and worth having for exactly the values the first layer cannot know about.
    """
    if not text:
        return text

    cleaned = str(text)

    for secret in known:
        # Short values are not redacted. A four-character secret would match half the message and
        # produce a redaction that hides the error instead of the credential.
        if secret and len(secret) >= 8:
            cleaned = cleaned.replace(secret, "[redacted]")

    for shape in _SECRET_SHAPES:
        cleaned = shape.sub(
            lambda match: (match.group(1) + " [redacted]") if match.lastindex else "[redacted]",
            cleaned,
        )

    return cleaned


def _host_of(url):
    return (urllib.parse.urlparse(url).hostname or "").lower()


def check_host(url, allowed=ALLOWED_HOSTS, plain_loopback=False):
    """Refuses a URL that does not point where this plugin is allowed to point.

    Called before a credential is attached and again on every hop, because an allowlist applied
    only to the URL somebody typed is one the far end can route around with a redirect.

    `plain_loopback` exists for the stand-in and is a property of the client that was built, never
    of the request that arrived. The rule itself is not relaxed anywhere: a test that edited the
    check it is meant to be exercising would prove nothing about the check.
    """
    host = _host_of(url)
    lowered = url.lower()

    if not lowered.startswith("https://"):
        if not (plain_loopback and lowered.startswith("http://") and host in ("127.0.0.1", "::1")):
            raise GraphError(E_REFUSED_HOST, "refusing a request that is not over TLS")

    if host not in allowed:
        # The host is named because whoever reads the audit needs to know where it tried to go.
        raise GraphError(E_REFUSED_HOST, "refusing to reach '%s'" % host)


class GraphClient:
    """One authenticated conversation with Microsoft Graph.

    Holds no state that survives the process. The token comes from the token source on every call
    that needs one, so an expired token is refreshed rather than retried into a wall of 401s.
    """

    def __init__(self, token_source, opener=None, sleep=None, now=None,
                 base=GRAPH_V1, allowed_hosts=ALLOWED_HOSTS, plain_loopback=False):
        self._tokens = token_source
        self._opener = opener or urllib.request.build_opener(_NoRedirects())
        self._sleep = sleep or time.sleep
        self._now = now or time.monotonic

        # Where this client may go, fixed when it is built. A capability chooses a path; it never
        # chooses a host, and nothing it is handed by Microsoft can change that either.
        self._base = base
        self._allowed = tuple(allowed_hosts)
        self._plain_loopback = plain_loopback

        # Recorded for the audit metadata a capability returns: how much of a call's time was spent
        # waiting for Microsoft to allow it, rather than waiting for Microsoft to do it.
        self.throttled_seconds = 0.0

    # ---- the one place a request is made ----

    def request(self, method, path, body=None, query=None, timeout=30,
                base=None, repeatable=None, headers=None, attempts=3):
        """One Graph call, with the retry and throttle rules applied.

        `repeatable` decides what a timeout means. A GET that times out can be asked again; a
        sendMail that times out may already have sent, and reporting that as a failure invites a
        retry that sends it twice. Defaults to whether the method is a safe one.
        """
        if repeatable is None:
            repeatable = method.upper() in ("GET", "HEAD")

        url = (base or self._base) + path

        if query:
            clean = {k: v for k, v in query.items() if v is not None}
            if clean:
                url += ("&" if "?" in url else "?") + urllib.parse.urlencode(clean)

        return self._send(method, url, body, timeout, repeatable, headers or {}, attempts)

    def paged(self, path, query=None, timeout=30, base=None, max_pages=MAX_PAGES):
        """Walks @odata.nextLink and returns the items, bounded.

        Graph paginates almost everything and the continuation is an opaque absolute URL. It is
        checked against the allowlist like any other: a nextLink is a URL the far end chose, and
        following it unchecked would be letting the provider pick the next host.
        """
        items = []
        url = None
        pages = 0
        truncated = False

        while pages < max_pages:
            if url is None:
                answer = self.request("GET", path, query=query, timeout=timeout, base=base)
            else:
                answer = self._send("GET", url, None, timeout, True, {}, 3)

            pages += 1
            items.extend(answer.get("value") or [])

            url = answer.get("@odata.nextLink")

            if not url:
                break
        else:
            truncated = url is not None

        if url:
            truncated = True

        return {"items": items, "pages": pages, "truncated": truncated}

    # ---- the parts that make it safe rather than merely working ----

    def _send(self, method, url, body, timeout, repeatable, headers, attempts):
        check_host(url, self._allowed, self._plain_loopback)

        payload = None if body is None else json.dumps(body).encode("utf-8")
        tried = 0

        while True:
            tried += 1

            request = urllib.request.Request(url, data=payload, method=method.upper())
            request.add_header("Accept", "application/json")
            request.add_header("User-Agent", "Aurora/1.0 (+local governed agent)")

            if payload is not None:
                request.add_header("Content-Type", "application/json")

            for name, value in headers.items():
                if name.lower() == "authorization":
                    # Not this way. The credential is attached below, after the host check, so a
                    # request that is about to be refused never carried it.
                    raise GraphError(
                        E_REFUSED_HOST,
                        "a credential was passed as an ordinary header",
                    )
                request.add_header(name, value)

            # Last, and only now. Every reason to refuse this request has already been taken.
            request.add_header("Authorization", "Bearer " + self._tokens.access_token())

            status, text, response_headers = self._perform(request, timeout, repeatable)

            if status == 429 or status in (503, 504):
                wait = _retry_after(response_headers)

                if tried < attempts and wait is not None and wait <= LONGEST_WAIT_SECONDS:
                    self.throttled_seconds += wait
                    self._sleep(wait)
                    continue

                if tried < attempts and wait is None and repeatable:
                    backoff = min(2 ** (tried - 1), 8)
                    self.throttled_seconds += backoff
                    self._sleep(backoff)
                    continue

                raise self._error(status, text, response_headers, wait)

            if status >= 400:
                raise self._error(status, text, response_headers, None)

            return _decode(text, status)

    def _perform(self, request, timeout, repeatable):
        try:
            with self._opener.open(request, timeout=timeout) as answer:
                declared = answer.headers.get("Content-Length")

                if declared and int(declared) > MAX_RESPONSE_BYTES:
                    raise GraphError(
                        E_GRAPH,
                        "the response declares more than the %d bytes this plugin reads"
                        % MAX_RESPONSE_BYTES,
                    )

                raw = answer.read(MAX_RESPONSE_BYTES + 1)

                if len(raw) > MAX_RESPONSE_BYTES:
                    raise GraphError(
                        E_GRAPH,
                        "the response passed the %d bytes this plugin reads" % MAX_RESPONSE_BYTES,
                    )

                return answer.status, raw.decode("utf-8", "replace"), dict(answer.headers)

        except urllib.error.HTTPError as failed:
            raw = failed.read(MAX_RESPONSE_BYTES + 1) or b""
            return failed.code, raw.decode("utf-8", "replace"), dict(failed.headers or {})

        except urllib.error.URLError as unreachable:
            # A network failure, which says nothing about whether the far end acted. For anything
            # that changes state that is an open question and not a failure.
            raise GraphError(
                E_UNKNOWN if not repeatable else E_GRAPH,
                redact("Microsoft could not be reached: %s" % unreachable.reason),
            )

        except TimeoutError:
            raise GraphError(
                E_UNKNOWN if not repeatable else E_GRAPH,
                "the request passed its deadline"
                + ("" if repeatable else " after it may already have taken effect"),
            )

    def _error(self, status, text, headers, retry_after):
        """Turns Graph's error document into something Aurora can route on.

        Graph answers errors with {"error": {"code", "message"}}, and the message is written by
        Microsoft about the caller's request — so it is passed through redaction before it is
        allowed anywhere near an audit record.
        """
        code, message = _graph_error_text(text)
        request_id = headers.get("request-id") or headers.get("client-request-id")

        if status in (401,):
            kind = E_AUTH
        elif status in (403,):
            kind = E_DENIED
        elif status in (404,):
            kind = E_NOT_FOUND
        elif status == 429:
            kind = E_THROTTLED
        else:
            kind = E_GRAPH

        detail = "%s (%s)" % (redact(message), code) if code else redact(message)

        return GraphError(kind, detail, status, request_id, retry_after)


class _NoRedirects(urllib.request.HTTPRedirectHandler):
    """Refuses to follow a redirect at all.

    Graph does redirect — a file's @microsoft.graph.downloadUrl is a pre-authenticated link to a
    content host that is deliberately not on this plugin's allowlist. Following it automatically
    would send the Authorization header to that host, which is both a leak and unnecessary: the
    link already carries its own authorisation. Downloads are therefore an explicit, separate,
    credential-free request, and everything else treats a redirect as an answer it did not expect.
    """

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _retry_after(headers):
    """What Microsoft asked Aurora to wait, in either shape it says it."""
    value = headers.get("Retry-After") or headers.get("retry-after")

    if not value:
        return None

    try:
        return max(0, min(int(value), 3600))
    except (TypeError, ValueError):
        return None


def _decode(text, status):
    """A Graph response body, or a refusal that says the shape was wrong.

    A 204 carries nothing and that is correct rather than malformed. Anything else that claims to
    be JSON and is not is a provider that changed under us, which is worth reporting as its own
    kind of failure rather than as an exception from a parser.
    """
    if status == 204 or not text.strip():
        return {}

    try:
        decoded = json.loads(text)
    except ValueError:
        raise GraphError(E_MALFORMED, "Microsoft answered with something that is not JSON", status)

    if not isinstance(decoded, dict):
        raise GraphError(
            E_MALFORMED, "Microsoft answered with a %s where an object was expected"
            % type(decoded).__name__, status)

    return decoded


def _graph_error_text(text):
    """Digs the code and message out of Graph's error envelope, defensively.

    The envelope is documented and is still parsed as though it might be anything, because this
    code path runs precisely when the far end is behaving unexpectedly.
    """
    try:
        document = json.loads(text)
    except ValueError:
        return "", (text[:200] if text else "Microsoft refused without saying why")

    if not isinstance(document, dict):
        return "", "Microsoft refused without saying why"

    error = document.get("error")

    if isinstance(error, str):
        return error, document.get("error_description") or error

    if not isinstance(error, dict):
        return "", "Microsoft refused without saying why"

    code = error.get("code") if isinstance(error.get("code"), str) else ""
    message = error.get("message") if isinstance(error.get("message"), str) else ""

    return code, (message or "Microsoft refused without saying why")


def audit_metadata(client, extra=None):
    """What a capability returns about how the call went, carrying nothing secret.

    Aurora records the result of every capability call. This is the part that says something useful
    about the provider — whether it throttled, which API version was used — without putting a token,
    a header or a mailbox in the audit.
    """
    metadata = {
        "provider": "microsoft.graph",
        "api_version": "v1.0",
        "throttled_seconds": round(client.throttled_seconds, 3),
    }

    if extra:
        metadata.update(extra)

    return metadata


def tls_context():
    """Certificate verification on, hostname checking on, and said out loud.

    Named rather than left to the default so that turning it off is a visible edit to a line that
    explains why it must not be, rather than an omission nobody notices.
    """
    context = ssl.create_default_context()
    context.check_hostname = True
    context.verify_mode = ssl.CERT_REQUIRED
    return context
