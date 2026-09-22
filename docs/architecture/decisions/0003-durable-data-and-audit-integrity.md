# ADR-0003: Durable data and audit integrity

Status: Accepted

Date: 2026-08-18

## Context

Authority, allocation, idempotency, orders, fills, reconciliation, and the kill
switch must survive a restart. Audit storage must detect event deletion,
replacement, and reordering.

## Decision

Use PostgreSQL 18 on the current supported minor release. Use Npgsql and Entity
Framework Core for schema migrations and ordinary persistence.

Use explicit PostgreSQL transactions for execution-critical changes. Store an
Order in `SUBMITTING` and reserve its idempotency key before the Broker call.
Use unique constraints and row locks for allocation and submission decisions.

Store money and quantity in bounded `numeric` columns. Store timestamps in UTC
with time zone. Preserve Broker timestamps and receipt timestamps separately.

Write each material state change to an append-only audit table in the same
transaction as its domain change. The application database role has no update
or delete permission on audit rows.

Create a SHA-256 chain from canonical event bytes, the event sequence, and the
previous hash. A separate checkpoint tool signs the current chain head with an
Ed25519 key. Store signed checkpoints outside the operational database.

Verify the chain and the latest trusted checkpoint during startup. Lock
execution when verification fails. Back up the database and signed checkpoints
as one recovery set.

## Safety impact

Atomic writes keep authority and audit evidence together. Unique constraints
and durable preparation reduce duplicate orders after timeouts or restarts.
External checkpoints make a rewritten audit suffix detectable.

## Rejected options

- SQLite was rejected for deployed execution. Shared allocation and concurrent
  reconciliation need stronger server-side locking and operations support.
- An event broker as the source of truth was rejected. It adds another durable
  system before the project needs independent services.
- Application logs were rejected as the audit record. They cannot guarantee an
  atomic link to execution state.
- A database-only hash chain was rejected. A privileged rewrite could replace
  the chain and its head.

## Migration constraints

Migrations must preserve stable identifiers, event sequence, canonical event
bytes, hashes, and signatures. A migration that changes canonicalization must
start a documented chain epoch linked to the prior signed checkpoint.

Database restore tests must include audit verification, leadership acquisition,
idempotency state, reconciliation state, and kill-switch state.

## References

- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
