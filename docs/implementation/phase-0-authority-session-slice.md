# Phase 0 authority and Session slice

Status: Complete

Date: 2026-08-21

## Scope

This slice stores read-only Goal activation and Session authority in PostgreSQL 18.
It connects `REPLAY` runs to durable Session records.

`REPLAY` uses the complete workflow. `RESEARCH` activation is available for a
future workflow. Phase 0 rejects all four executable modes during activation.

## Included behavior

- Durable Goal activation evidence, state, expiry, revocation, and revision
- One active or paused Goal version for each Goal
- Durable Session state, immutable inputs, and optimistic revision checks
- Exact Goal version, activation, mode, data snapshot, and Replay seed bindings
- Database rechecks of active authority during Session insertion
- Database locks that order Session creation against activation state changes
- Atomic Session creation and state-change Audit events
- Atomic REPLAY result and Session completion
- Failed Session evidence after Strategy or response validation failure
- Restart recovery that changes interrupted REPLAY Sessions to `FAILED`
- Database guards for authority bindings, transitions, deletion, and truncation
- Durable Audit continuity across application restart

## Acceptance criteria

Concurrent activation must create only one active authority for a Goal. A
paused, revoked, expired, stale, or mismatched activation cannot start a Session.

REPLAY must load authority from PostgreSQL. It cannot trust an activation object
from the caller.

REPLAY completion must store the result and terminal Session state in one
transaction. A restart must retain both records and their Audit evidence.

Recovery must find each nonterminal REPLAY Session. It must record a terminal
failure without calling Strategy or any Broker.

## Failure behavior

An unknown Goal version or activation stops the operation. An invalid Goal
digest stops activation or REPLAY.

A stale authority or Session revision fails without a state change. A database
binding difference stops Session insertion.

An interrupted REPLAY Session does not resume automatically. Recovery records a
failure, and a later operation can start a new Session.

Phase 0 rejects `PAPER_REVIEW`, `PAPER_AUTO`, `LIVE_REVIEW`, and `LIVE_AUTO`
activation. These modes need complete executable preflight controls first.

## Test coverage

Application tests cover both read-only modes and all four blocked executable
modes. They also cover stale authority and REPLAY restart recovery.

PostgreSQL tests cover concurrent activation, application restart, authority
binding guards, state transitions, Audit continuity, and immutable records.

The complete suite passed on 2026-08-21. It included 126 tests and 21
PostgreSQL 18 integration tests.

## Safety review

The authority path starts with an immutable Goal version. Session insertion
locks and rechecks the exact activation before it commits.

REPLAY can create no Execution intent, Broker request, order, Fill, or position.
Recovery can only change an interrupted read-only Session to `FAILED`.

Paper and live activation remains unavailable. This prevents partial Phase 0
controls from becoming executable authority.

Order idempotency, reconciliation, allocation, and kill-switch behavior remain
deferred. No external trading action exists in this slice.

## Deferred Phase 0 work

- Complete `RESEARCH` workflow
- Strategy gRPC client wiring and response size limits
- Complete Decision records and deterministic Decision guards
- Risk policy model and evaluation
- Signed Audit checkpoints outside the operational database
- Startup wiring for the Audit integrity guard and Session recovery
- Operator authentication and activation authority summary interface
- Accessibility testing for the Operator interface
- Observability instrumentation

Phase 1 Broker submission tests remain deferred. Automated tests cannot reach a
paper or live Broker endpoint.
