---
name: python-scripts
description: Use lightweight Python scripting for StockRipper experiments, local utilities, and one-off checks with uv-managed environments.
---

# Python Scripting Skill

## When to Use

- Running a one-off analysis or inspection script
- Probing provider responses, ledger records, or replay outputs
- Creating local automation for manual validation

## Key Rules

1. Prefer `uv run python` or `uv run` for scripts so dependencies are predictable.
2. Keep scripts small and disposable unless they are part of the supported repo tooling.
3. Do not use ad hoc scripts to bypass tests, risk checks, or audit trails.
4. If a script becomes reusable, move it into `scripts/` or the package with tests.
