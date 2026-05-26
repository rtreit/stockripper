---
name: performance-observability
description: Review StockRipper runtime and workflow performance with an emphasis on latency, provider throttling, parallel-track scaling, and ledger throughput.
---

# Performance and Observability Skill

## When to Use

- Reviewing per-window or intraday workflow latency across multiple parallel tracks
- Investigating provider rate-limiting, retries, or heavy data-processing paths shared across tracks
- Auditing whether unattended autonomous runs stay responsive and bounded

## Key Rules

1. Measure the hot path before micro-optimizing.
2. Watch for repeated provider calls, redundant parsing, and unbounded retries — especially when N tracks fan out the same candidate universe.
3. Keep metrics and logs focused on actionable operational signals (per-track latency, queue depth, Alpaca error rate, schema failure rate).
4. Use deterministic fixtures when benchmarking or replaying workflow behavior so per-track scaling effects are reproducible.
