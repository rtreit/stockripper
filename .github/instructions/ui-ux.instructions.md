# UI and Dashboard UX Instructions

## Purpose

These rules govern the StockRipper UX surfaces — the multi-track research dashboard, daily/intraday reports, and terminal output. The UI is observational: it shows what the autonomous system is doing across every strategy track, not an approval queue.

## Interaction Rules

- Make the most important information visible first: the head-to-head **track leaderboard**, per-track equity and exposure, the latest judge decisions, risk-gate verdicts, and operational health.
- Keep dashboards readable and low-churn during live windows, even when multiple tracks are firing intraday orders.
- Surface evidence, confidence, and data-quality warnings directly instead of hiding them in side panels.
- The kill switch and per-track pause/resume are emergency controls, not order approvers. Make them prominent but distinct from routine navigation.
- Never introduce per-order approval UI. The system executes autonomously after the per-track risk gate; UI that suggests otherwise is a bug.
- Make it impossible to confuse paper-trading state with live execution.

## Workflow Focus

- Track leaderboard (cumulative return, Sharpe, Calmar, drawdown, win rate)
- Per-track decision windows and intraday triggers
- Per-agent calibration and contribution within each track
- Evidence quality and source transparency
- Risk-gate verdicts (universal floor vs per-track policy)
- Order and reconciliation status per track
- Backtest, shadow, ablation, and aggression-sweep comparisons

## UX Expectations

- Use clear labels for track, agent, judge, risk, and execution states.
- Show source citations and retrieval timestamps wherever a claim is rendered.
- Keep tables, charts, and status badges legible across normal and narrow layouts.
- Ensure terminal output is actionable and not just verbose.
- The UI must never have a control that can bypass the deterministic per-track risk gate, the execution adapter, or the universal floors.
