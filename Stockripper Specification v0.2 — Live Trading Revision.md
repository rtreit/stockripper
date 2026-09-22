# Stockripper Specification v0.2 — Live Trading Revision

Status: Revision record

The current functional baseline is
[Stockripper functional specification](./docs/functional-specification.md).

The specification now treats **paper trading and live trading as equal, first-class execution paths**.

The active goal explicitly selects one of six modes:

- `REPLAY`
- `RESEARCH`
- `PAPER_REVIEW`
- `PAPER_AUTO`
- `LIVE_REVIEW`
- `LIVE_AUTO`

For executable goals, the immutable goal version also binds Stockripper to the exact broker connection, broker account, capital allocation, schedule, and risk-policy version.

`LIVE_AUTO` is fully supported. Once the Operator deliberately activates a `LIVE_AUTO` goal, goal activation becomes the authorization boundary and subsequent valid sessions may execute without per-session confirmation.

The specification deliberately distinguishes **accepted market risk** from **unacceptable software risk**:

> A losing live trade may be a valid experimental outcome. Trading the wrong account, exceeding the authorized capital allocation, crossing paper and live endpoints, duplicating an order, or producing an unauditable trade is a software failure.

The implementation plan now includes a dedicated **Phase 2 — Live Fire**, with acceptance criteria for `LIVE_REVIEW`, `LIVE_AUTO`, actual-fill reconciliation, live-account binding, kill-switch behavior, restart recovery, and end-to-end traceability.
