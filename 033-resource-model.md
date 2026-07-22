# Aurora OS — RFC 033: Operational Power and Resource Model

**State:** Normative · **Depends on:** RFC 027, 040

## Objective

Model actual resources and job budgets: CPU, memory, disk, network, queue, model cost, time, and vendor limits. “Energy” is a product metaphor for available operational capacity; does not represent biological or emotional state.

## Architecture

```text
Infrastructure + Self + cost limits + task load
                         │
                         ▼
ResourceState and budget
                         │
                         ▼
        Scheduler / Attention / Decision Engine
                         │
                         ▼
execute, postpone, reduce quality or request intervention
```

## Data structures

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

## Mandatory rules

1. Security work, recovery and confirmed operations have a defined reserve; curiosity, indexing and maintenance are postponed first.
2. No resource estimate is treated as unlimited; tasks receive reservation and deadline.
3. `CRITICAL` blocks non-essential tasks and may degrade queries, but does not eliminate data or stop external actions without reconciliation.
4. Observed cost and consumption are included in operational audit, without exposing sensitive content.

## Limit and error cases

- **CPU 95%:** postpone indexing/consolidation, limit competition and signal maintenance need.
- **Exhausted model budget:** use cheaper approved option, postpone or request decision; Never bill without a limit.
- **Metric unavailable:** assume `UNKNOWN` and adopt conservative admission.

## Justification

This model prevents the Aurora from becoming less reliable precisely when there is more work. Priority now depends on real resources, not a blind queue.

## Future expansions

Load forecasting, preemptive priority, multiple nodes, cost per objective and environmental/time policies.