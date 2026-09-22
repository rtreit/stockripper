# Phase 1 startup recovery slice

Status: Implemented

Date: 2026-08-31

## Outcome

The Host now runs paper execution recovery during each startup.
The affected mode is `PAPER_REVIEW`.

Recovery locks interrupted executable Sessions before it checks Broker work.
It then processes prepared cancellation requests before uncertain Order submissions.

## Fail-closed Broker boundary

The Host uses an unavailable paper Broker adapter until a production adapter is configured.
The adapter cannot submit an Order.

An interrupted `SUBMITTING` Order changes to `SUBMISSION_UNKNOWN` when Broker lookup is unavailable.
A prepared cancellation changes to `RECONCILIATION_REQUIRED` when Broker access is unavailable.

Startup fails if a required durable transition fails.
Startup never unlocks an executable Session.

## Post-Fill cancellation

A post-Fill policy violation already locks the Session and triggers the required kill switches.
The application now passes each triggered switch to the cancellation coordinator.

The coordinator uses the durable cancellation idempotency key for each Order.
Repeated scope processing cannot create another external cancellation action.

## Recovery order

The startup order is:

1. Apply database migrations.
2. Verify audit integrity.
3. recover interrupted read-only Sessions.
4. lock interrupted executable Sessions.
5. recover prepared cancellation requests.
6. recover uncertain Order submissions.
7. mark the Host ready with execution disabled.

## Validation

`./build.ps1` completed with no warnings or errors.
`./test.ps1` completed against isolated PostgreSQL 18 databases.

All 214 tests passed.
The PostgreSQL group contains 38 integration tests.

The tests verify startup recovery order and post-Fill cancellation coordination.

## Remaining Broker blockers

The following work remains before a real paper Broker connection:

- Reconciliation that compares complete Broker and local truth
- Resolution of unknown cancellation outcomes
- Periodic and connection-interruption reconciliation scheduling
- Complete risk-policy controls
- Operator notification delivery and recovery interface
- Production paper adapter, credentials, deployment, and network isolation
- Another trading safety review before external paper access
