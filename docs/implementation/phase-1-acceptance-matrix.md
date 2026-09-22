# Phase 1 simulated-paper acceptance matrix

Status: Implementation complete, final database validation pending

Date: 2026-08-31

## Scope

This matrix covers the approved Phase 1 behavior for `PAPER_REVIEW` and
`PAPER_AUTO`. It does not authorize a real Broker or live execution path.

The applicable software failures are wrong-account execution, allocation
excess, endpoint crossing, duplicate submission, and missing Audit evidence.

## Functional evidence

| Requirement | Current evidence | Result |
| --- | --- | --- |
| Immutable paper authority | Goal, activation, Session, allocation, and policy records use exact version bindings. | Implemented |
| Paper account handshake | Preflight rejects a wrong environment, endpoint, account, or capability. | Implemented |
| `PAPER_REVIEW` | Review binds the Operator action to one immutable intent digest. | Implemented |
| `PAPER_AUTO` | Activation permits submission without routine Session confirmation. | Implemented |
| Repeated Sessions | One valid activation can start later Sessions after prior Session completion. | Implemented |
| Goal controls | The Operator can pause, resume, or revoke the current activation. | Implemented |
| Deterministic risk | Intent, final, and post-Fill checks use one exact Risk policy. | Implemented |
| Exposure reservation | PostgreSQL reserves exposure before Order preparation. | Implemented |
| Review expiry | Startup and periodic maintenance expire due approvals, release unused exposure, and append Audit evidence. | Implemented |
| Final submission gate | The gate reloads authority, allocation, Broker truth, policy, schedule, reconciliation, and kill switches. | Implemented |
| Final Audit gate | The gate verifies Audit integrity before the simulated Broker boundary. | Implemented |
| Submission interlock | Submission and kill-switch trigger ordering share one Host interlock. | Implemented |
| Durable idempotency | PostgreSQL stores the client Order identifier before the Broker call. | Implemented |
| Unknown outcome recovery | Recovery uses Broker lookup and never sends a blind retry. | Implemented |
| Broker event processing | Duplicate and out-of-order events cannot duplicate Fill exposure or regress Order state. | Implemented |
| Cancellation | Exposure remains reserved until terminal Broker truth. | Implemented |
| Reconciliation | Comparison covers Orders, Fills, Positions, reservations, and external Orders. Each record identifies the compared evidence. | Implemented |
| Restart recovery | Startup locks executable Sessions before submission, cancellation, and reconciliation recovery. | Implemented |
| Kill switch | System, connection, account, and Goal scopes block each new exposure boundary. | Implemented |
| Post-Fill response | A policy violation locks execution, requests reconciliation, and alerts the Operator. | Implemented |
| Audit integrity | Append-only Audit records use sequence and hash-chain verification. | Implemented |
| Operator interface | The interface shows paper authority, immutable review data, controls, alerts, and persistent status. | Implemented |
| Runtime health | Reconciliation differences and unavailable truth produce a degraded status. | Implemented |
| Paper and live separation | Production code contains no network Broker client or live Order service. | Implemented |

## Acceptance test evidence

The direct tests cover these cases:

- All six exact mode values, inheritance, and unknown mode rejection
- Wrong paper endpoint, account, environment, and capability
- Missing binding, policy, allocation, reconciliation, and kill-switch state
- Allocation boundary, excess, and concurrent allocation conflicts
- Approval digest, rejection, expiry, and modification identity
- Durable Review expiry, repeated expiry safety, and unused reservation release
- Activation pause, resume, revocation, and later Session creation
- Final schedule, activation, allocation, policy, exposure, endpoint, and account changes
- Duplicate submission, Broker rejection, and unknown outcomes
- Partial Fill, cancellation race, and out-of-order Broker events
- Detailed reconciliation, external Orders, and unevaluated Fills
- Latest-record ordering, compared evidence references, and required Operator action
- Reconciliation start, result, and correction counts in the Audit chain
- Startup recovery, cancellation recovery, and idempotent submission recovery
- Kill-switch persistence, recovery evidence, cancellation, and submission blocking
- Complete `PAPER_AUTO` execution without review
- Audit continuity, tamper detection, and final submission blocking
- Runtime health degradation after startup or periodic reconciliation differences

The current direct totals are:

- 97 Domain tests
- 131 Application tests
- 3 Host runtime tests
- 6 architecture tests
- 2 simulated Broker adapter tests

## Pending validation

The PostgreSQL integration suite exists and includes the complete simulated
`PAPER_AUTO` lifecycle. It also includes restart and concurrent transaction tests.

The managed Codex process cannot open the Docker Desktop engine pipe. The final
integration run requires `./test.ps1` from a process with Docker engine access.

This pending run prevents a claim of fully validated Phase 1 acceptance.
