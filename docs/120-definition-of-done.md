# Aurora Platform — 120: Definition of Done

**Tipo:** gate de entrega · **Depende de:** documentos 100, 105 e 110

## Regra

Um módulo, capability ou Application não está concluído quando “funciona localmente”. Está concluído quando cumpre todos os critérios aplicáveis abaixo.

## Checklist obrigatória

- [ ] Responsabilidade, dono de estado e RFC/ADR de origem identificados.
- [ ] Contratos de entrada/saída, estados e erros tipados/versionados.
- [ ] Política, permissões e classificação de dados aplicadas por defeito.
- [ ] Eventos produzidos/consumidos documentados; consumidor/produtor idempotente.
- [ ] Logs, métricas, health check, auditoria e `correlation_id` implementados.
- [ ] Testes unitários, de contrato, integração e casos de falha/recovery passam.
- [ ] Leis e Constitution avaliadas; não existe bypass conhecido.
- [ ] Migração, rollback e retenção definidos para estado persistente.
- [ ] Documentação de operador e utilizador atualizada; revisão de código aprovada.
- [ ] Para efeitos externos: sandbox, aprovação, `UNKNOWN`/reconciliação e kill switch demonstrados.

## Evidência

Cada item concluído referencia testes, dashboards, logs de deploy ou documentação. Ausência de evidência deixa o item aberto; uma exceção só é válida com ADR/risco aceite e data de expiração.

