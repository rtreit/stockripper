# Phase 0 durable Goal and audit slice

Status: Complete

Date: 2026-08-18

## Scope

This slice stores Goal drafts and immutable Goal versions in PostgreSQL 18.
It writes material Goal changes to a chained, append-only Audit record.

The slice affects all six execution modes. It adds no Broker order path and
uses no Broker credentials.

## Included behavior

- Mutable Goal drafts with optimistic revision checks
- Atomic publication of one immutable Goal version
- Deterministic Goal version content digests
- Exact storage and restoration of all six mode values
- PostgreSQL constraints for modes, states, versions, and hashes
- Unique constraints for Goal versions and proposed versions
- Audit events in the same transaction as each Goal change
- A SHA-256 Audit hash chain with canonical JSON payloads
- Database triggers that reject Audit update, delete, and truncate operations
- Database triggers that reject Goal version update, delete, and truncate operations
- Integrity verification for sequence, payload, link, event, and head values
- Application startup guard contracts that fail on invalid Audit integrity
- Entity Framework Core migrations with no stored connection string
- Isolated PostgreSQL integration tests with random runtime credentials

## Atomic behavior

Draft creation writes the draft and its Audit event in one transaction.
Draft revision writes the new revision and its Audit event in one transaction.

Publication locks the draft and verifies its expected revision. It writes the
Goal version, draft state, Audit event, and Audit head in one transaction.

A failed Audit write rolls back the related Goal change. Concurrent publication
creates one Goal version and one publication Audit event.

## Failure behavior

A missing draft fails without a state change. A stale revision fails without an
Audit event.

A published draft rejects revision and repeated publication. Invalid Goal
content fails before durable publication.

A missing Audit head rolls back publication. Audit verification reports a
sequence, payload, link, event, or head difference.

The Audit integrity guard raises an exception for any verification failure.
No execution action is available in this slice.

## Safety test coverage

Publication and Audit continuity tests cover all six modes. Each test closes
and reopens the application data source before verification.

One test restarts the PostgreSQL container before it reads the Goal version.
The test then verifies the complete Audit chain.

Negative tests cover stale revisions, concurrent publication, and a missing
Audit head. Database tests reject Audit and Goal version mutation commands.

A controlled test disables the database trigger and changes an Audit row. The
integrity verifier and guard both detect the change.

The test harness accepts only a loopback server and a dedicated test identity.
It creates and removes only validated, isolated test databases.

## Safety review

Immutable Goal versions and exact bindings survive application and database
restart. Publication cannot succeed without its Audit evidence.

Paper and live bindings remain explicit in each Goal definition. The slice
does not create credentials, Broker clients, submission commands, or endpoints.

Order idempotency, Broker acknowledgment, Fill, reconciliation, allocation
enforcement, schedule enforcement, and kill-switch behavior are not applicable.
No order lifecycle exists yet.

Signed external Audit checkpoints remain required before execution can start.
The current hash chain detects changes when a trusted head is available.

## Deferred Phase 0 work

- Signed Audit checkpoints outside the operational database
- Startup wiring for the Audit integrity guard
- Decision guard and Risk policy evaluation
- Operator authentication
- Accessibility testing for the Operator interface
- Observability instrumentation

Phase 1 Broker submission tests remain deferred. Automated tests cannot reach a
paper or live Broker endpoint.

## Commands

Run `./build.ps1` from the repository root. Run `./test.ps1` with Docker
available for the isolated PostgreSQL 18 tests.
