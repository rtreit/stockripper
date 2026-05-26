---
name: langgraph-change
description: Implement or review a StockRipper LangGraph workflow change with replayable state, auditability, and test coverage.
---

# LangGraph Workflow Change Prompt

Use this workflow when implementing or reviewing a LangGraph workflow change in StockRipper.

1. Identify whether the change belongs in the scheduler, top-level state model, the Strategy Tracks Manager, a per-track sub-graph, provider adapters, the research layer, a track judge, the per-track risk gate, the execution adapter, or the ledger.
2. Preserve checkpointed state, replayability, deterministic behavior, and full autonomy (no per-order human approval interrupts).
3. Add or update a failing regression test first when fixing a bug.
4. Implement the smallest correct fix that keeps the workflow auditable per track and across tracks.
5. Run the Python validation loop with uv:
   - `uv sync --dev`
   - `uv run ruff check .`
   - `uv run mypy src/stockripper tests`
   - `uv run pytest`
6. Summarize the impact, validation, and any remaining workflow, per-track, or migration considerations.
