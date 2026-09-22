# Phase 1 Order preparation slice

Status: Implemented

Date: 2026-08-21

## Outcome

This slice adds restart-safe Order preparation for `PAPER_REVIEW`.
It does not connect to a real paper or live Broker account.

The application now performs final checks immediately before preparation.
It stores the evaluation, outbound request, idempotency key, and client Order identifier atomically.
The stored Order enters `SUBMITTING` before the Broker fake receives a call.

## Final evaluation

The evaluation checks these conditions:

- Active Goal authority and running Session
- Exact paper connection, endpoint, and account
- Current approval and intent expiry
- Current reconciliation
- Active exposure reservation
- Capital allocation and buying power
- Supported instrument and Order terms
- Current observation data
- Open execution schedule
- Four clear kill-switch scopes

A failed check creates no Order.
The evaluation stores all check results and its content digest.

## Durable Order preparation

Each Order stores the exact intent digest and final evaluation identifier.
It also stores the full restricted account binding and outbound request snapshot.

The client Order identifier derives from the immutable intent identifier.
The idempotency key also derives from that identifier.
Unique database constraints prevent a second Order for the same intent or identifier.

PostgreSQL stores the evaluation, Order, intent transition, and audit evidence in one serializable transaction.
Database triggers reject direct changes to immutable preparation fields.

## Broker fake and unknown outcomes

The deterministic Broker fake is available only to tests.
It has no network client, credentials, or external endpoint.

The fake supports acknowledgment, rejection, timeout before acceptance, and timeout after acceptance.
It deduplicates requests by the stable client Order identifier.

An ambiguous call changes the Order to `SUBMISSION_UNKNOWN`.
Recovery looks up the same client Order identifier.
It never sends the request again.

A found result updates the existing Order.
An unavailable result leaves the Order unknown.
A not-found result changes the Order to `RECONCILIATION_REQUIRED`.

## Audit and restart behavior

Audit records cover final evaluation, preparation, state changes, and fake Broker results.
They contain internal identifiers but no full account identifier.

The recovery query loads `SUBMITTING` and `SUBMISSION_UNKNOWN` Orders after restart.
The Order keeps its original client identifier throughout recovery.

## Validation

`./build.ps1` completed with zero warnings and zero errors.
`./test.ps1` completed against isolated PostgreSQL 18 databases.

All 190 tests passed.
The PostgreSQL group contains 32 integration tests.

The tests cover these cases:

- Stable preparation identifiers
- Failed final checks
- Timeout before acceptance
- Timeout after acceptance
- Restart restoration
- Lookup recovery without resubmission
- Repeated send after acknowledgment
- Absence of a real Broker order surface

## Remaining Broker blockers

The following work remains before a real paper Broker connection:

- Fill, partial-fill, cancel, rejection, and expiry processing
- Periodic and connection-interruption reconciliation scheduling
- Reservation adjustment from Broker events
- Operator review and recovery interface
- Production paper adapter, credentials, deployment, and network isolation
- Another trading safety review
