---
name: determinism-and-replay
description: Apply StockRipper's determinism and replay-safety rules to workflow changes so they remain stable, reproducible, and production-safe across every strategy track.
---

# Determinism and Replay Safety Skill

## When to Use

- Reviewing workflow changes for hidden state or nondeterministic behavior
- Hardening replay and backtest logic
- Verifying that the same inputs produce the same decision path

## Key Rules

1. Keep state transitions explicit and inspectable.
2. Avoid timestamp or environment drift in replay-critical code.
3. Ensure look-ahead, stale-data, and rate-limit conditions are test-covered.
4. Treat replay and decision history as first-class correctness requirements.
