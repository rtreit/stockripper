# Phase 1 reconciliation lock slice

Status: Implemented

Date: 2026-08-31

## Outcome

This slice makes the latest reconciliation result authoritative for `PAPER_REVIEW`.
A newer non-current result invalidates all older `CURRENT` evidence for the same binding.

The comparison uses the exact Goal version, Broker connection version, environment, endpoint, and account.
An equal completion time fails closed when one result is not current.

## Atomic safety response

PostgreSQL stores a non-current reconciliation and applies its safety response in one transaction.
The transaction locks active executable Sessions for the Goal.

The transaction also triggers the account and Goal kill switches.
It returns a `RECOVERY_REQUIRED` switch to `TRIGGERED` when new non-current evidence arrives.

The transaction records reconciliation, Session, and kill-switch audit events.
A transaction failure preserves the previous durable state.

Preflight Sessions remain under the preflight workflow.
The triggered Goal switch makes the preflight fail and stores its failure evidence.

## Validation

The regression test first reproduced stale `CURRENT` evidence reuse.
The corrected test verifies a same-time non-current result, Session lock, safety switches, and audit continuity.

`./test.ps1` completed against isolated PostgreSQL 18 databases before the final documentation update.
All 215 tests passed.
The PostgreSQL group contains 39 integration tests.

## Remaining reconciliation work

The following work remains:

- Compare complete local and Broker Order sets.
- Compare complete local and Broker Fill sets.
- Compare calculated and Broker Positions.
- Compare open exposure reservations.
- Record external Broker Orders without claiming ownership.
- Resolve unknown cancellation outcomes from Broker terminal truth.
- Schedule reconciliation after interruption, kill-switch activation, and periodic deadlines.
