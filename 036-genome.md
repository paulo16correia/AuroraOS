# Aurora Platform — RFC 036: Genome

**State:** Normative · **Depends on:** RFC 035, 040

## Objective

Define the **Genome**: the versioned manifest of everything an Aurora instance receives at birth. The Genome brings together innate and inheritable configuration without confusing installation data, mature personality or acquired memory.

## Architecture

```text
Base genome + authorized variants + installation environment
                         │
                         ▼
                 GenomeResolution
                         │
                         ▼
 bootstrap do Kernel → Mind inicial → Applications/capacidades permitidas
                         │
                         ▼
                 Life History: “nascimento”
```

## Data structures

```text
Genome
  id, family, version, parent_genome_ref?, status: DRAFT|RELEASED|RETIRED
  constitution_version, law_set_version, base_identity_template_ref
  personality_baseline_ref, development_profile_ref
  mind_schema_version, allowed_capability_ids[], policy_bundle_refs[]
  default_locales[], bootstrap_configuration_ref, integrity_hash, signature

GenomeResolution
  id, genome_id, installation_id, selected_variants[], effective_hash
  denied_overrides[], resolved_at, resolver
```

Example families: `Aurora Personal`, `Aurora Casa`, `Aurora Empresa`, `Aurora Finance`. A variant may restrict capabilities or policies, but never relax Constitution, Laws or security guarantees.

## Interfaces

```text
Genome.resolve(genome_id, installation_context) -> GenomeResolution
Genome.bootstrap(resolution_id) -> BootstrapPlan
Genome.validateOverride(change) -> ALLOW|DENY|REVIEW
```

## Mandatory rules

1. The Genome MUST be signed, versioned, reproducible and independent of secrets.
2. Contains no user memory, credentials, relationships, beliefs or execution state.
3. The entire installation preserves reference to the effective Genome in Mind State and Life History.
4. Incompatible changes create new Genome/version and migration plan; do not silently alter existing instances.

## Limit and error cases

- **Override weakens a Law:** reject in resolution.
- **Required capacity unavailable:** bootstrap becomes blocked/degraded, does not invent a replacement.
- **Invalid signature:** do not create instance.

## Justification

The Genome allows you to create specialized Auroras from a common root, maintaining traceability of what is innate versus learned.

## Future expansions

Genome catalog, declarative composition of variants, compatibility tests and promotion between environments.