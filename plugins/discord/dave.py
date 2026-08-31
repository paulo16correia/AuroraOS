"""Discord's end-to-end voice encryption, when the machine can do it.

Discord requires DAVE — its MLS-based end-to-end encryption — on voice connections in servers that
have it enabled, and refuses the session with close code 4017 when a client says it cannot. That is
not a setting to work around: it means the people in the call have been told their audio is end-to-
end encrypted, and a client that opted out would make that untrue for everybody.

MLS is a group key agreement protocol and is not something a plugin implements. It arrives as
`davey`, a compiled extension, in the same category as libopus: found if the owner installed it,
and refused clearly if not.
"""

import glob
import importlib
import os
import sys

# Where a pip install puts things, for the interpreters a plugin is likely to be run by. Searched
# rather than assumed because the plugin runs with a cleared environment and no PYTHONPATH — that
# is deliberate, and it means anything installed for the owner has to be found rather than
# inherited.
SEARCH = [
    # Beside the plugin, first. The sandbox lets a plugin read its own directory and nothing else
    # of the owner's, so a dependency installed into a home directory is one the plugin cannot
    # open — correctly, and by design. What a plugin needs ships with it.
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "vendor"),

    "/usr/local/lib/python*/site-packages",
    "/usr/local/lib/python*/dist-packages",
    "/opt/homebrew/lib/python*/site-packages",
    os.path.expanduser("~/Library/Python/*/lib/python/site-packages"),
    os.path.expanduser("~/.local/lib/python*/site-packages"),
]

_davey = None
_looked = False


def _find():
    global _davey, _looked

    if _looked:
        return _davey

    _looked = True

    try:
        _davey = importlib.import_module("davey")
        return _davey
    except ImportError:
        pass

    for pattern in SEARCH:
        for directory in ([pattern] if os.path.isdir(pattern) else glob.glob(pattern)):
            if not os.path.isdir(os.path.join(directory, "davey")):
                continue

            if directory not in sys.path:
                sys.path.insert(0, directory)

            try:
                _davey = importlib.import_module("davey")
                return _davey
            except ImportError:
                continue

    return None


def available():
    """Whether this machine can take part in an end-to-end encrypted call."""
    return _find() is not None


def protocol_version():
    """The DAVE version to offer, or zero for "I cannot do this".

    Zero is an honest answer and Discord may refuse it. Sending a version that is not backed by a
    working library would be a worse one: the call would be established and the audio would not be
    what everybody in it was told it is.
    """
    library = _find()
    return getattr(library, "DAVE_PROTOCOL_VERSION", 0) if library else 0


def session(version, user_id, channel_id):
    """A new MLS session for one call."""
    library = _find()

    if library is None:
        raise RuntimeError("the davey library is needed for end-to-end encrypted voice")

    return library.DaveSession(version, int(user_id), int(channel_id))


def proposals_operation(append):
    """Discord sends a byte saying whether a proposal set adds members or removes them."""
    library = _find()
    kinds = library.ProposalsOperationType

    return kinds.append if append else kinds.revoke


def is_commit_welcome(result):
    library = _find()
    return isinstance(result, library.CommitWelcome)
