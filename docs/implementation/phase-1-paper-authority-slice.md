# Phase 1 paper authority slice

Status: Implemented

Date: 2026-08-21

## Outcome

This slice adds durable `PAPER_REVIEW` Goal authority and account allocation.
It does not create an executable Session or an order.

The slice keeps `PAPER_AUTO`, `LIVE_REVIEW`, and `LIVE_AUTO` blocked.
It does not add a Broker submission method.

## Authority workflow

The application accepts only an immutable `PAPER_REVIEW` Goal version.
It verifies the exact paper Broker environment, endpoint, account, and capabilities.

The Operator must select an immutable paper account allocation limit.
The Goal capital allocation must fit inside that limit.

PostgreSQL stores the Goal activation and allocation in one transaction.
The same transaction stores these Audit events:

- `BROKER_CONNECTION_VERIFIED`
- `ACCOUNT_ALLOCATION_RESERVED`
- `GOAL_ACTIVATED`

The Audit records do not contain the full account identifier.
The allocation record keeps the exact observed Broker values for restricted review.

## Allocation behavior

PostgreSQL serializes allocation changes for one Broker connection and paper account.
Concurrent Goal activation cannot exceed the account allocation limit.

A paused Goal keeps its allocation.
This rule prevents another Goal from using reserved capital.

Goal revocation or expiry releases the allocation atomically.
The transaction also stores `ACCOUNT_ALLOCATION_RELEASED` and the Goal state event.

Database guards protect each authority binding.
They reject changes to limits, active allocation records, and Broker observations.

## Failure behavior

The service fails closed for these conditions:

- The execution mode is not `PAPER_REVIEW`.
- The Goal version or account limit is missing or invalid.
- The Broker environment is not `PAPER`.
- The Broker endpoint or account does not match.
- A required Broker capability is missing.
- The account limit binding does not match the Goal binding.
- The capital allocation exceeds the available account allocation.
- A concurrent authority conflicts with the same durable scope.

No failed preflight can store Goal authority or account allocation.

## Validation

Release compilation passed with zero warnings and zero errors.
The complete automated suite passed with isolated PostgreSQL 18 databases.

PostgreSQL integration tests are present for these behaviors:

- Atomic allocation and Goal activation
- Concurrent account allocation
- Application restart recovery
- Allocation retention while paused
- Allocation release after revocation
- Immutable database bindings
- Audit chain continuity

## Safety review

The affected execution mode is `PAPER_REVIEW`.
The slice does not change read-only or live mode behavior.

The fake Broker probe has no order operation.
No component in this slice can submit an order.

Durable locks and database guards protect allocation across concurrent processes.
Restart recovery reads the same immutable Goal activation and allocation records.

This slice does not implement idempotent order submission, reconciliation, or the kill switch.
Those controls must exist before a paper order path becomes executable.
