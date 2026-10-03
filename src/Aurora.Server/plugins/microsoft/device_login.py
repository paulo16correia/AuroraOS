#!/usr/bin/env python3
"""Signs in to Microsoft once, so the refresh token can go into Aurora's vault.

**Run this yourself. It is not a capability, and that is deliberate.**

The flow ends by producing a refresh token — a long-lived credential that acts as you. A capability
that returned one would hand it back through Aurora's result path and into its audit, which is
precisely where a credential must not be. So the flow runs here, prints the token to your terminal,
and you put it in the vault by hand:

    python3 plugins/microsoft/device_login.py <tenant-id> <client-id>
    aurora secret set plugin/microsoft tenant_id
    aurora secret set plugin/microsoft client_id
    aurora secret set plugin/microsoft refresh_token

Aurora never sees your password, and never asks for it. Microsoft does the signing-in, in your own
browser, and hands back a token scoped to what you consented to.

Nothing here writes to disk. If you close the terminal before storing the token, run it again.
"""

import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

LOGIN = "https://login.microsoftonline.com"

# What Aurora will ask for. Kept narrow on purpose and widened only when a capability family that
# needs more is actually implemented — consent is easier to grant than to take back, and a scope
# nobody uses is authority nobody is watching.
SCOPES = [
    "offline_access",
    "openid",
    "profile",
    "User.Read",
]


def post(url, form):
    body = urllib.parse.urlencode(form).encode("utf-8")
    request = urllib.request.Request(url, data=body, method="POST")
    request.add_header("Content-Type", "application/x-www-form-urlencoded")

    try:
        with urllib.request.urlopen(request, timeout=30) as answer:
            return json.loads(answer.read().decode("utf-8"))
    except urllib.error.HTTPError as failed:
        return json.loads(failed.read().decode("utf-8") or "{}")


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    tenant, client = sys.argv[1], sys.argv[2]

    started = post(
        "%s/%s/oauth2/v2.0/devicecode" % (LOGIN, tenant),
        {"client_id": client, "scope": " ".join(SCOPES)},
    )

    if "user_code" not in started:
        print("Microsoft would not start the sign-in: %s"
              % started.get("error_description", started.get("error", "no reason given")))
        return 1

    print()
    print(started.get("message") or
          "Open %s and enter the code %s"
          % (started.get("verification_uri"), started.get("user_code")))
    print()
    print("Waiting for you to finish in the browser...")

    interval = int(started.get("interval") or 5)
    deadline = time.monotonic() + int(started.get("expires_in") or 900)

    while time.monotonic() < deadline:
        time.sleep(interval)

        answer = post(
            "%s/%s/oauth2/v2.0/token" % (LOGIN, tenant),
            {
                "client_id": client,
                "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                "device_code": started["device_code"],
            },
        )

        error = answer.get("error")

        if error == "authorization_pending":
            continue

        if error == "slow_down":
            interval += 5
            continue

        if error:
            print("Sign-in failed: %s" % answer.get("error_description", error))
            return 1

        refresh = answer.get("refresh_token")

        if not refresh:
            print("Microsoft signed you in but returned no refresh token. The app registration "
                  "probably does not request 'offline_access'.")
            return 1

        print()
        print("Signed in. Store these three, then delete this terminal's scrollback:")
        print()
        print("  aurora secret set plugin/microsoft tenant_id       %s" % tenant)
        print("  aurora secret set plugin/microsoft client_id       %s" % client)
        print("  aurora secret set plugin/microsoft refresh_token")
        print()
        print("The refresh token, to paste when the last command asks for it:")
        print()
        print(refresh)
        print()
        return 0

    print("The sign-in expired before it was completed.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
