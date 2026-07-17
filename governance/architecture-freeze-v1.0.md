# Aurora Platform — Architecture Freeze v1.0

**Efetivo em:** 2026-07-17  
**Baseline congelada:** commit `a720961` e respetivas correções aprovadas na revisão v1.0

## Decisão

A arquitetura cognitiva v1.0 está congelada. Não serão criadas novas RFCs de conceito, módulos, objetos de domínio ou camadas até a implementação revelar uma falha material que não possa ser resolvida dentro dos contratos existentes.

## O que fica congelado

- Hierarquia `Aurora Platform → Aurora Kernel / Aurora Mind / Aurora Applications`.
- Constituição, Leis e fronteiras de autoridade.
- Modelo de domínio, contratos de ciclo de vida e comunicação por Event Bus.
- Separação entre missão, objetivo, plano, tarefa, decisão, ação e observação.
- Responsabilidades de Kernel, Mind e Applications.

## Alterações permitidas

1. Correções editoriais sem alteração semântica.
2. Clarificações compatíveis que não acrescentem estado, efeito, interface pública ou dependência.
3. Implementações, testes, documentação de operação e exemplos conformes.
4. ADRs para alterações necessárias, cada um com impacto, migração, compatibilidade e aprovação.

## Alterações bloqueadas

- Novos subsistemas cognitivos, novos tipos de estado canónico ou novas exceções às Leis.
- Aumento de autonomia, permissões ou superfícies de ferramentas sem ADR e revisão de segurança.
- Alterar uma RFC diretamente para resolver desacordo de implementação.

## Critério para reabrir a arquitetura

Uma proposta só reabre o freeze se demonstrar: (a) violação de Lei/Constituição, (b) ambiguidade que bloqueia uma implementação de referência, (c) falha de segurança/recuperação, ou (d) incompatibilidade incontornável entre contratos congelados. O pedido inicia-se por ADR, não por uma RFC nova.

