# Phase 1 post-Fill safety slice

Status: Implemented

Date: 2026-08-31

## Outcome

This slice evaluates fake Broker Position truth after each Fill in `PAPER_REVIEW`.
It does not connect to a real paper or live Broker account.

The application records one complete Position snapshot batch for each Fill.
It compares Position exposure and open Order exposure with the authorized account allocation.

This slice enforces the hard allocation boundary.
It does not implement all controls in the referenced risk policy.

## Durable records

Each Position batch keeps the exact paper connection, endpoint, account, Broker time, receipt time, and source payload reference.
Each Position record keeps the Broker instrument, whole-unit quantity, average entry price, market value, and currency.

PostgreSQL stores the batch, Positions, evaluation, safety response, Session lock, kill switches, and audit evidence atomically.
Database triggers make each new record append-only.

The database rejects an incomplete Position batch at transaction completion.
It also rejects a Position or evaluation that does not match the exact Fill authority.

## Fail-closed behavior

A current Position must include at least the filled quantity for the Fill instrument.
Position and open Order exposure must not exceed the bound allocation.

Any failed condition creates a `VIOLATION_LOCKED` result.
The same transaction locks the Session and triggers configured account and Goal kill switches.

The transaction also creates a reconciliation request and a pending Operator notification.
This slice does not deliver the notification or complete the reconciliation request.

An unevaluated Fill blocks a later passing pre-submit evaluation for the same paper account.
Duplicate Position delivery returns the stored result without a second state or audit effect.

## Validation

`./test.ps1` completed against isolated PostgreSQL 18 databases.
All 205 tests passed.
The PostgreSQL group contains 35 integration tests.

The tests cover these cases:

- Complete Position observations
- Exact paper account binding
- Post-Fill allocation violations
- Missing current Position evidence
- Session lock and kill-switch response
- Duplicate delivery after restart
- Append-only Position and evaluation storage
- Audit-chain continuity

## Remaining Broker blockers

The following work remains before a real paper Broker connection:

- Cancellation requests and cancellation-race recovery
- A worker that completes requested reconciliation
- Operator notification delivery and recovery interface
- Periodic and connection-interruption reconciliation scheduling
- Complete risk-policy controls
- Production paper adapter, credentials, deployment, and network isolation
- Another trading safety review before external paper access
