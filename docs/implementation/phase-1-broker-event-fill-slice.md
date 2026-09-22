# Phase 1 Broker event and Fill slice

Status: Implemented

Date: 2026-08-22

## Outcome

This slice processes fake Broker truth for `PAPER_REVIEW`.
It does not connect to a real paper or live Broker account.

The application records acknowledgments, partial Fills, complete Fills, cancellations, rejections, and expiries.
It accepts duplicate and out-of-order event delivery without Order state regression.

## Durable records

Each inbound event keeps the exact paper binding and Broker identifiers.
It also keeps Broker time, receipt time, and the source payload reference.

Each Fill keeps its Broker Fill identifier when one exists.
A unique event identifier provides a second deduplication boundary.

PostgreSQL stores the event, Fill, Order change, reservation change, and audit evidence in one transaction.
Database triggers make Broker events and Fill records append-only.

## Order and exposure behavior

Order state can advance through `ACKNOWLEDGED`, `PARTIALLY_FILLED`, and `FILLED`.
Broker truth can also set `CANCELED`, `REJECTED`, or `EXPIRED`.

Filled quantity cannot decrease or exceed Order quantity.
A conflicting Broker Order identifier fails closed.

A partial Fill reduces the open reservation by the unfilled quantity at the limit price.
A terminal Broker event releases the remaining reservation.

Broker event ingestion does not depend on a clear kill switch.
A triggered kill switch still blocks new submissions through existing final checks.

## Validation

`./test.ps1` completed against isolated PostgreSQL 18 databases.
All 197 tests passed.
The PostgreSQL group contains 33 integration tests.

The tests cover these cases:

- Exact paper account binding
- Partial Fill and complete Fill state
- Duplicate event and Fill delivery
- Out-of-order event delivery
- Reservation adjustment and terminal release
- Restart restoration
- Append-only Broker event and Fill storage
- Audit-chain continuity
- Broker-truth ingestion without a kill-switch dependency

## Remaining Broker blockers

The following work remains before a real paper Broker connection:

- Position snapshots and post-Fill policy evaluation
- Cancellation requests and cancellation-race recovery
- Periodic and connection-interruption reconciliation scheduling
- Operator review and recovery interface
- Production paper adapter, credentials, deployment, and network isolation
- Another trading safety review before external paper access
