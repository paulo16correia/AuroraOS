# Aurora Platform — RFC 036: Genome

**Estado:** Normativo · **Depende de:** RFC 035, 040

## Objetivo

Definir o **Genome**: o manifesto versionado de tudo o que uma instância da Aurora recebe no nascimento. O Genome reúne configuração inata e herdável sem confundir dados de instalação, personalidade amadurecida ou memória adquirida.

## Arquitetura

```text
Genome base + variantes autorizadas + ambiente de instalação
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

## Estruturas de dados

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

Exemplos de famílias: `Aurora Personal`, `Aurora Casa`, `Aurora Empresa`, `Aurora Finance`. Uma variante pode restringir capacidades ou políticas, mas nunca relaxar Constituição, Leis ou garantias de segurança.

## Interfaces

```text
Genome.resolve(genome_id, installation_context) -> GenomeResolution
Genome.bootstrap(resolution_id) -> BootstrapPlan
Genome.validateOverride(change) -> ALLOW|DENY|REVIEW
```

## Regras obrigatórias

1. O Genome DEVE ser assinado, versionado, reprodutível e independente de segredos.
2. Não contém memória do utilizador, credenciais, relações, crenças ou estado de execução.
3. Toda a instalação preserva referência ao Genome efetivo na Mind State e Life History.
4. Alterações incompatíveis criam novo Genome/versão e plano de migração; não alteram silenciosamente instâncias existentes.

## Casos limite e erro

- **Override enfraquece uma Lei:** rejeitar na resolução.
- **Capacidade exigida indisponível:** bootstrap fica bloqueado/degradado, não inventa uma substituição.
- **Assinatura inválida:** não criar instância.

## Justificação

O Genome permite criar Auroras especializadas a partir de uma raiz comum, mantendo rastreabilidade do que é inato versus aprendido.

## Expansões futuras

Catálogo de Genomes, composição declarativa de variantes, testes de compatibilidade e promoção entre ambientes.
