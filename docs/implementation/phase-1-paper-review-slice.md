# Phase 1 paper review slice

Status: Implemented

Date: 2026-08-21

## Outcome

This slice adds the durable `PAPER_REVIEW` workflow through Operator approval.
It does not add a Broker submission operation.

The workflow now supports these boundaries:

1. Start an executable Session with one immutable data snapshot.
2. Verify exact paper authority during Session preflight.
3. Lock interrupted executable Sessions after application restart.
4. Evaluate bounded order terms with deterministic risk checks.
5. Store one immutable Execution intent for one external order action.
6. Approve or reject the exact intent digest.
7. Store durable kill-switch and reconciliation state.
8. Reserve shared account exposure during approval.

`PAPER_AUTO`, `LIVE_REVIEW`, and `LIVE_AUTO` remain blocked.

## Executable Session preflight

The Session stores the observed Broker environment, endpoint, and account.
The preflight record stores the complete deterministic check set.
It binds passed evidence to one immutable reconciliation record.

The checks cover these controls:

- Active Goal authority
- Goal version digest
- Execution schedule
- Input snapshot and data sources
- Bound policy versions
- Kill-switch observation
- Broker environment, endpoint, account, capabilities, and clock
- Market session state
- Reconciliation status
- Capital data and buying power
- Durable account allocation

PostgreSQL permits `PREFLIGHT` to `READY` only with matching passed evidence.
A failed check stores immutable failed evidence and moves the Session to `FAILED`.

Application restart moves each interrupted executable Session to `LOCKED`.
The Session must return through preflight before later execution work.

## Risk and intent behavior

This slice supports bounded paper equity limit orders with `DAY` time in force.
It calculates maximum order exposure from quantity and limit price.

The deterministic risk record includes current position exposure and open-order exposure.
It also includes buying power, allocation, policy, schedule, reconciliation, and kill-switch results.

Accepted risk moves the intent to `REVIEW_PENDING`.
Rejected risk moves the intent to `RISK_REJECTED`.

The intent digest excludes lifecycle state.
The digest always identifies the same immutable order terms.

The idempotency key derives from the immutable intent identifier.
PostgreSQL enforces uniqueness for the intent and idempotency key.

## Review behavior

The Operator action includes the exact intent identifier and digest.
An approval cannot apply to a changed or different intent.

Approval reloads Goal authority, Session state, allocation, and preflight evidence.
It records new reconciliation evidence for the exact paper account.
It also reloads all applicable durable kill switches.

Approval reserves the intent maximum exposure in the same database transaction.
The database locks the shared allocation before it calculates remaining capacity.
Concurrent Sessions cannot reserve more than the shared Goal allocation.

PostgreSQL stores the Review approval before it changes the intent state.
The database permits only the matching `APPROVED` or `REJECTED` transition.

An Operator edit must create a new intent and Risk evaluation.
An expired intent cannot receive approval.

## Durable records and Audit events

PostgreSQL stores these new records:

- Executable Session preflight evidence
- Risk evaluation
- Execution intent
- Review approval
- Kill-switch state for system, connection, account, and Goal scopes
- Reconciliation record
- Exposure reservation

Database triggers reject deletion and immutable-field changes.
They also reject an intent state change without exact Review evidence.

The Audit chain includes these new event types:

- `SESSION_PREFLIGHT_COMPLETED`
- `RISK_EVALUATED`
- `EXECUTION_INTENT_CREATED`
- `REVIEW_RECORDED`
- `EXECUTION_INTENT_STATE_CHANGED`
- `KILL_SWITCH_CONFIGURED`
- `KILL_SWITCH_STATE_CHANGED`
- `RECONCILIATION_COMPLETED`
- `EXPOSURE_RESERVED`
- `EXPOSURE_RELEASED`

Audit records contain stable identifiers and digests.
They do not contain the full Broker account identifier.

## Failure behavior

The application fails closed for these conditions:

- The mode is not `PAPER_REVIEW`.
- Goal authority is missing, stale, expired, or different.
- Session preflight evidence is missing or failed.
- Broker environment, endpoint, or account differs from the Goal binding.
- Account allocation is missing, released, or different.
- Risk input is unavailable or stale.
- Worst-case exposure exceeds Goal allocation.
- Buying power does not cover maximum order exposure.
- An applicable kill switch is missing, unavailable, or active.
- Reconciliation is not current.
- A reconciliation binding differs from the exact paper authority.
- Concurrent reservations would exceed the Goal allocation.
- Review uses a stale digest.
- Intent or approval time has expired.

No failure can create a Broker request.
This repository still has no Broker order operation.

## Current boundary

The application services use probe interfaces for current Broker evidence.
The host does not register a production risk probe or review workflow.

The repository does not yet include these required controls:

- Alpaca paper Broker adapter
- Final pre-submit evaluation
- Broker submission and response records
- Order and Fill state machines
- Periodic and connection-interruption reconciliation scheduling
- Automatic reservation expiry and Broker-event adjustment
- Operator interface for paper Session and review actions

These controls must exist before Stockripper can submit a paper order.

## Validation

`./build.ps1` completed with zero warnings and zero errors.
`./test.ps1` completed against isolated PostgreSQL 18 databases.

The automated checks cover these behaviors:

- Wrong paper account and endpoint
- Missing policy and active kill switch
- Failed preflight durability
- Restart locking
- Allocation and buying-power boundaries
- Risk rejection
- Exact digest approval
- Kill-switch change before approval
- Missing durable kill-switch scope
- Kill-switch restart, reconciliation, recovery, and clear
- Reconciliation and reservation tamper rejection
- Concurrent shared-allocation reservation
- Direct database tampering
- Application restart after approval
- Audit chain continuity
