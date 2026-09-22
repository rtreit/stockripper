---
name: trading-safety-review
description: >-
  Review Stockripper execution changes for live and paper trading safety. Use
  for order submission, broker adapters, goal activation, authorization,
  reconciliation, risk controls, kill switches, audit records, or restart
  recovery. Do not use for research-only changes with no execution path.
---

# Trading safety review

Read `AGENTS.md` and the functional baseline before the review.

For each affected execution mode, trace this path:

1. Immutable goal version
2. Deliberate goal activation
3. Connection, account, allocation, schedule, and policy binding
4. Decision and deterministic authorization checks
5. Idempotent order submission
6. Broker acknowledgement and fill processing
7. Reconciliation and final position
8. Audit evidence, kill switch, and restart recovery

Report only plausible failures. Give each finding a concrete counterexample,
affected mode, broken invariant, severity, and smallest safe correction.

Treat these outcomes as critical:

- Wrong-account execution
- Paper and live endpoint crossing
- Allocation overrun
- Duplicate order
- Order without durable audit evidence
- Kill switch that does not stop new execution
