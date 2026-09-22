# Phase 0 MVP

Status: Complete

Date: 2026-08-21

## Outcome

The Phase 0 MVP supports complete `REPLAY` and `RESEARCH` workflows.
Each workflow uses immutable inputs and records durable evidence.

The MVP has no Broker order adapter. It cannot create or submit an order.
It rejects all four executable modes at the activation boundary.

## Operator workflow

The Blazor Operator interface provides three explicit stages:

1. Prepare an immutable Goal version and data snapshot.
2. Review and authorize the exact read-only authority summary.
3. Run the authorized `REPLAY` or `RESEARCH` evaluation.

The result view shows the result ID, Session ID, metrics, evidence, and digest.
The interface identifies the disabled execution state with text and color.

## Durable records

PostgreSQL 18 stores the following records:

- Goal drafts and immutable Goal versions
- Data snapshots and source observations
- Goal activations and exact authority summaries
- Session state and immutable input bindings
- Deterministic Decisions and guard results
- `REPLAY` and `RESEARCH` results
- Append-only Audit events with a verified hash chain

Database guards reject mutation of immutable records. Startup verifies the
Audit chain before the interface becomes ready.

Startup also finds interrupted read-only Sessions. It records each Session as
`FAILED`. It does not rerun Strategy after a restart.

## Strategy boundary

The host calls a separate .NET Strategy worker through versioned gRPC.
The worker listens on the loopback interface.

Both processes require the same access token. The host enforces a deadline and
a maximum response size. Deterministic guards validate every Strategy response.

The Phase 0 Strategy always returns `NO_ACTION`. Model output cannot authorize
an order or bypass an application guard.

## Local setup

Install .NET 10 and PostgreSQL 18. Create a local development database.
Keep its connection string outside the repository.

Store the host connection string with .NET user secrets:

```powershell
dotnet user-secrets set "ConnectionStrings:Stockripper" "<connection string from your secret store>" --project src/Stockripper.Host
```

Create one local Strategy token. Store the same token for both processes:

```powershell
$strategyToken = [guid]::NewGuid().ToString('N')
dotnet user-secrets set "Strategy:AccessToken" $strategyToken --project src/Stockripper.Strategy.Worker
dotnet user-secrets set "Strategy:AccessToken" $strategyToken --project src/Stockripper.Host
Remove-Variable strategyToken
```

Start the Strategy worker in one terminal:

```powershell
dotnet run --project src/Stockripper.Strategy.Worker
```

Start the Operator host in a second terminal:

```powershell
dotnet run --project src/Stockripper.Host --urls http://127.0.0.1:5080
```

Open `http://127.0.0.1:5080`. The host applies database migrations and verifies
Audit integrity during startup.

The health endpoint is `http://127.0.0.1:5080/health`. A ready response states
that execution is disabled.

## Validation

The root build checks restore, formatting, analyzers, and Release compilation.
The root test command runs all unit, architecture, and integration tests.

An end-to-end browser check completed both Operator workflows. It used a
temporary PostgreSQL 18 database and the authenticated Strategy worker.

The check confirmed the following behavior:

- Activation remained disabled until the Operator selected the confirmation.
- `REPLAY` completed with deterministic `NO_ACTION` evidence and a content digest.
- `RESEARCH` completed with `NO_ACTION` evidence and a content digest.
- The health endpoint reported `READY` and `executionEnabled: false`.
- A clean browser session produced no console warning or error.

## Safety review

Affected modes are `REPLAY` and `RESEARCH`. The MVP rejects `PAPER_REVIEW`,
`PAPER_AUTO`, `LIVE_REVIEW`, and `LIVE_AUTO` activation.

Paper and live credentials, endpoints, accounts, databases, and order paths are
absent. No automated test can reach a live trading account.

The MVP preserves immutable Goal authority, durable Session state, complete
Decision evidence, and append-only Audit continuity. It normalizes durable
timestamps to PostgreSQL microsecond precision before digest creation.

Phase 1 must add complete paper execution safety before any Broker adapter can
submit an order. Required work includes idempotency, reconciliation, allocation
enforcement, exposure reservations, and the kill switch.
