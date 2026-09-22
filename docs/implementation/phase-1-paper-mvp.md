# Phase 1 simulated-paper MVP

Status: Implemented

Date: 2026-08-31

The [acceptance matrix](./phase-1-acceptance-matrix.md) maps the approved
Phase 1 requirements to current implementation and test evidence.

## Outcome

The MVP implements complete simulated `PAPER_REVIEW` and `PAPER_AUTO`
workflows. It also preserves the Phase 0 `REPLAY` and `RESEARCH` workflows.

The simulated Broker is production code with no network client. It uses no
Broker credentials. It cannot reach a paper or live trading account.

The local interface uses one fixed simulated Operator identity. Run it on the
documented loopback address only. Passkey authentication remains required before
live or remote deployment.

Run one Operator Host instance for this local MVP. A multi-Host execution
deployment requires a durable account leadership lease.

`LIVE_REVIEW` and `LIVE_AUTO` remain non-executable. The repository contains
no live Order path.

## Affected execution modes

The MVP changes these modes:

- `PAPER_REVIEW`
- `PAPER_AUTO`

`PAPER_REVIEW` requires an exact intent approval. `PAPER_AUTO` uses deliberate
Goal activation as its authorization boundary.

The MVP does not add routine Session confirmation to `PAPER_AUTO`.

## Operator workflow

The Blazor interface provides this workflow:

1. Prepare an immutable paper Goal and data snapshot.
2. Inspect the complete authority summary.
3. Authorize the exact paper binding and allocation.
4. Run executable Session preflight.
5. Create one deterministic Execution intent.
6. Replace the immutable intent when its terms must change.
7. Approve or reject the intent in `PAPER_REVIEW`.
8. Submit one idempotent Order to the simulation.
9. Simulate a Fill or request cancellation.
10. Record Position truth and post-Fill safety evidence.
11. Reconcile Broker truth and complete the Session.
12. Start another Session under the same valid activation when required.

The interface always shows the execution mode and `PAPER` text. It also shows
the masked account, Goal, activation, Session, kill switch, and reconciliation.

The interface reports risk rejection, uncertain submission, reconciliation
difference, Session lock, and kill-switch state with text.

The interface can pause, resume, or revoke the active Goal authority. These
actions do not claim cancellation of an existing Broker Order.

## Authority and risk

Each executable Goal binds these immutable items:

- Goal version
- Broker connection version
- `PAPER` environment
- Endpoint identity
- Expected account identifier
- Capital allocation
- Execution schedule
- Risk policy version
- Order policy version
- Reconciliation policy version

The local MVP policy permits bounded, long-only, whole-share `LIMIT DAY`
Orders for `AAPL` and `MSFT`.

The policy defines every required control. It explicitly disables the daily
loss response. Missing or different policy content blocks execution.

Risk checks enforce Order, Position, portfolio, concentration, buying power,
freshness, schedule, open Order, retry, and approval limits.

Post-Fill evaluation repeats Position, portfolio, concentration, and allocation
checks. A violation locks the Session and triggers durable safety scopes.

## Durable execution

PostgreSQL 18 stores authority, intent, review, Order, Fill, Position,
reservation, cancellation, reconciliation, and Audit records.

The database stores the idempotency key before the simulated external action.
An uncertain result never causes an automatic resubmission.

The final send gate reloads authority, allocation, policy, Broker observations,
reconciliation, and kill switches. A changed value blocks the Broker call.

The final send gate also verifies the complete Audit chain. An integrity failure
blocks the simulated Broker call.

One Host interlock orders each simulated submission against a kill-switch
trigger. A trigger cannot overtake a submission after its final gate starts.
Reconciliation differences and post-Fill responses use the same interlock.

Broker events update the Order, Fill, reservation, and Audit chain in one
serializable transaction. Duplicate and out-of-order events cannot regress
Order state or duplicate exposure.

Final reconciliation compares Orders, Fills, Positions, and reservations.
An unevaluated Fill is a reconciliation difference.

Each reconciliation stores compared Order, Fill, and Position evidence
references. It also stores the required Operator action for a difference.

A durable sequence identifies the latest reconciliation for one paper binding.
Later gates cannot replace detailed evidence with an aggregate status value.

Risk observations must match the detailed reconciliation exposure values.
The Audit chain records both reconciliation start and completion events.

Startup and periodic maintenance expire due Review approvals. The same
transaction expires the intent, releases unused exposure, and appends Audit evidence.

Revocation releases an allocation only when no reserved or filled exposure
remains. Existing exposure keeps the allocation unavailable to another Goal.

## Kill switch and recovery

Durable kill switches cover system, connection, account, and Goal scopes.
One active scope blocks new exposure.

A trigger requests cancellation for known open Orders. It does not liquidate
Positions.

Startup locks interrupted executable Sessions before recovery. It then recovers
prepared cancellations, uncertain submissions, and reconciliation targets.

Kill-switch recovery requires current reconciliation evidence. An Operator must
then clear each recovered scope.

The local Broker simulation stores its external state in host memory. A host
restart loses that simulated external state. Durable Stockripper state remains
locked until Broker truth is available and current.

## Local setup

Install these prerequisites:

- .NET SDK 10.0.204 or a compatible patch
- Docker Desktop with Linux containers
- PowerShell 7

Start PostgreSQL and configure local user secrets:

```powershell
./eng/start-local.ps1
```

The script creates `secrets/stockripper-local.env` on its first run. Git ignores
that file. The script does not print its generated secret values.

Start the Operator host:

```powershell
dotnet run --project src/Stockripper.Host --urls http://127.0.0.1:5080
```

Open `http://127.0.0.1:5080`.

Start the Strategy worker only when you use the Phase 0 workflows:

```powershell
dotnet run --project src/Stockripper.Strategy.Worker
```

Check runtime readiness at `http://127.0.0.1:5080/health`.

Stop PostgreSQL without deleting its durable volume:

```powershell
./eng/stop-local.ps1
```

## Failure behavior

The system creates no Order when any required authority or evidence is absent.
It also creates no Order when evidence is stale or different.

The system locks execution for these conditions:

- Wrong environment, endpoint, account, or Broker connection
- Missing or expired activation
- Missing or failed Session preflight
- Missing or different policy version
- Allocation, buying power, schedule, or risk violation
- Active or unreadable kill-switch state
- Unknown submission result
- Broker event conflict
- Reconciliation difference
- Missing post-Fill evaluation
- Audit integrity failure

## Validation evidence

The Release solution build passes with zero warnings and zero errors.

These direct test groups pass:

- 97 Domain tests
- 131 Application tests
- 3 Host runtime tests
- 6 architecture tests
- 2 simulated Broker adapter tests

The current managed Codex process cannot access the Docker Desktop engine pipe.
It did not run the PostgreSQL integration suite after the final changes.

Entity Framework reports no pending model change. Idempotent migration SQL
generation produces a complete script. The full integration suite must run with
`./test.ps1` from a process that can access Docker Desktop.

## Safety review

The MVP keeps paper and live execution separate. No adapter can map `PAPER` to
`LIVE` or accept an unknown execution mode.

All external actions use durable idempotency. Reconciliation treats Broker truth
as authoritative and preserves external identifiers.

The Audit chain covers material authority and execution transitions. Application
logs and broad interface views do not expose the full account identifier.

The next phase can add an Alpaca paper adapter. That change requires a new
safety review and approved paper-account end-to-end tests.
