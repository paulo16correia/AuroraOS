#!/usr/bin/env python3
"""An Aurora plugin.

Aurora runs this program once per call. The call arrives on standard input as JSON and the result
goes to standard output as JSON. Anything else the program writes is ignored, and a non-zero exit
means the call failed.

Almost nothing is inherited: none of Aurora's environment, and on macOS and Linux no network and
no access to the owner's files. Three variables are passed deliberately — AURORA_PLUGIN_ID,
AURORA_CAPABILITY, and a PATH holding only the system directories, so that this line can find an
interpreter at all.
"""
import json
import os
import sys

call = json.loads(sys.stdin.read() or "{}")
capability = os.environ.get("AURORA_CAPABILITY", "")

if capability.endswith(".greet"):
    print(json.dumps({"greeting": f"Hello, {call['name']}."}))
else:
    # Exit non-zero for anything you cannot do. Aurora records the exit code and deliberately does
    # not read your standard error, so put nothing there that somebody needs to see.
    sys.exit(1)