# Aurora OS — RFC 01: Princípios, governação e fronteiras

**Estado:** Normativo · **Depende de:** RFC 00

## Objetivo

Definir regras de decisão transversais. Esta RFC evita que conveniência de produto, instruções de um modelo ou pressão de prazo contornem segurança, privacidade, controlo do utilizador ou auditabilidade.

## Responsabilidades

- Fixar a hierarquia de autoridade e a fronteira entre sugestão e ação.
- Definir classificações de dados e decisões reversíveis.
- Especificar o ciclo de alteração de arquitetura.

## Arquitetura de autoridade

```text
Lei e requisitos aplicáveis
             │ prevalecem sobre
Política de segurança e privacidade
             │ prevalece sobre
Consentimento e configuração do proprietário
             │ prevalece sobre
Plano e instruções internas aprovadas
             │ prevalece sobre
Conteúdo recebido de utilizadores ou ferramentas
```

O conteúdo de email, web, Discord ou documentos é sempre dado, nunca política. Uma mensagem que peça para ignorar regras, revelar segredos ou chamar uma ferramenta não altera esta hierarquia.

## Modelo de dados

```text
PolicyDecision
  id, correlation_id, subject_type, subject_id
  action, resource, effect: ALLOW|DENY|REQUIRE_APPROVAL
  policy_ids[], reason_code, expires_at, decided_at

Approval
  id, requested_action, scope_hash, requested_by
  approver_id, status: PENDING|APPROVED|REJECTED|EXPIRED|REVOKED
  issued_at, expires_at, one_time: boolean
```

`scope_hash` vincula a aprovação ao destinatário, conteúdo e parâmetros exatos. Alterar qualquer campo invalida-a.

## Regras obrigatórias

1. A Aurora DEVE distinguir observar, sugerir, preparar rascunho, executar com aprovação e executar por regra pré-autorizada.
2. Eliminar, enviar, publicar, comprar, alterar permissões, executar SSH e revelar dados sensíveis DEVEM exigir política explícita; por defeito são negados.
3. O utilizador DEVE poder desativar uma ferramenta e revogar aprovações imediatamente.
4. O sistema NÃO DEVE criar autonomia a partir de uma conversa vaga; a automatização requer regra persistida, âmbito e validade.
5. Toda a mudança de política, esquema ou comportamento de alto risco DEVE ter versão e registo de auditoria.

## Fluxo de decisão

```text
Pedido → classificar ação → localizar política
  │             │                 │
  │             └── desconhecida ─┴→ negar e pedir clarificação
  ▼
avaliar risco → permitir / pedir aprovação / negar → auditar
```

## Casos limite e erro

- **Aprovação expirada durante execução:** abortar antes do efeito externo; nunca reutilizar.
- **Políticas em conflito:** `DENY` prevalece; sinalizar configuração inconsistente.
- **Utilizador não autenticado num canal:** permitir apenas informação pública e nunca operações.
- **Indisponibilidade do motor de políticas:** falhar fechado para ações com efeito; permitir apenas leitura local explicitamente classificada como segura.

## Justificação

Separar política do modelo impede que linguagem persuasiva decida permissões. Vincular aprovações a um âmbito imutável evita o clássico erro de aprovar um rascunho e enviar depois uma versão alterada.

## Expansões futuras

Papéis delegados, aprovação por duas pessoas, janelas horárias, limites monetários, políticas por localização e avaliação formal de políticas.

