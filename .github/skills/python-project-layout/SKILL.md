---
name: python-project-layout
description: Organize StockRipper source, tests, and Python tooling so the project stays maintainable and uv-friendly.
---

# Python Project Layout Skill

## When to Use

- Creating or refactoring project structure
- Adding Python modules, tests, scripts, or dashboards
- Aligning the repo with `pyproject.toml`, `uv.lock`, and `src/stockripper/`

## Key Rules

1. Keep application code in `src/stockripper/` and tests under `tests/`.
2. Keep provider adapters, models, workflow orchestration, and ledger code separated by responsibility.
3. Keep Python tooling explicit in `pyproject.toml` and use uv commands for environment management.
4. Keep scripts and local automation under `scripts/` so they do not mix with runtime code.
