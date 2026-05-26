---
name: python-linting
description: Apply StockRipper’s Python linting and static-analysis expectations with uv-managed tooling.
---

# Python Linting Skill

## When to Use

- Reviewing Python code quality, consistency, or type-safety issues
- Setting up linting and static analysis for new modules
- Triage warnings from ruff, mypy, or pyright

## Key Rules

1. Use `uv run ruff check .` for linting and import hygiene.
2. Use `uv run mypy src/stockripper tests` for type-safety checks where the repo uses mypy.
3. Keep violations actionable and tied to a real code-quality or correctness issue.
4. Prefer small, local fixes over broad suppressions.
