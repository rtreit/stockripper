# Phase 1 cancellation slice

Status: Implemented

Date: 2026-08-31

## Outcome

This slice requests cancellation for known fake Broker orders in `PAPER_REVIEW`.
It does not connect to a real paper or live Broker account.

The application supports Operator cancellation for acknowledged and partially filled Orders.
A supplied cancellation coordinator also handles Orders affected by a triggered kill switch.

## Durable request boundary

The application stores the cancellation request before the fake Broker call.
The same transaction changes the Order to `CANCEL_PENDING` and records audit evidence.

Each request has one stable idempotency key for its Order.
Restart recovery reuses a durably prepared request through the fake adapter.

A timeout moves the request to `RECONCILIATION_REQUIRED`.
A repeated Operator request does not create another external cancellation action.

## Broker truth and exposure

`CANCEL_PENDING` is not a terminal Order state.
The exposure reservation remains active until Broker truth reports a terminal state.

A `CANCELED` Broker event confirms the request and releases only the remaining exposure.
The same event moves the related Execution intent to `CANCELED`.

A Fill can win the cancellation race.
A complete Fill makes the cancellation request `SUPERSEDED` and preserves the filled Order state.

Partial Fills can arrive while cancellation remains unresolved.
The request continues to track later terminal Broker truth.

## Database controls

PostgreSQL protects immutable request identity, paper account binding, Broker identifiers, and allowed state transitions.
A deferred trigger prevents a committed `CANCEL_PENDING` Order without a cancellation request.

Broker terminal events resolve cancellation state, Order state, exposure, Execution intent state, and audit evidence atomically.
Direct account-binding changes fail closed.

## Validation

`./test.ps1` completed against isolated PostgreSQL 18 databases.
All 213 tests passed.
The PostgreSQL group contains 38 integration tests.

The tests cover these cases:

- Durable preparation before the external request
- Restart between preparation and dispatch
- Stable cancellation idempotency
- Unknown cancellation outcome
- Partial Fill before cancellation
- Complete Fill during cancellation
- Broker cancellation confirmation
- Exposure retention until terminal truth
- Kill-switch cancellation coordination
- Wrong account-binding mutation
- Audit-chain continuity

## Remaining Broker blockers

The following work remains before a real paper Broker connection:

- Reconciliation that resolves unknown cancellation outcomes
- Periodic and connection-interruption reconciliation scheduling
- Complete risk-policy controls
- Operator notification delivery and recovery interface
- Production paper adapter, credentials, deployment, and network isolation
- Another trading safety review before external paper access
