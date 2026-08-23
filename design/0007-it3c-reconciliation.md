# Design 0007 — Reconciliação de execuções indeterminadas (It.3, terceiro incremento)

**Estado:** Implementado · **Data:** 2026-08-23
**Depende de:** `design/0001-mcp-pipeline-slice1.md` (estados de idempotência do It.0)

## O problema

A idempotência do It.0 tem um estado `EXECUTING` que, deliberadamente, **não
é retentável**: se um pedido está a meio do efeito, repeti-lo pode duplicar
esse efeito. Enquanto o processo está vivo isso é correto.

Se o processo morre a meio, a linha fica em `EXECUTING` para sempre. A chave
de idempotência fica **encravada**: qualquer tentativa futura recebe
`in_progress` de uma execução que já não existe, e não há caminho de saída.
Nem retentar, nem desistir.

Note-se que isto **não** é o mesmo que o caminho de cancelamento, que já
liquidava como `UNKNOWN`. O buraco é a morte abrupta, em que nenhum código
nosso chega a correr.

## A solução

`ReconcileStaleAsync` move para `UNKNOWN` as reservas em `EXECUTING` cujo
`updated_at_utc` seja mais antigo que uma janela configurável, e corre no
arranque do servidor, antes de aceitar tráfego.

`UNKNOWN` é a resposta honesta: o efeito **pode** ter acontecido. Não
sabemos. O chamador recebe `unknown_state` em vez de um `in_progress` falso,
o que é a diferença entre "espera mais um pouco" e "verifica o que aconteceu
antes de tentar outra vez".

Três decisões que interessam:

- **Só `EXECUTING`.** Linhas `ACCEPTED` nunca tentaram o efeito, portanto são
  abandonáveis e não indeterminadas — tratá-las como indeterminadas seria
  pessimismo desnecessário sobre uma reserva inofensiva.
- **A janela conta a partir de `updated_at_utc`**, carimbado quando a linha
  entra em `EXECUTING`. Mede-se o início do *efeito*, não o do pedido.
- **Omissão de 15 minutos**, configurável. Uma janela curta declararia
  indeterminada uma execução lenta mas viva — roubar-lhe a reserva por baixo
  seria pior do que o encravamento que estamos a resolver.

## Testes

4 testes novos: uma reserva `EXECUTING` obsoleta passa a `UNKNOWN` e a chave
deixa de estar encravada; uma `EXECUTING` recente fica intocada; uma
`ACCEPTED` antiga é ignorada; e a reconciliação é idempotente (a segunda
passagem move zero).

O primeiro teste afirma explicitamente o encravamento **antes** de
reconciliar, para o teste documentar o problema e não só a correção.

## Adiado

Reconciliação periódica em runtime (só corre no arranque); inspeção das
linhas `UNKNOWN` por uma ferramenta de operador; e correlação automática
com o log de auditoria para adivinhar se o efeito chegou a acontecer — isso
exige que cada capability saiba dizer se o seu efeito é verificável, o que
é matéria de outro incremento.
