# Aurora OS — RFC 033: Modelo de recursos e energia operacional

**Estado:** Normativo · **Depende de:** RFC 027, 040

## Objetivo

Modelar recursos reais e orçamentos de trabalho: CPU, memória, disco, rede, fila, custo de modelo, tempo e limites de fornecedores. “Energia” é uma metáfora de produto para capacidade operacional disponível; não representa estado biológico ou emocional.

## Arquitetura

```text
Infraestrutura + Self + limites de custo + carga de tarefas
                         │
                         ▼
                ResourceState e orçamento
                         │
                         ▼
        Scheduler / Attention / Decision Engine
                         │
                         ▼
       executar, adiar, reduzir qualidade ou pedir intervenção
```

## Estruturas de dados

```text
ResourceState
  id, observed_at, cpu_pct, memory_pct, disk_pct, network_state
  queue_depth, active_workers, model_cost_today, rate_limit_state
  operational_energy: 0..1, status: NORMAL|CONSTRAINED|CRITICAL|UNKNOWN

ResourceBudget
  id, scope, max_cost, max_tokens, max_cpu_seconds, max_concurrency
  time_window, reserve_for_critical, policy_id
```

## Interfaces

```text
Resources.observe() -> ResourceState
Resources.reserve(task, estimate) -> Reservation
Resources.release(reservation_id, outcome) -> ResourceState
Resources.admit(decision, budget) -> ALLOW|DEFER|DENY
```

## Regras obrigatórias

1. Trabalho de segurança, recuperação e operações confirmadas tem reserva definida; curiosidade, indexação e manutenção são adiadas primeiro.
2. Nenhuma estimativa de recursos é tratada como ilimitada; tarefas recebem reserva e prazo.
3. `CRITICAL` bloqueia tarefas não essenciais e pode degradar consultas, mas não elimina dados nem interrompe ações externas sem reconciliação.
4. Custo e consumo observados entram em auditoria operacional, sem expor conteúdo sensível.

## Casos limite e erro

- **CPU 95%:** adiar indexação/consolidação, limitar concorrência e sinalizar Need de manutenção.
- **Orçamento de modelo esgotado:** usar opção aprovada mais barata, adiar ou pedir decisão; nunca faturar sem limite.
- **Métrica indisponível:** assumir `UNKNOWN` e adotar admissão conservadora.

## Justificação

Este modelo evita que a Aurora se torne menos fiável precisamente quando há mais trabalho. A prioridade passa a depender de recursos reais, não de uma fila cega.

## Expansões futuras

Previsão de carga, prioridade preemptiva, múltiplos nós, custo por objetivo e políticas ambientais/horárias.
