# Performance Instructions

## Purpose

These instructions govern the performance and observability expectations for StockRipper’s daily workflow, provider adapters, and reporting surfaces.

## Performance Rules

- Keep windows responsive even when N strategy tracks run in parallel and intraday triggers fire.
- Prefer predictable O(1) or O(log n) access paths for hot operations such as per-track portfolio state, risk checks, and ledger reads.
- Avoid unnecessary API calls, repeated parsing, and duplicate provider work — share normalized data across tracks rather than refetching per track.
- Cache provider results and normalized data where safe and measurable.
- Measure before and after when changing workflow, replay, or risk paths.

## Workflow Guidance

- Keep LangGraph state transitions and checkpointing lightweight and deterministic, especially in the per-track sub-graphs.
- Rate-limit Alpaca and external data calls system-wide, not per-track; aggressive tracks must not be able to starve conservative ones.
- Use bounded retries and explicit failure paths for provider errors.
- Keep ledger writes and reconciliation logic efficient enough for unattended overnight and weekend autonomous runs.

## Observability Guidance

- Track per-track latency, retry count, provider failure rate, queue depth, and ledger reconciliation outcome.
- Log actionable operational signals without exposing secrets or sensitive account data.
- Keep dashboards and terminal output focused on the head-to-head leaderboard, per-track run state, risk decisions, and evidence quality.
