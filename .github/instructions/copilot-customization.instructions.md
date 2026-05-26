# Copilot Customization Instructions

This document explains how to maintain the StockRipper Copilot customization files in `.github/`.

## Customization Types

| Type | Location | File Pattern | Purpose |
|---|---|---|---|
| Instructions | `.github/instructions/` | `*.instructions.md` | Always-on StockRipper rules and workflow guidance |
| Skills | `.github/skills/<name>/` | `SKILL.md` | Reusable domain knowledge for Python, LangGraph, Alpaca, risk, and audit work |
| Agents | `.github/agents/` | `*.agent.md` | Named specialists for planning, implementation, investigation, review, and security |
| Prompts | `.github/prompts/` | `*.prompt.md` | Reusable task templates for feature work, debugging, or workflow changes |

## StockRipper-Specific Rules

- Prefer the project spec over copied guidance from other repositories.
- Keep Python and uv guidance current. Use `uv run`, `uv sync --dev`, and `uv lock` rather than ad hoc environment setup.
- Keep skills, prompts, and agents aligned to LangGraph, Alpaca, SEC EDGAR, deterministic risk gates, and auditability.
- Remove stale references to other projects or technologies when they no longer apply.

## Writing New Files

- Start with a clear `name` and `description` in YAML frontmatter for skills, agents, and prompts.
- Keep each instruction focused on one domain.
- Reference `PROJECT_SPEC.md`, `pyproject.toml`, `uv.lock`, `src/stockripper/`, and `tests/` where relevant.
- Use real StockRipper commands and workflows instead of copied examples from unrelated codebases.
