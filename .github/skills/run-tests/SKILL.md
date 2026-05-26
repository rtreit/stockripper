---
name: run-tests
description: Run and triage StockRipper tests with uv-managed Python tooling and focused pytest workflows.
---

# Run Tests Skill

## When to Use

- Executing pytest for a targeted area
- Checking whether a bug fix or feature change is passing locally
- Triage failing tests in the workflow, risk, or provider layers

## Key Rules

1. Use `uv run pytest` for repo test execution.
2. Prefer focused test selection for speed during iterative work.
3. If a test fails, confirm whether the failure is a real regression or a flaky dependency on time, network, or randomness.
4. Keep test commands reproducible via `uv` rather than shell-specific global installs.
