# Phase 0 Replay snapshot slice

Status: Complete

Date: 2026-08-20

## Scope

This slice adds immutable, versioned data snapshots and deterministic Replay
result evidence. It affects `REPLAY` directly.

The Replay service rejects all five other modes. It creates no Execution intent
and has no Broker dependency.

## Included behavior

- Immutable data snapshot identifiers and content digests
- Separate payload and complete snapshot SHA-256 digests
- Data set, data policy, schema, source, range, and observation metadata
- Injected snapshot capture and Replay times
- Active Goal activation validation before Strategy evaluation
- Exact `REPLAY` mode and Goal digest validation
- Exact data-policy and Strategy-response version validation
- Decision schedule enforcement with local time-zone conversion
- Deterministic output metrics and evidence for one input and seed
- Stable Replay output digests that exclude run identity and wall-clock values
- PostgreSQL storage for snapshots and completed Replay results
- Atomic Audit events for snapshot creation and Replay completion
- Database triggers that reject snapshot and Replay result mutation
- Additive Strategy response fields for contract and artifact identity

## Acceptance criteria

The same Goal version, snapshot, Strategy version, seed, metrics, and evidence
must produce the same output digest.

A different seed must produce a different output digest. An application restart
must not change the stored snapshot or Replay evidence.

The service must reject a non-`REPLAY` Goal before Strategy evaluation. It must
also reject expired activation, closed schedule, stale digest, and version mismatch.

A failed Audit write must roll back the completed Replay result. Snapshot and
Replay result rows must reject update, delete, and truncate commands.

## Failure behavior

A missing Goal version or snapshot stops Replay. An invalid Goal or snapshot
digest stops Replay.

A data-policy or Strategy-version mismatch stops Replay. A closed decision
schedule stops Replay.

An inactive, paused, revoked, or expired activation cannot start Replay. Invalid
Strategy output creates no completed Replay result.

## Test coverage

Domain tests cover snapshot digests, payload changes, time ranges, source
identity, Replay seed behavior, and schedule boundaries.

Application tests cover deterministic output, all five rejected modes, data
policy mismatch, Strategy mismatch, malformed output, activation expiry, and
schedule closure.

PostgreSQL tests cover application restart, database restart, Audit rollback,
record immutability, privileged snapshot tamper, and Audit continuity.

The complete test suite passed on 2026-08-21. It included 115 tests and 18
PostgreSQL 18 integration tests.

## Safety review

The Replay path accepts no execution binding, credential, Broker client, order
command, or Execution intent.

The Strategy runtime receives immutable identifiers, digests, versions, and a
seed. The application checks the returned Strategy version before persistence.

Order idempotency, Fill, reconciliation, allocation, and kill-switch behavior
are not applicable. This slice has no external trading action.

## Deferred Phase 0 work

- Strategy gRPC client wiring and response size limits
- Complete Decision records and deterministic Decision guards
- `RESEARCH` workflow
- Risk policy model and evaluation
- Signed Audit checkpoints outside the operational database
- Startup wiring for the Audit integrity guard
- Operator authentication
- Accessibility testing for the Operator interface
- Observability instrumentation

Phase 1 Broker submission tests remain deferred. Automated tests cannot reach a
paper or live Broker endpoint.
