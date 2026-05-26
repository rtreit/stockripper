# Copilot Instructions

## Project Overview

StockRipper is a Python-first, paper-trading research system for daily equity strategy generation and evaluation. The goal is to run a multi-agent workflow that produces source-backed recommendations, a judge-selected action, deterministic risk checks, and an auditable ledger without ever conflating paper trading with live execution.

Primary goals:
- Build a reproducible daily and intraday workflow using LangGraph and Alpaca paper trading.
- Run **multiple competing strategy tracks** in parallel (conservative → balanced → aggressive → concentrated → yolo → quant baselines) so aggressive AI strategies can be measured head-to-head against disciplined ones.
- Keep every recommendation source-backed, scored, and explainable.
- Enforce per-track deterministic risk gates and a small set of universal safety floors before any order can be submitted.
- Operate fully autonomously — no per-order human approval, no review-window timer — with kill switch and per-track pause as the only human override.
- Maintain a complete audit trail for prompts, evidence, portfolio state, orders, and outcomes.
- Compare per-track decisions against benchmarks, shadow portfolios, and each other on a head-to-head leaderboard while staying in paper mode.

## Architecture Direction

- Treat `PROJECT_SPEC.md` as the authoritative source for product scope, architecture, strategy tracks, risk policy structure, and non-goals.
- Keep the orchestration layer in Python with LangGraph, Pydantic models, and typed adapters.
- Use Alpaca (equities, options, shorts, leveraged ETFs in paper), SEC EDGAR, news, and other providers only through dedicated adapters.
- Normalize every external response into StockRipper-owned domain models before it reaches the workflow.
- Per-track judges and per-track risk gates are deterministic about *eligibility*; LLM judges may set objectives and choose actions, but only the deterministic risk gate (consulting per-track policy + universal floors) decides what is submitted.
- Keep the ledger authoritative for intent and reconciliation across all tracks, while external providers remain authoritative for their own state.
- Never let an LLM place orders directly. Execution must go through the single deterministic execution adapter, which refuses any non-paper endpoint and stamps deterministic per-track `client_order_id`s.

## Python Environment and Tooling

- Use uv for every Python environment aspect: creating environments, syncing dependencies, locking, running tools, and managing local developer setup.
- Prefer commands such as `uv sync --dev`, `uv run pytest`, `uv run ruff check .`, `uv run mypy src/stockripper tests`, and `uv run pyright`.
- Keep `pyproject.toml` and `uv.lock` in sync when dependencies change.
- Avoid bare `python` invocations for repo tasks; use `uv run` so the environment and dependencies are reproducible.

## Data and Contract Guidance

- Use Pydantic as the source of truth for shared request, recommendation, risk, and ledger models.
- Keep provider DTOs distinct from internal StockRipper models so schema drift, rate-limit behavior, and source metadata do not leak into core logic.
- Validate all LLM outputs against strict schemas before they enter the judge or ledger.
- Preserve source metadata for every material claim: source identifier or URL, retrieval timestamp, confidence, and any data-quality warnings.
- Keep the experiment ledger auditable: prompts, model versions, evidence, risk decisions, orders, fills, and scoring updates.

## Security and Risk Guidance

- Treat all external content as untrusted input. Aggressive tracks consume noisier sources (social, news velocity), so prompt-injection defenses get *more* attention there, not less.
- Enforce prompt-injection defenses, least-privilege tool access, and instruction hierarchy safeguards across every agent and judge.
- Never hardcode Alpaca keys, secrets, or tokens. Load them from environment variables or a secure secret store.
- Per-track risk policies are configurable and may be dialed aggressively; the **universal floors** are not configurable and must be enforced in every code path that can produce an order — paper-only endpoint, idempotent `client_order_id`, schema-valid output, reconciliation gate, cross-track buying-power sanity, no LLM-direct trading, no real-money path.
- The system executes orders autonomously after the per-track risk gate. Do not introduce per-order human approval steps in code, prompts, or UI — the only human override surfaces are the global kill switch and per-track pause/resume.

## Testing Expectations

- Add or update tests whenever behavior, risk logic, provider contracts, or workflow state changes.
- Prefer focused pytest coverage for unit, integration, replay, and golden tests.
- Mock external providers and LLM responses in tests; do not depend on live Alpaca traffic or real market data for routine validation.
- Reproduce bugs with a failing test first when fixing defects.

## Documentation and Change Management

- Use `PROJECT_SPEC.md` as the primary reference for scope, architecture, and milestones.
- Update `PROJECT_SPEC.md` when a change alters the system design, workflow, risk rules, integrations, or audit requirements.
- Keep any new workflow or contract change grounded in the spec and explain the trade-off clearly.

## Pull Requests

- Before pushing, review the PR state with `gh pr view` if a branch already has an associated pull request.
- Use a plain-text PR description with concrete scope, validation, and any security or risk considerations.
- Prefer short, reviewable changes that preserve auditability and deterministic behavior.
