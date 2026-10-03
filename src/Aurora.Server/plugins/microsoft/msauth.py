"""Getting a Microsoft access token, and holding it no longer than necessary.

Two grants, both starting from something already in Aurora's vault:

    refresh token       acts as the owner. What almost everything here needs, because "read *my*
                        mail" is a sentence about a person and delegated permissions are how
                        Microsoft says that.
    client credentials  acts as the application, tenant-wide. Needed for a few things Microsoft
                        offers no delegated form of, and dangerous in proportion: an app-only token
                        can usually see every mailbox in the tenant rather than one.

**No credential is ever written to disk by this plugin.** The access token lives in memory for the
minutes it is valid; the refresh token and the client secret arrive in the opening frame from
Aurora's vault and are not persisted anywhere. A plugin that cached them in its working directory
would have moved the owner's tenant credentials out of the vault and into a file, which is a
downgrade however convenient it is.

Device code login is deliberately **not** a capability. It ends by producing a refresh token, and a
capability that returns one would be handing a long-lived tenant credential back through Aurora's
result path and into its audit. It is a setup step the owner runs themselves — see
`device_login.py` — after which the token they were shown goes into the vault by hand.
"""

import json
import time
import urllib.error
import urllib.parse
import urllib.request

from graph import (
    E_AUTH,
    LOGIN_HOST,
    GraphError,
    check_host,
    redact,
)

# Refreshed this long before expiry rather than after it. A token that expires between the check
# and the call produces a 401 that looks like a permissions problem and is not one.
EARLY_REFRESH_SECONDS = 120

# What a token is assumed to last when Microsoft does not say. Short, because being wrong in this
# direction costs one extra refresh and being wrong in the other costs a failed call.
DEFAULT_LIFETIME_SECONDS = 300


class TokenSource:
    """Hands out an access token, fetching or refreshing it as needed.

    One instance per plugin process. `access_token()` is called on every request rather than the
    token being held by the caller, so expiry is handled in one place and a caller cannot keep a
    stale one alive by holding a reference.
    """

    def __init__(self, tenant_id, client_id, refresh_token=None, client_secret=None,
                 scopes=None, opener=None, now=None, login_base=None,
                 allowed_hosts=None, plain_loopback=False):
        self._tenant = tenant_id
        self._client = client_id
        self._refresh = refresh_token
        self._secret = client_secret
        self._scopes = scopes or ["https://graph.microsoft.com/.default", "offline_access"]
        self._opener = opener or urllib.request.build_opener()
        self._now = now or time.monotonic

        # Where the sign-in service is. Fixed when this is built, never chosen per request, and
        # checked against the allowlist like anything else.
        self._login = login_base or ("https://" + LOGIN_HOST)
        self._allowed = tuple(allowed_hosts) if allowed_hosts else None
        self._plain_loopback = plain_loopback

        self._token = None
        self._expires_at = 0.0

        # Counted rather than logged. How often a token had to be renewed is a fact worth having in
        # the audit metadata; the tokens themselves are not.
        self.refreshes = 0

    def _held(self):
        """The secret values this process is carrying, for exact-match redaction.

        Includes the access token: Microsoft quotes the credential it rejected back at the caller,
        and the one it rejects is usually the one we just sent.
        """
        return tuple(v for v in (self._refresh, self._secret, self._token) if v)

    @property
    def configured(self):
        """Whether this could obtain a token at all, without trying."""
        return bool(self._tenant and self._client and (self._refresh or self._secret))

    @property
    def grant(self):
        return "refresh_token" if self._refresh else (
            "client_credentials" if self._secret else "none")

    def access_token(self):
        if self._token and self._now() < self._expires_at:
            return self._token

        self._acquire()
        return self._token

    def forget(self):
        """Drops the cached token.

        Called when Microsoft says 401 despite a token that looked live — a tenant can revoke one
        early, and continuing to present it turns a recoverable state into a wall.
        """
        self._token = None
        self._expires_at = 0.0

    # ---- the exchange ----

    def _acquire(self):
        if not self.configured:
            raise GraphError(
                E_AUTH,
                "this plugin has no Microsoft credentials; set them with "
                "'aurora secret set plugin/microsoft <name>'",
            )

        url = "%s/%s/oauth2/v2.0/token" % (self._login, self._tenant)

        if self._allowed is None:
            check_host(url)
        else:
            check_host(url, self._allowed, self._plain_loopback)

        if self._refresh:
            form = {
                "client_id": self._client,
                "grant_type": "refresh_token",
                "refresh_token": self._refresh,
                "scope": " ".join(self._scopes),
            }

            if self._secret:
                # A confidential client. Public clients registered without one omit it entirely,
                # and sending an empty string is rejected rather than ignored.
                form["client_secret"] = self._secret
        else:
            form = {
                "client_id": self._client,
                "grant_type": "client_credentials",
                "client_secret": self._secret,
                "scope": "https://graph.microsoft.com/.default",
            }

        answer = self._post_form(url, form)

        token = answer.get("access_token")

        if not isinstance(token, str) or not token:
            raise GraphError(E_AUTH, "Microsoft returned no access token")

        try:
            lifetime = int(answer.get("expires_in") or DEFAULT_LIFETIME_SECONDS)
        except (TypeError, ValueError):
            lifetime = DEFAULT_LIFETIME_SECONDS

        self._token = token
        self._expires_at = self._now() + max(0, lifetime - EARLY_REFRESH_SECONDS)
        self.refreshes += 1

        # Microsoft rotates the refresh token on many tenants. Keeping the new one in memory means
        # the process keeps working; it is still never written down, so a restart goes back to the
        # one in the vault — which is the behaviour to want, since the vault is what the owner can
        # revoke.
        rotated = answer.get("refresh_token")

        if isinstance(rotated, str) and rotated:
            self._refresh = rotated

    def _post_form(self, url, form):
        body = urllib.parse.urlencode(form).encode("utf-8")

        request = urllib.request.Request(url, data=body, method="POST")
        request.add_header("Content-Type", "application/x-www-form-urlencoded")
        request.add_header("Accept", "application/json")
        request.add_header("User-Agent", "Aurora/1.0 (+local governed agent)")

        try:
            with self._opener.open(request, timeout=20) as answer:
                raw = answer.read(256 * 1024)
                return _decode_token_response(raw)

        except urllib.error.HTTPError as failed:
            raw = failed.read(256 * 1024) or b""
            document = _safe_json(raw)

            # The identity platform's errors are the most useful and the most dangerous to pass
            # through: AADSTS codes tell an owner exactly what to fix, and the description can
            # quote the request. Redacted, then handed on.
            code = document.get("error") or "invalid_grant"
            description = document.get("error_description") or "Microsoft refused the credentials"

            raise GraphError(
                E_AUTH,
                redact("%s: %s" % (code, description.splitlines()[0][:300]), self._held()),
                failed.code,
            )

        except urllib.error.URLError as unreachable:
            raise GraphError(
                E_AUTH,
                redact(
                    "Microsoft's sign-in service could not be reached: %s" % unreachable.reason,
                    self._held()),
            )


def _safe_json(raw):
    try:
        document = json.loads(raw.decode("utf-8", "replace"))
        return document if isinstance(document, dict) else {}
    except ValueError:
        return {}


def _decode_token_response(raw):
    document = _safe_json(raw)

    if not document:
        raise GraphError(E_AUTH, "Microsoft's sign-in service answered with something unreadable")

    return document


def from_secrets(secrets, settings=None, login_base=None, allowed_hosts=None,
                 plain_loopback=False):
    """Builds a token source from what Aurora delivered in the opening frame.

    Names match the manifest's required_secrets, so an owner who ran the commands the install
    printed has already done everything this needs.
    """
    settings = settings or {}

    return TokenSource(
        tenant_id=(secrets.get("tenant_id") or settings.get("tenant_id") or "").strip(),
        client_id=(secrets.get("client_id") or settings.get("client_id") or "").strip(),
        refresh_token=(secrets.get("refresh_token") or "").strip() or None,
        client_secret=(secrets.get("client_secret") or "").strip() or None,
        login_base=login_base,
        allowed_hosts=allowed_hosts,
        plain_loopback=plain_loopback,
    )
