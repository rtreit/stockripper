---
name: code-testing
description: Generate StockRipper-focused pytest coverage for workflow, risk, provider, and replay changes using a research-plan-implement workflow.
---

# Code Testing Skill

## When to Use

- Adding tests for a new feature or bug fix
- Creating unit, integration, or replay tests for the Python codebase
- Improving coverage for risk gates, ledger logic, or provider adapters

## Key Rules

1. Start with the smallest failing test that proves the behavior change.
2. Use pytest fixtures for provider stubs, sample multi-track runs, and deterministic ledger state.
3. Cover happy paths, edge cases, and failure paths for every affected track.
4. Always add coverage for the universal floors and for the per-track risk-gate decisions when touching execution-adjacent code.
5. Keep tests aligned with StockRipper's audit and determinism expectations.
