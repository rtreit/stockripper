# Phase 1 execution safety slice

Status: Implemented

Date: 2026-08-21

## Outcome

This slice adds durable safety state before any paper Broker submission path.
It affects only `PAPER_REVIEW`.

The slice adds these controls:

- System, connection, account, and Goal kill-switch scopes
- Durable reconciliation records for exact paper authority
- Shared account exposure reservations
- Restart-safe recovery and database tamper protection

The later Order preparation slice adds a test-only Broker fake operation.
The repository still has no real Broker connection.

## Kill-switch behavior

Each applicable scope must exist and have `CLEAR` state.
Missing or unreadable state blocks Session creation, intents, approvals, and reservations.

A trigger changes durable state to `TRIGGERED`.
Application restart does not clear this state.

Recovery needs a `CURRENT` reconciliation completed after the trigger.
This evidence changes the state to `RECOVERY_REQUIRED`.
An Operator action can then change the state to `CLEAR`.

The database rejects invalid transitions, deletion, truncation, and scope changes.
Audit records identify the scope type without storing the full account identifier.

## Reconciliation behavior

Each record binds the Goal version, connection version, environment, endpoint, account, and reconciliation policy.
It stores observed exposure, buying power, differences, corrections, times, and a content digest.

`CURRENT` means that the observation succeeded and has no differences.
`DIFFERENCE_LOCKED` blocks execution because one or more differences exist.
`FAILED` blocks execution because the observation did not complete.

Passed preflight, accepted Risk, and approved Review records bind exact reconciliation evidence.
The database keeps reconciliation records append-only.

## Exposure reservation behavior

Approval calculates maximum exposure from intent quantity and limit price.
It reserves this amount before the intent can become `APPROVED`.

The approval transaction locks the shared allocation row.
It includes reconciled positions, open orders, and active reservations in the capacity check.
The transaction fails when total exposure would exceed the Goal allocation.

The reservation keeps the exact paper connection, endpoint, account, Goal, allocation, intent, currency, and content digest.
Release state exists for later Broker-event and reconciliation workflows.
No automatic release workflow exists in this slice.

## Failure behavior

The application fails closed for these conditions:

- One required kill-switch scope is missing or unavailable.
- One applicable kill switch is not `CLEAR`.
- Reconciliation is failed, different, stale, or bound to other authority.
- The current account observation differs from the Goal binding.
- A reservation differs from the intent or allocation.
- Concurrent reservations exceed the shared allocation.
- Stored safety evidence fails its content digest.

These failures create no Broker request.

## Validation

`./build.ps1` completed with zero warnings and zero errors.
`./test.ps1` completed against isolated PostgreSQL 18 databases.

The complete suite contains 182 tests.
The PostgreSQL group contains 31 integration tests.

The tests cover these safety cases:

- Missing durable scope
- Triggered state after restart
- Reconciliation before recovery and clear
- Wrong endpoint and account
- Direct reconciliation and reservation tampering
- Concurrent approvals for one shared allocation
- Audit-chain continuity
- Absence of a Broker order surface

No automated test uses a paper or live Broker account.

## Remaining submission blockers

The Order preparation slice completed final evaluation, durable request state,
idempotency, and unknown-result recovery with a Broker fake.

The following controls remain required before real paper submission:

- Production Alpaca paper adapter and isolated credentials
- Fill, cancel, rejection, and expiry processing
- Periodic and connection-interruption reconciliation scheduling
- Reservation adjustment from Broker events
- Operator interface for review and recovery

Another trading safety review is required before Broker integration.
