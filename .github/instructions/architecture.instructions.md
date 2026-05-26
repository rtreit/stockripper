# Architecture Instructions

## Purpose

These instructions define the StockRipper architecture and workflow boundaries for the Python/LangGraph implementation.

## Core Workflow

The per-decision-window path runs concurrently for every enabled strategy track:

1. Scheduler triggers a window (open / midday / close / intraday-on-signal).
2. Shared universe builder produces candidate tickers (per-track filtering applied next).
3. Shared data ingestion normalizes market, fundamentals, options-chain, short-interest, filings, and news data.
4. The Strategy Tracks Manager fans out: for each enabled track, the track-configured council runs in parallel.
5. Skeptic, risk-manager, and prompt-injection detector run for every track (non-bypassable).
6. The track's judge selects an action plan against the track's objective function.
7. The per-track deterministic risk gate evaluates eligibility against the track's policy and the universal floors.
8. The execution adapter submits approved orders to Alpaca paper (autonomously — no human approval step).
9. The ledger reconciles intent, order state, and outcomes per track.
10. Scoring, shadow portfolios, and the head-to-head leaderboard update.

## Architectural Rules

- Keep orchestration and state in Python with LangGraph; use sub-graphs per track.
- Keep providers at the edge: Alpaca (equities, options, shorts, leveraged ETFs), SEC EDGAR, market data, options chains, short interest, and news adapters must not leak into core workflow logic.
- Normalize provider responses immediately into StockRipper-owned models.
- Treat the ledger as the system of record for per-track decision intent, order attempts, and post-trade reconciliation.
- Per-track judges and per-track risk gates are deterministic about eligibility. LLM judges set objectives; only the deterministic gate decides what reaches the execution adapter.
- The execution adapter is the single code path that may call Alpaca order endpoints. LLM agents have no direct trading tools.
- No code path may submit to a non-paper Alpaca endpoint in the MVP.
- The system runs fully autonomously; do not introduce per-order human approval gates in code, prompts, or UI.
- Prefer small, testable modules over broad orchestration objects.
- Preserve source-backed reasoning, confidence, and data-quality warnings on every material recommendation, across every track.

## Data Model Principles

- Use Pydantic models for status, recommendations, multi-instrument actions (equities, options multi-leg, shorts), risk outputs, and provider DTOs.
- Keep internal models and provider DTOs separate.
- Store explicit source metadata for every evidence item.
- Keep run IDs, track IDs, timestamps, and ledger references stable and queryable.

## Testing and Replay

- Test the seams between scheduler, universe builder, research, judge, per-track risk gate, execution adapter, and ledger.
- Add replay tests that exercise multiple tracks in parallel to ensure deterministic behavior across the same inputs.
- Mock provider responses and LLM tool outputs rather than depending on live services.
- Add golden tests for high-value LLM outputs when the schema or prompt behavior changes.

## Quality and Safety Expectations

- Keep the per-window workflow auditable end to end across every track.
- Validate per-track risk gates and the universal floors before any execution path is used.
- Prefer explicit failures over silent fallbacks when data quality or provider state is uncertain.
- Keep the workspace aligned with `PROJECT_SPEC.md` and the current Python toolchain.
