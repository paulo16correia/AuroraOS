# Writing an Aurora plugin

A plugin is a folder with two things in it: a `plugin.json` saying what you offer, and a program
Aurora runs. Any language. Aurora writes the call to your program's standard input as JSON and
reads the result from its standard output.

That is the whole contract.

## Five minutes

```bash
aurora plugin new ~/my-plugin
aurora plugin validate ~/my-plugin
aurora plugin install ~/my-plugin
```

`plugin new` writes a working plugin. Change it into yours, run `validate` until it stops
complaining, then `install`. Your capability joins Aurora's catalogue the next time it starts.

## The program

```python
#!/usr/bin/env python3
import json, os, sys

call = json.loads(sys.stdin.read() or "{}")

if os.environ["AURORA_CAPABILITY"] == "notes.append":
    print(json.dumps({"appended": True}))
else:
    sys.exit(1)
```

- **Standard input** is the call's arguments, as JSON, already validated against your
  `input_schema`. If it arrived, it matched.
- **Standard output** must be one JSON object. Anything else fails the call — Aurora will not treat
  a line of text as a result.
- **A non-zero exit** means the call failed. Aurora records the code.
- **Standard error is not read.** Deliberately: it is written by your program and could carry
  anything, so nothing there reaches the caller. Put nothing in it that somebody needs to see.
- **`AURORA_CAPABILITY`** tells you which of your capabilities was called; **`AURORA_PLUGIN_ID`** is
  your own id.

Your program runs once per call. It is not a server, it does not need to loop, and it should not
expect to keep anything between calls except what it writes to its own working directory.

## What your program can and cannot do

On macOS and on Linux with bubblewrap installed, Aurora confines it:

| | |
| --- | --- |
| The network | **Closed.** No sockets, no DNS. |
| The owner's files | **Unreadable.** Their home directory does not exist as far as you are concerned. |
| Your own folder | Readable. |
| Your working directory | Readable and writable. This is where anything you keep goes. |
| Everything else | Readable if the system can read it; not writable. |

On a platform Aurora cannot confine — Windows today — it **refuses to run plugins at all** unless
the owner has explicitly accepted that. This is not something you can opt out of from the manifest.

`aurora health` reports which of those applies:

```
plugin-sandbox PASS — plugins confined by sandbox-exec
```

## The manifest

```json
{
  "plugin_id": "acme/notes",
  "version": "1.0.0",
  "publisher": "acme",
  "executable": "run.py",
  "max_data_class": "PRIVATE",
  "required_permissions": ["notes.write"],
  "capabilities": [
    {
      "key": "notes.append",
      "title": "Append a note",
      "description": "Adds a line to the notebook.",
      "input_schema": {
        "type": "object",
        "additionalProperties": false,
        "required": ["line"],
        "properties": { "line": { "type": "string", "maxLength": 500 } }
      },
      "effects": ["notes.write"],
      "risk": "MEDIUM",
      "approval_required": true,
      "reversible": false,
      "idempotent": true,
      "timeout_seconds": 10
    }
  ]
}
```

Every field is a limit rather than a licence. Declaring an effect does not grant it; it makes
anything else a refusal.

**`key`** is the action id in Aurora's catalogue, so it is dotted like the rest. You cannot claim
one Aurora already has, and `aurora.`, `kernel.` and `mind.` are reserved.

**`risk`** decides how Aurora's policy treats you, by exactly the same rules as Aurora's own
capabilities:

| Risk | What it takes to be allowed |
| --- | --- |
| `LOW` | Nothing, **if** you declare no effects. |
| `MEDIUM` | `approval_required: true`. |
| `HIGH` | `approval_required: true` **and** `reversible: true`. |
| `CRITICAL` | Denied. Nothing runs at CRITICAL. |

`HIGH` needs reversibility because one yes is not enough when something goes wrong and somebody has
to put it back. If you cannot say how a call is undone, it is not HIGH — it is MEDIUM, or it needs
rethinking.

Understating your risk gains you nothing. What you may actually do is bounded by your declared
effects and the permissions the owner granted, not by the number you wrote.

**`input_schema`** is JSON Schema, and Aurora validates every call against it before your program
starts. Be strict: `additionalProperties: false` and real bounds mean your program can trust what it
reads.

**`max_data_class`** is the highest classification you may ever be handed. Aurora refuses to pass
you anything above it — so asking for less is a promise you benefit from keeping.

## What happens when you get it wrong

`validate` reports everything at once, naming the field:

```
[Aurora] plugin.json cannot be used yet:
  - 'timout_seconds' is not a field Aurora knows — did you mean timeout_seconds?
  - capability 'greet': key must be dotted, like "notes.append", so it reads as an action
  - capability 'greet': anything above LOW must set approval_required, or policy will refuse
    every call to it
```

Fixing six mistakes should take one round trip.

## Listening to events

Declare what you want and Aurora hands it to you as it happens:

```json
"event_subscriptions": ["MemoryRevised", "GoalDrafted"]
```

Your program is called on the capability key `on_event`, with the event on standard input:

```json
{
  "type": "MemoryRevised",
  "event_id": "…", "correlation_id": "…", "occurred_at_utc": "…",
  "aggregate_ref": "memory/1",
  "payload": "{…}"
}
```

You are given only the types the owner granted you, and `payload` is `null` for anything classified
`CONFIDENTIAL` or above — you learn that something happened, not what it was.

**A subscription is not an invitation to act.** What arrives is a fact. If you want to *do*
something about it, ask Aurora for a capability, and that goes through policy and approval like
anything else.

Delivery goes through the same path a call does, so the same sandbox, permissions and circuit
breaker apply. A delivery that fails counts against you exactly as a failed call does.

## What Aurora does about a plugin that misbehaves

Not as a threat — as the thing you can rely on, and the reason an owner can afford to install
something a stranger wrote.

- **Three consecutive failures** open a circuit and the plugin is quarantined until somebody looks.
- **Output shaped like a credential** is dropped rather than returned, and the plugin is quarantined.
  Whether it was malice or an accident, the next call is not made.
- **A new publisher, or new permissions in an update,** requires review before it runs again.
- **A changed manifest** stops verifying. Aurora sealed what the owner approved; anything else is a
  different plugin.

## Signing

There is none, and that is a decision rather than an omission. Aurora runs on one machine with no
network, so nothing about a publisher's identity is verifiable here — a signature from one would be
a ceremony proving nothing.

**The owner is the trust anchor.** They read what you declared, said yes, and Aurora sealed exactly
that. What re-verification proves on every call is that nothing has changed since.

## Distributing one

A folder. Tell people to run `aurora plugin validate` on it before `install`, so they read what they
are agreeing to.

Publish your `plugin.json` where people can read it without downloading anything. It is the whole of
what you are asking for, and it is short.
