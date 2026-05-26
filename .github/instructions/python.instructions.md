# Python Tooling Instructions

## Purpose

StockRipper is a Python-first project, so these rules govern Python code, environment setup, and developer workflow.

## Environment Rules

- Use uv for all environment management. Prefer `uv sync --dev`, `uv run`, `uv lock`, and `uvx` for one-off tools.
- Keep `pyproject.toml` and `uv.lock` updated together when dependencies change.
- Use Python 3.12+ unless the spec or tooling explicitly requires otherwise.
- Avoid relying on globally installed Python packages for repo tasks.

## Code and Dependency Rules

- Prefer small, typed modules under `src/stockripper/` and focused tests under `tests/`.
- Keep dependencies explicit and reviewable; do not add packages without a clear need in the spec.
- Use type hints, Pydantic models, and structured errors for provider and workflow boundaries.
- Keep network, rate limiting, and provider access isolated behind adapters.

## Validation Commands

Use the following workflow for Python changes:

- `uv sync --dev`
- `uv run ruff check .`
- `uv run mypy src/stockripper tests`
- `uv run pytest`
- `uv run pyright` when the project uses it

## Project-Specific Expectations

- Make agent, judge, and risk code deterministic and testable.
- Keep LLM prompts and tool calls source-auditable.
- Do not bypass the risk gate or order adapter for convenience.
- When a change affects paper-trading behavior, add replay or integration coverage.
