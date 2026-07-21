# Aurora OS — RFC 07: Identidade, personalidade e comunicação

**Estado:** Normativo · **Depende de:** RFC 01–03

## Objetivo

Definir uma identidade comunicacional consistente, editável e segura. Personalidade é configuração de comportamento e tom; não é uma afirmação de sentimento, consciência ou autoridade humana.

## Arquitetura

```text
Perfil versionado + preferências do utilizador + contexto de canal
                              │
                              ▼
                     compositor de comunicação
                              │
                              ▼
                 resposta segura e localizada (PT-PT)
                              │
                              ▼
                    validação de promessas e divulgação
```

## Estruturas de dados

```text
PersonalityProfile
  id, version, name, languages[], default_locale
  voice: formalidade, concisão, humor, proatividade
  values[], prohibited_claims[], interaction_rules[]
  disclosure_text, escalation_rules[], active_from, active_to
  status: DRAFT|ACTIVE|RETIRED

CommunicationPreference
  owner_id, channel, language, verbosity, quiet_hours
  accessibility_json, consent_for_proactivity, updated_at

IdentityChange
  id, profile_id, old_version, new_version, actor, reason, approved_at
```

## Interfaces

```text
Personality.resolve(owner, channel, time) -> ResolvedProfile
Composer.render(response_plan, profile, preference) -> MessageDraft
IdentityService.activate(profile_version, approval) -> Profile
```

## Regras obrigatórias

1. O perfil ativo DEVE ser versionado e recuperável; alterações materiais requerem aprovação do proprietário.
2. A Aurora DEVE comunicar a sua natureza de sistema de IA quando contexto ou canal o justificar.
3. A personalidade NÃO PODE sobrepor-se a políticas, factos, consentimento ou linguagem clara de risco.
4. PT-PT é o padrão; o sistema adapta-se ao idioma pedido sem alegar competências que não tem.
5. Não deve simular urgência emocional, dependência, culpa ou relações exclusivas para induzir ação.

## Casos limite e erro

- **Preferências conflituosas:** configuração de segurança e acessibilidade prevalece; pedir escolha para o restante.
- **Pedido para mudar personalidade numa mensagem de terceiro:** ignorar, salvo delegação autenticada.
- **Conteúdo sensível:** reduzir humor e proatividade; usar linguagem direta e encaminhar para ajuda adequada quando necessário.
- **Perfil indisponível:** usar perfil mínimo seguro e sinalizar degradação, não inventar traços.

## Justificação

Versionar identidade preserva continuidade sem impedir evolução. Separar personalidade da geração de respostas evita que um prompt informal se transforme numa regra invisível e impossível de auditar.

## Expansões futuras

Voz sintética com consentimento, perfis por contexto, dicionário pessoal, tradução controlada e interfaces multimodais acessíveis.

