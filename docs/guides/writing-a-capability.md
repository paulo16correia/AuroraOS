# Writing an Aurora capability

A capability is a class with a description of itself and a method that does the work. Two files
change: the class you add, and one line registering it.

That is the whole contract. Everything else in this guide is about the description, because in
Aurora the description is a security control.

## Plugin or capability?

Write a **capability** when the code lives in this repository, runs in Aurora's process, and needs
Aurora's own ports — the database, the sandbox, the note store.

Write a **[plugin](writing-a-plugin.md)** when the code is yours, lives outside this repository, and
can do its work in a subprocess with no network and no access to anything but its own directory.
Plugins are the right answer far more often than people expect.

## Five minutes

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>Counts the words in a string.</summary>
public sealed class CountWordsCapability : ICapability
{
    private static readonly JsonElement SchemaElement =
        CapabilityInput.Object()
            .String("text", maxLength: 10_000, required: true, minLength: 1)
            .Build();

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "text.count_words",
        Title: "Count words",
        Description: "Counts the words in a string.",
        InputSchema: SchemaElement,
        Effects: Array.Empty<string>(),
        Risk: RiskLevel.Low,
        ApprovalRequired: false);

    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var text = input.GetProperty("text").GetString() ?? string.Empty;
        var count = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        return ValueTask.FromResult(
            JsonSerializer.SerializeToElement(new JsonObject { ["words"] = count }));
    }
}
```

Then one line in `src/Aurora.Server/ServiceRegistration.cs`, beside the others:

```csharp
services.AddSingleton<ICapability, CountWordsCapability>();
```

Run the tests. If they pass, it is live — `aurora_execute` can call it, the catalogue lists it, and
the audit log records it.

## The descriptor is a security control

This is the part worth reading twice.

Aurora's policy engine decides **entirely from what your descriptor says**. There is no separate
policy file, no allowlist to add yourself to, no approval scope to declare. Four combinations exist:

| What you declare | What happens |
| --- | --- |
| `Low`, no effects, no approval | Allowed automatically. Nobody is asked. |
| `Medium` + `ApprovalRequired: true` | A person approves it, once, for that exact input. |
| `High` + `ApprovalRequired: true` + `Reversible: true` | Same, and the caller is given what they need to undo it. |
| anything else | Denied on every call. |

So a capability that writes to disk and declares itself `Low` with no effects would run on every
call with nobody asked. **Declare what you actually do.**

`CapabilityDeclarationTests` enforces this rather than trusting it. Ports that change something
carry `[Effect("files.write")]` on the method, and a capability that calls one without declaring
that effect fails the build. If you add a new port that mutates, mark it — the check only sees what
is marked.

Two rules follow from the table that catch people out:

- **`High` without `Reversible: true` is dead on arrival.** Policy denies it every time. The test
  tells you at build time instead of leaving you to find out.
- **`Reversible` defaults to `false`**, which is the honest answer for a capability whose author
  did not think about it. Set it to `true` only if the caller genuinely gets what they need to undo
  the call.

## Effects

An effect is a string naming what changes outside Aurora's own reasoning: `files.write`,
`files.move`, `memory.write`. Use an existing one if it fits. Reading is not an effect.

Over-declaring is safe and nothing complains. Under-declaring is the thing the tests exist to stop.

## Input schemas

Build them; do not write JSON by hand:

```csharp
CapabilityInput.Object()
    .String("path", maxLength: 512, required: true, minLength: 1)
    .String("content", maxLength: 65_536, required: true)
    .Boolean("dry_run")
    .ArrayOf(
        "rules",
        CapabilityInput.Object()
            .String("match", maxLength: 128, required: true, minLength: 1)
            .String("into", maxLength: 128, required: true, minLength: 1),
        maxItems: 20, minItems: 1)
    .Build()
```

`additionalProperties: false` and the 2020-12 draft are applied for you and are not optional — a
capability that accepts unknown fields will one day be handed one that means something to a later
version and nothing to this one.

Every string needs a `maxLength` and every array a `maxItems`. That is deliberate: a field with no
ceiling is a way to put as much as you like into Aurora's database through one call. Use
`minLength: 1` for anything that names something, so the empty string is refused rather than
handled.

Aurora validates input against your schema before `ExecuteAsync` runs, so inside it you can read
required fields directly.

## Errors

Throw. The kernel catches it, records the call as `failed` in the audit log, and returns a refusal.

Do not put the reason in the exception message if the reason contains a path, a filename, or
anything else about the machine — the message is not echoed to the caller precisely because a
caller who could probe one rejected path at a time would learn the sandbox layout from your error
messages. There is a test for that.

## What you do not have to do

- **No policy registration.** The descriptor is the policy input.
- **No approval scope.** The kernel derives it from the action id and the input.
- **No audit call.** The kernel writes the entry.
- **No `capability_definition` row.** There is a second thing in this codebase called a capability
  — `ICapabilityResolver` and the `capability_definition` table, which is RFC 051's resolution
  layer for choosing between several providers of one capability. It is implemented and tested, and
  **nothing on the execution path uses it**. If you are adding a capability to Aurora, it is not
  the thing you want and you can ignore it entirely.

## Before you open the pull request

Run the suite. Three things will fail if you got the description wrong, and each says what to fix:

- `CapabilityDeclarationTests` — you call something that changes state and did not declare it, or
  you declared `High` without reversibility.
- `AuthorizationMatrixTests` — `docs/reference/capability-authorization.md` is generated from the
  registry and no longer matches. Regenerate it rather than editing by hand:

  ```bash
  AURORA_WRITE_REFERENCE=1 dotnet test --filter FullyQualifiedName~AuthorizationMatrixTests
  ```
- `LawComplianceTests` — the capability reaches something a capability is not allowed to reach.

## Where things are

| | |
| --- | --- |
| The interface | `src/Aurora.Core/Abstractions/Capabilities.cs` |
| The descriptor | `src/Aurora.Core/Contracts/Catalog.cs` |
| Schema builder | `src/Aurora.Core/Contracts/CapabilityInput.cs` |
| Existing capabilities | `src/Aurora.Adapters/Capabilities/` |
| Registration | `src/Aurora.Server/ServiceRegistration.cs` |
| Policy rules | `src/Aurora.Adapters/Policy/AllowlistPolicyEngine.cs` |
| What is live now | `docs/reference/capability-authorization.md` |
