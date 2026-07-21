# ADR-028 - Disputas preservam evidencia sem inferencia

**Estado:** Aceite  
**Data:** 2026-07-19

## Decisao

Uma contestacao explicita move a assertion para `DISPUTED`. A Aurora preserva
as duas mensagens como evidencia, mas nao trata a relation como atual,
historica ou valida num instante especifico.

## Justificacao

Uma correcao diz que a assertion nao e segura, mas nao prova automaticamente
qual e o facto correto. Inferir uma negacao ou escolher uma versao ocultaria a
incerteza e criaria uma fonte de verdade falsa.

## Alternativa rejeitada

- **Apagar a assertion:** destroi auditabilidade e impossibilita revisao.
