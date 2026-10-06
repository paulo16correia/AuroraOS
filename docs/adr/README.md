# Architecture decision records

Each record says what was decided, why, and what was given up. They are written in the past tense
and are not edited afterwards except for a line at the top saying what later superseded them — a
record is a decision as it was made, not a description of the current system. For the current
system, read the [runbook](../guides/operator-runbook.md) and
[platform support](../reference/platform-support.md).

0039 was removed when 0045 superseded it.

| # | Decision | Status |
| --- | --- | --- |
| 0001 | [Aurora MCP Pipeline, Slice 1](0001-mcp-pipeline-slice1.md) | Superseded in part by 0012 |
| 0002 | [Persistent Approval (It.2, first increment)](0002-it2a-persistent-approval.md) | Implemented |
| 0003 | [`files.write_sandbox` (It.2, second increment)](0003-it2b-sandbox-write.md) | Implemented |
| 0004 | [Reasoner (It.1)](0004-it1-reasoner.md) | Superseded in part |
| 0005 | [Audit hardening (It.3, first increment)](0005-it3a-audit-hardening.md) | Implemented |
| 0006 | [Audit pre-image enrichment (It.3, second increment)](0006-it3b-audit-preimage.md) | Implemented |
| 0007 | [Reconciling indeterminate executions (It.3, third increment)](0007-it3c-reconciliation.md) | Implemented |
| 0008 | [Operational metrics (It.3, fourth increment)](0008-it3d-metrics.md) | Implemented; partly superseded by 0088 |
| 0009 | [Backup and restore (It.3, fifth increment)](0009-it3e-backup-restore.md) | Implemented (backup) |
| 0010 | [Consent sessions, read-only (It.2, final increment)](0010-it2c-consent-sessions.md) | Implemented |
| 0011 | [Operator passphrase on approval (It.2, follow-up)](0011-it2d-operator-passphrase.md) | Implemented; partly superseded by 0088 |
| 0012 | [Specification baseline and conformance](0012-specification-baseline.md) | Accepted |
| 0013 | [Event Bus, outbox and dead-letter queue (step 3)](0013-step3-event-bus.md) | Implemented |
| 0014 | [Vault abstraction (step 4)](0014-step4-vault.md) | Implemented |
| 0015 | [Portable principal, and versioned migrations](0015-portable-principal-and-migrations.md) | Implemented |
| 0016 | [Instance lifecycle (step 5a, closing the step 1 gate)](0016-instance-lifecycle.md) | Implemented |
| 0017 | [Genome (step 5b)](0017-genome.md) | Implemented |
| 0018 | [Mind State: capture, verify, restore (step 5c)](0018-mind-state.md) | Implemented |
| 0019 | [Persistent memory with provenance (step 6a)](0019-memory.md) | Implemented |
| 0020 | [Knowledge Graph (step 6b)](0020-knowledge-graph.md) | Implemented |
| 0021 | [World Model (step 6c)](0021-world-model.md) | Implemented |
| 0022 | [Attention and Working Memory (step 7a)](0022-attention-working-memory.md) | Implemented |
| 0023 | [Decision Engine and the cognitive cycle (steps 7b and 7c)](0023-decision-and-cycle.md) | Implemented |
| 0024 | [Objectives, plans and tasks (step 7d)](0024-planner.md) | Implemented |
| 0025 | [Capability Resolver (step 8a)](0025-capability-resolver.md) | Implemented |
| 0026 | [Tool Manager (step 8b)](0026-tool-manager.md) | Implemented |
| 0027 | [Laws 001–007 as compliance tests](0027-law-compliance.md) | Implemented, with two named gaps |
| 0028 | [Action, Observation, Reflection and Learning (step 9a)](0028-action-observation-learning.md) | Implemented |
| 0029 | [The low-risk pilot: first vertical slice (step 9b)](0029-pilot-first-slice.md) | Implemented |
| 0030 | [The operator API (RFC 10)](0030-operator-api.md) | Implemented |
| 0031 | [MCP runs through the cognitive cycle](0031-mcp-through-the-cycle.md) | Implemented, with one named consequence |
| 0032 | [The Scheduler: rhythm without authority](0032-scheduler.md) | Implemented |
| 0033 | [Signals and Needs: priority without permission](0033-signals-and-needs.md) | Implemented |
| 0034 | [Status and maintenance: capacity, timing, upkeep](0034-status-and-maintenance.md) | Implemented, with named gaps |
| 0035 | [Missions, governed curiosity, and the second application](0035-missions-curiosity-review.md) | Implemented |
| 0036 | [Closing the open items](0036-closing-the-open-items.md) | Implemented, with two items honestly not done |
| 0037 | [Unfreezing the sandbox file capabilities](0037-unfreezing-the-sandbox-capabilities.md) | Implemented |
| 0038 | [The control panel](0038-control-panel.md) | Implemented, partial |
| 0040 | [Internal deliberation and explainable synthesis](0040-internal-deliberation.md) | Implemented |
| 0041 | [The belief system](0041-belief-system.md) | Implemented |
| 0042 | [Relationships and preferences](0042-relationships-and-preferences.md) | Implemented |
| 0043 | [Self: what Aurora knows about itself](0043-self-model.md) | Implemented |
| 0044 | [Communication identity](0044-personality.md) | Implemented, with one gap named |
| 0045 | [Aurora is local only](0045-local-only.md) | Decided by the owner |
| 0046 | [The development model](0046-development-model.md) | Implemented, with one deliberate narrowing |
| 0047 | [Life history](0047-life-history.md) | Implemented |
| 0048 | [The plugin SDK](0048-plugin-sdk.md) | Implemented, with the isolation boundary stated exactly |
| 0049 | [Closing the seams](0049-closing-the-seams.md) | Implemented, one seam left open on purpose |
| 0050 | [Completing the platform](0050-completing-the-platform.md) | Implemented, with two items that cannot be closed here |
| 0051 | [Aurora proposes with its own catalogue, or not at all](0051-no-model-proposer.md) | Decided by the owner |
| 0052 | [Confining plugins with the operating system](0052-plugin-confinement.md) | Implemented |
| 0053 | [Closing LAW-008, and tidying after a withdrawn RFC](0053-law-008-and-the-withdrawn-rfc.md) | Implemented |
| 0054 | [Aurora can be asked what it is](0054-aurora-can-be-asked-what-it-is.md) | Implemented |
| 0055 | [The evaluator RFC 08 asked for](0055-the-evaluator.md) | Implemented |
| 0056 | [A high-risk incident revokes, records and notifies](0056-incidents.md) | Implemented |
| 0057 | [The Constitution, applied rather than quoted](0057-the-constitution-applied.md) | Implemented |
| 0058 | [The Mind and the unit of work](0058-mind-and-workitem.md) | Implemented |
| 0059 | [Three divergences, written down](0059-divergences-and-dormant-seams.md) | Decided |
| 0060 | [`files.organise_sandbox`, the reference capability](0060-reference-capability.md) | Implemented |
| 0061 | [The disk is judged on room left, not on proportion used](0061-the-disk-is-judged-on-room-left.md) | Implemented |
| 0062 | [Plugins people can actually write](0062-plugins-people-can-actually-write.md) | Implemented |
| 0063 | [Aurora does its own upkeep](0063-the-heartbeat.md) | Implemented |
| 0064 | [The security events nobody raised](0064-the-events-nobody-raised.md) | Implemented |
| 0065 | [Dead contracts, and a test that keeps them dead](0065-dead-contracts.md) | Implemented |
| 0066 | [Capabilities people can write safely](0066-capabilities-people-can-write-safely.md) | Implemented |
| 0067 | [Plugins that hold a connection](0067-plugins-that-hold-a-connection.md) | Implemented |
| 0068 | [Voice, and what about it is unverified](0068-voice-and-what-is-unverified.md) | Implemented, unverified against Discord |
| 0069 | [Granting a plugin the graphics processor](0069-granting-a-plugin-the-graphics-processor.md) | Implemented |
| 0070 | [A window that names what it covers](0070-a-window-that-names-what-it-covers.md) | Implemented |
| 0071 | [Microsoft 365 does not make Aurora networked](0071-microsoft-365-does-not-make-aurora-networked.md) | Decided |
| 0072 | [Confinement that proves itself](0072-confinement-that-proves-itself.md) | Implemented, unverified |
| 0073 | [One voice, across channels](0073-one-voice-across-channels.md) | Implemented, unverified |
| 0074 | [A voice that needs nobody](0074-a-voice-that-needs-nobody.md) | Implemented, unverified on real models |
| 0075 | [What runs a script plugin](0075-what-runs-a-script-plugin.md) | Implemented |
| 0076 | [A backup Aurora has let go of](0076-a-backup-aurora-has-let-go-of.md) | Implemented |
| 0077 | [One name for a time zone](0077-one-name-for-a-time-zone.md) | Implemented |
| 0078 | [The AppContainer, actually run](0078-appcontainer-that-actually-runs.md) | Implemented, verified on Windows |
| 0079 | [Operational hardening for a controlled demo](0079-operational-hardening.md) | Implemented |
| 0080 | [A refusal worth repeating, and one that is not](0080-a-refusal-worth-repeating.md) | Implemented |
| 0081 | [A refusal is not a fault](0081-a-refusal-is-not-a-fault.md) | Implemented |
| 0082 | [A media path that can say it died](0082-a-media-path-that-can-say-it-died.md) | Implemented (detection only) |
| 0083 | [Tests do not write to the installation](0083-tests-do-not-write-to-the-installation.md) | Implemented |
| 0084 | [A model that only makes sentences](0084-a-model-that-only-makes-sentences.md) | Implemented, provider-agnostic; partly superseded by 0087 |
| 0085 | [An encoder window the size of what was said](0085-an-encoder-window-the-size-of-what-was-said.md) | Implemented |
| 0086 | [Experiments do not live in the runtime](0086-experiments-do-not-live-in-the-runtime.md) | Implemented as a rule |
| 0087 | [The model is reached through the plugin](0087-the-model-is-reached-through-the-plugin.md) | Superseded by 0089 |
| 0088 | [The agent does not decide for itself](0088-the-agent-does-not-decide-for-itself.md) | Implemented |
| 0089 | [The model is asked from Aurora](0089-the-model-is-asked-from-aurora.md) | Implemented |
| 0090 | [A window belongs to the person](0090-a-window-belongs-to-the-person.md) | Implemented |
