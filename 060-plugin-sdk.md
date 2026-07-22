# Aurora OS — RFC 060: Plugins and Extensions SDK

**State:** Normative · **Depends on:** RFC 06, 09, 040

## Objective

Allow third parties to add capabilities to Aurora without being granted implicit authority over Mind, secrets, or existing tools. A plugin is a declarative, isolated extension, not code trusted by default.

## Architecture

```text
Plugin author → signed manifest → compatibility validation
                                             │
                                             ▼
catalog of required capabilities and policies
                                             │
                                             ▼
isolated installation → authorization by capacity
                                             │
                                             ▼
                               Tool Orchestrator → plugin sandbox
```

## Data structures

```text
PluginManifest
  plugin_id, version, publisher, signature, min_platform_version
  capabilities[], tool_manifests[], event_subscriptions[]
  required_permissions[], data_classes[], network_endpoints[]
  migration_manifest?, documentation_ref, integrity_hash

PluginInstallation
  id, plugin_id, version, status: INSTALLED|DISABLED|QUARANTINED|REMOVED
  granted_permissions[], config_ref, installed_at, updated_at

PluginCapability
  key, input_schema, output_schema, effects[], approval_requirements
  rate_limit, timeout, idempotency_support, audit_level
```

## Interfaces

```text
Plugin.verify(manifest, signature) -> VerificationReport
Plugin.install(manifest, approval) -> PluginInstallation
Plugin.describe(capability) -> ToolManifest
Plugin.invoke(authorised_tool_call) -> ToolResult
Plugin.disable(installation_id, actor) -> PluginInstallation
```

## Mandatory rules

1. Each plugin MUST declare capabilities, effects, processed data, network domains and permissions; Undeclared dynamic requests are denied.
2. Plugins MUST run in isolation, without access to the main process, database, vault or general network.
3. Installation and any permit increases require explicit review/consent.
4. A plugin never receives data above the declared rating nor can it subscribe to events without authorization filtering.
5. Updates with incompatible changes, new permissions or new origin require re-verification and may enter `QUARANTINED`.

## Limit and error cases

- **Invalid signature:** reject before installing; It doesn't even run in preview mode.
- **Plugin fails repeatedly:** protection circuit disables capability and preserves pending calls for reconciliation.
- **Update removes capacity used in active plan:** block dependent tasks and request replanning.
- **Plugin tries to send secret in output:** writer blocks output, opens security event and puts installation in quarantine.

## Justification

A mature SDK converts extensibility into a security contract, rather than turning every integration into a privileged exception. So a Spotify plugin can be useful without gaining powers over email, SSH or Mind.

## Future expansions

Marketplace with reputation, certification, delegated permissions, compliance testing and declarative plugins without arbitrary code.