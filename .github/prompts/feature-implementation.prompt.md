---
name: feature-implementation
description: Implement a scoped StockRipper feature using the project spec, uv-managed Python tooling, and deterministic risk controls.
---

# Feature Implementation Prompt

Implement the requested StockRipper feature with the following expectations:

1. Start from `PROJECT_SPEC.md` and identify the affected workflow area and which strategy track(s) are impacted.
2. Keep the change aligned with LangGraph orchestration (including per-track sub-graphs), Pydantic models, provider adapters, the per-track risk gate, the universal floors, the execution adapter, and the ledger.
3. Preserve per-track deterministic risk checks, universal floors, source-backed evidence behavior, and full autonomy (no per-order human approval).
4. Use uv for Python environment and validation commands.
5. Add focused tests for behavior, replay, per-track risk, universal-floor enforcement, and provider integration where appropriate.
6. Summarize the impact on auditability, safety, head-to-head leaderboard interpretation, and operator observability.
