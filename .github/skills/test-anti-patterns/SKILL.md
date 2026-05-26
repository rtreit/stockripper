---
name: test-anti-patterns
description: Review StockRipper tests for flaky or misleading patterns that undermine paper-trading safety, determinism, and replayability.
---

# Test Anti-Patterns Skill

## When to Use

- Reviewing new or existing tests before merge
- Catching flaky or over-mocked test coverage
- Improving regression tests for provider, risk, or replay behavior

## Key Rules

1. Avoid live network calls, time-sensitive assertions, or random data in unit tests.
2. Mock providers and LLM responses explicitly; test the boundary, not the implementation details.
3. Prefer deterministic fixtures and replayable sample multi-track runs.
4. Keep tests focused on behavior that matters to the spec: per-track risk decisions, universal-floor enforcement, order eligibility, reconciliation, autonomy of the execution path, and auditability.
