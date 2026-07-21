# Aurora OS — RFC 029: Modelo de relações e preferências

**Estado:** Normativo · **Depende de:** RFC 04, 028, 040–042

## Objetivo

Modelar relações entre pessoas, organizações, contas, projetos e recursos; e distinguir essas relações de preferências operacionais do utilizador. O sistema deve saber “quem se relaciona com quem” sem inferir intimidade, autoridade ou acesso não demonstrados.

## Arquitetura

```text
Entidades do World Model + evidência temporal
                    │
        ┌───────────┴────────────┐
        ▼                        ▼
 RelationshipAssertion      Preference
        │                        │
        ▼                        ▼
 grafo/World Model       decisão, estilo e recomendação
```

## Estruturas de dados

```text
RelationshipAssertion
  id, subject_ref, relation_type, object_ref
  qualifiers_json, authority_scope, confidence, evidence_refs[]
  valid_from, valid_to, status: PROPOSED|ACTIVE|DISPUTED|ENDED|RETRACTED

Preference
  id, owner_ref, subject_ref, dimension, value_json
  strength: 0..1, basis: EXPLICIT|OBSERVED|INFERRED
  evidence_refs[], scope_json, status: CANDIDATE|ACTIVE|REJECTED|EXPIRED
  review_at, consent_required
```

Relações podem ser familiares, profissionais, contratuais, de pertença, dependência, contacto ou propriedade. `authority_scope` é sempre explícito: “é cliente” não dá autorização para agir em nome da pessoa. Preferências incluem ferramenta, formato, horário, tom ou opção técnica; não definem personalidade da Aurora.

## Interfaces

```text
Relationships.assert(candidate, evidence) -> RelationshipAssertion
Relationships.end(id, evidence) -> RelationshipAssertion
Preferences.setExplicit(owner, dimension, value) -> Preference
Preferences.infer(candidate) -> Preference
Preferences.resolve(owner, context) -> Preference[]
```

## Regras obrigatórias

1. Relação, permissão e identidade são objetos separados; nenhum é derivado implicitamente de outro.
2. Preferências inferidas não podem acionar compras, comunicação externa, dados sensíveis ou alterações persistentes sem confirmação.
3. Relações de terceiros só são armazenadas se relevantes, autorizadas e sujeitas a retenção proporcional.
4. Histórico temporal deve preservar início/fim; reatribuir uma relação não reescreve o passado.

## Casos limite e erro

- **Duas pessoas com o mesmo nome:** não criar relação até resolução de entidade concluir.
- **Preferência explícita contradiz inferida:** explícita prevalece e a inferida é `REJECTED`.
- **Relação expira:** remove-se de decisões atuais, mas conserva-se evidência histórica segundo política.

## Justificação

Relações tornam o World Model socialmente útil; preferências tornam decisões pessoais sem converter hábitos em traços de personalidade ou direitos de ação.

## Expansões futuras

Relações de equipa, delegação formal, preferências contextuais, importação consentida de contactos e políticas de privacidade por relação.

