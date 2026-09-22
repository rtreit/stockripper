# Architecture safety review

Status: Phase 1 simulated-paper review complete

Date: 2026-08-18

Scope: Architecture baseline before Phase 0 implementation

## Affected modes

The decisions affect all six modes. Phase 0 implements `REPLAY` and `RESEARCH`
behavior first. It models every mode value but has no real Broker order path.

Phase 1 adds `PAPER_REVIEW` and `PAPER_AUTO`. Phase 2 can add `LIVE_REVIEW` and
`LIVE_AUTO` only after the required paper, recovery, and approval gates pass.

## Safety trace

| Trace step | Architecture control | Failure behavior |
| --- | --- | --- |
| Immutable Goal version | PostgreSQL stores immutable versions and content digests. | A missing or invalid digest locks execution. |
| Deliberate activation | The Blazor interface shows the authority summary and records Operator evidence. | Missing evidence prevents activation. |
| Exact binding | A version binds endpoint, environment, account, allocation, schedule, and policies. | Any missing, stale, or different binding stops preflight. |
| Deterministic authorization | The application host owns Decision guards, Risk checks, and approval checks. | Invalid Strategy output or failed checks create no executable Intent. |
| Idempotent submission | A transaction stores `SUBMITTING`, reserves exposure, and claims a unique idempotency key before the Broker call. | An unknown result locks the Order for reconciliation. |
| Broker result | The adapter records acknowledgments and stream events without changing their external identifiers. | Invalid or inconsistent input creates a reconciliation difference. |
| Reconciliation | REST history and positions are authoritative after stream gaps, timeout, or restart. | An unresolved difference blocks new execution. |
| Audit and recovery | Domain changes and chained audit events share one transaction. Signed checkpoints anchor the chain. | Audit, leadership, database, or kill-switch uncertainty locks execution. |

## Paper and live separation

Paper and live execution use separate deployments, databases, networks, secret
roots, service identities, hostnames, and Broker endpoints. One process cannot
load both credential sets.

The Goal version records the expected environment and endpoint identity. The
Session records the observed endpoint and account. A mismatch stops preflight
and creates an audit event.

## Restart and duplicate-order review

The application stores submission preparation before an external call. A
stable client order identifier derives from the immutable Intent identity.

After an uncertain result or restart, the application queries Broker state
before it can retry. The application does not use absence from a stream as
proof that the Broker rejected an order.

Only one execution leader can operate one Broker account. Loss of the durable
leadership lease blocks new submissions and starts reconciliation.

## Kill-switch review

The kill-switch state is durable and loads before execution leadership. A
trigger stops new Intents and starts the policy-defined cancellation workflow.

Restart cannot clear the switch. Recovery requires completed reconciliation
and an authenticated Operator action.

## Review outcome

No blocking architecture finding remains. The baseline contains no real order
path. Phase 0 must prove deterministic Replay, audit integrity, mode validation,
and restart-safe state with the Broker fake.

The next safety review occurs before Phase 1 connects to Alpaca paper trading.

## Phase 1 paper review update

Date: 2026-08-21

Affected mode: `PAPER_REVIEW`

The application now stores executable Session preflight, Risk evaluation,
Execution intent, Review approval, reconciliation, and exposure reservation records.

The database enforces exact paper account bindings and immutable order terms.
It also requires exact Review evidence for an approved intent state.

Application restart moves interrupted executable Sessions to `LOCKED`.
The Session must complete a new preflight before later execution work.

Four durable kill-switch scopes control paper execution.
Missing or unreadable scope state blocks Sessions, intents, approvals, and reservations.
Triggered state survives restart.
Recovery needs a current reconciliation after the trigger.
An Operator action then clears the recovered scope.

Each passed preflight and accepted Risk evaluation binds exact reconciliation evidence.
Each approval binds a newer reconciliation and one durable exposure reservation.
PostgreSQL locks shared allocation state before it accepts the reservation.

No Broker adapter exposes an order operation.
No automated test can reach a live or paper Broker account.

The following controls remain blocking requirements for Broker submission:

- Final pre-submit evaluation
- Durable Broker request state and idempotency claim
- Unknown-result recovery
- Order, Fill, and reconciliation state machines
- Periodic and connection-interruption reconciliation scheduling
- Automatic reservation adjustment from Broker events
- Production paper credentials and network separation

The safety review found no external trading action in the current slice.
Alpaca paper integration requires another safety review before implementation.

## Phase 1 Order preparation update

Date: 2026-08-21

Affected mode: `PAPER_REVIEW`

The application now performs the final deterministic checks before Order preparation.
It atomically stores the evaluation, outbound request, stable client identifier, idempotency key, and `SUBMITTING` state.

Only the test project contains a Broker fake implementation.
Production code contains no network client, Broker credentials, or real submission adapter.

Timeout and restart recovery use the original client Order identifier.
Recovery does not send an uncertain request again.
A not-found result requires reconciliation instead of permitting a retry.

PostgreSQL protects immutable Order fields and allowed state transitions.
Audit records cover final evaluation, preparation, state changes, and fake Broker results.

The safety review found no path to a real paper or live account.
Fill processing, reconciliation scheduling, reservation adjustment, and the Operator interface remain incomplete.

All PostgreSQL integration tests passed against an isolated PostgreSQL 18 container.
Real paper integration remains blocked until another safety review completes.

## Phase 1 Broker event and Fill update

Date: 2026-08-22

Affected mode: `PAPER_REVIEW`

The application now records fake Broker execution events and Fill records.
Each event must match the exact paper connection, endpoint, account, client Order identifier, and Broker Order identifier.

Duplicate delivery has no repeated state, Fill, exposure, or audit effect.
Out-of-order delivery can add missing evidence without Order state regression.
Conflicting reuse of an event or Fill identifier fails closed.

PostgreSQL updates the event, Fill, Order, reservation, and audit chain in one serializable transaction.
Partial Fills reduce only the open Order reservation.
Terminal Broker truth releases the remaining reservation.

Broker event ingestion remains available while the kill switch blocks new submissions.
The database prevents Fill removal and filled-quantity regression.

The review found no real paper or live Broker path.
Position snapshots, post-Fill policy response, cancellation requests, and scheduled reconciliation remain incomplete.
These items block real paper integration.

## Phase 1 post-Fill safety update

Date: 2026-08-31

Affected mode: `PAPER_REVIEW`

The application now records complete fake Broker Position snapshots after a Fill.
Each snapshot uses the exact paper connection, endpoint, and account binding.

The post-Fill evaluation combines Position market value with open Order reservations.
It compares that total with the immutable account allocation.

An unevaluated Fill blocks a later passing pre-submit evaluation for the same account.
A stale Position or allocation violation produces `VIOLATION_LOCKED`.

The violation transaction locks the Session and triggers account and Goal kill switches.
It also creates a reconciliation request and a pending Operator notification.

The request states do not prove completed reconciliation or notification delivery.
Workers and Operator interface work remain required for those outcomes.

PostgreSQL validates Fill authority, Position completeness, exposure values, and append-only storage.
Duplicate observations return the durable result after restart.

The review found no real paper or live Broker network path.
Cancellation races, full risk-policy controls, scheduled reconciliation, and production paper isolation remain incomplete.

## Phase 1 cancellation update

Date: 2026-08-31

Affected mode: `PAPER_REVIEW`

The application now stores a cancellation request before the fake Broker boundary.
The request binds the exact paper account, Broker Order, and one stable idempotency key.

Cancellation preparation changes the Order to `CANCEL_PENDING`.
It does not release exposure or claim that the Broker canceled the Order.

Broker terminal truth resolves the request, Order, remaining reservation, and audit evidence atomically.
A Fill can supersede cancellation without Order or quantity regression.

The kill-switch service can invoke the cancellation coordinator for known acknowledged and partially filled Orders.
Cancellation failure cannot clear the durable kill switch.

Restart recovery reuses one prepared fake Broker request.
An unknown result requires reconciliation and prevents another Operator request.

The review found no path to a real paper or live account.
Reconciliation completion and production paper isolation remain blocking requirements.

## Phase 1 startup recovery update

Date: 2026-08-31

Affected mode: `PAPER_REVIEW`

The Host now locks interrupted executable Sessions before it processes Broker recovery work.
Prepared cancellations run before uncertain submission lookup.

The default Host Broker adapter is unavailable and cannot submit an Order.
Unavailable Broker truth preserves the lock and moves uncertain work to reconciliation.

Post-Fill violations now pass all triggered safety scopes to the durable cancellation coordinator.
Repeated scope processing uses the existing cancellation request for each Order.

The review found no path to a real paper or live account.
Complete reconciliation, periodic scheduling, and production paper isolation remain blocking requirements.

## Phase 1 reconciliation lock update

Date: 2026-08-31

Affected mode: `PAPER_REVIEW`

A newer reconciliation difference now invalidates older `CURRENT` evidence for the exact paper binding.
A durable sequence identifies the latest result when completion times are equal.

PostgreSQL records each non-current result and its execution lock in one transaction.
The transaction locks active Sessions and triggers the account and Goal kill switches.

Preflight retains ownership of `PREFLIGHT` Session completion.
The triggered Goal scope forces failed preflight evidence without a conflicting Session transition.

The review found no paper-to-live crossing or new external Broker path.
Complete Broker truth comparison and scheduled reconciliation remain blocking requirements.

## Phase 1 simulated-paper MVP update

Date: 2026-08-31

Affected modes: `PAPER_REVIEW` and `PAPER_AUTO`

No blocking safety finding remains inside the simulated-paper scope.

The production Host now registers one simulated Broker adapter. The adapter has
no network client, credentials, or endpoint selection logic.

The Goal binds the adapter to one explicit `PAPER` endpoint and account. All
application and database checks reject a different binding.

`PAPER_REVIEW` requires exact-digest approval. `PAPER_AUTO` uses deliberate Goal
activation and does not add routine Session confirmation.

The immutable Risk policy defines every required control. Intent, pre-submit,
and post-Fill checks enforce its applicable exposure limits.

The database records `SUBMITTING` and the idempotency key before the Broker call.
Recovery looks up the original client Order identifier before any retry decision.

The final send gate reloads the exact authority, allocation, policy, Broker
observation, reconciliation, and kill switches. A changed value blocks submission.

A durable kill switch now blocks the actionable Decision boundary. It also
blocks Session, intent, approval, and submission boundaries.

Detailed reconciliation compares Orders, Fills, Positions, and reservations.
It treats each unevaluated Fill as a difference and locks execution.

Periodic reconciliation and startup recovery use the same exact account binding.
A non-current result locks Sessions and triggers account and Goal safety scopes.

The Blazor interface shows persistent authority and execution state. It uses
explicit `PAPER` and `SIMULATED` text and masks the account identifier.

The local simulation uses one fixed Operator identity and a loopback endpoint.
Passkey authentication remains required before live or remote deployment.

The simulated Broker keeps its external state in Host memory. Host restart
therefore makes simulated Broker truth unavailable or different. Recovery keeps
durable execution locked in that condition.

The review found no plausible path to a live Order. Alpaca paper integration
remains outside this MVP and requires a separate safety review.

Direct unit, architecture, and simulated adapter checks pass. The final
PostgreSQL integration suite was not available in the managed review process.

## Phase 1 detailed reconciliation evidence update

Date: 2026-08-31

Affected modes: `PAPER_REVIEW` and `PAPER_AUTO`

Preflight, intent creation, review approval, and Order preparation now use the
complete reconciliation component. No application path creates aggregate
reconciliation truth from one status value.

Each reconciliation stores hashed references for compared Orders, Fills, and
Positions. A difference also stores the required Operator action.

A database identity sequence defines the latest record for one exact paper
binding. Later gates reject an older record after a newer difference.

Risk and capital observations must match the detailed reconciliation exposure.
A mismatch fails closed before new exposure.

The Audit chain records the reconciliation start before Broker observation. It
records result and correction counts after durable completion.

Serializable preflight and Order preparation transactions recheck the latest
reconciliation, all kill-switch scopes, and the exposure reservation.

The single-Host submission interlock orders Broker submission against a
kill-switch trigger. The final safety gate runs while the interlock is held.
Reconciliation differences and post-Fill triggers use the same interlock.

The review found no real Broker client, credential, or live Order path.
Database-backed validation remains pending because this process cannot access
the Docker Desktop engine pipe.

## Phase 1 completion audit update

Date: 2026-08-31

Affected modes: `PAPER_REVIEW` and `PAPER_AUTO`

The final submission boundary now verifies Audit integrity while it holds the
single-Host submission interlock. Verification failure creates no Broker call.

Due Review approvals expire during startup and periodic maintenance. One
serializable transaction expires the intent and releases unused exposure.

A database trigger permits only time-due `PAPER_REVIEW` expiry. Approved
transitions also require the exact stored approval digest.

The transaction records Review expiry, intent transition, and reservation
release in the Audit chain. Repeated maintenance has no repeated effect.

An Operator edit creates a new intent and Risk evaluation. The earlier intent
retains its identity, terms, and digest.

One valid activation can create later Sessions after completion. The Operator
can also pause, resume, or revoke that authority without changing prior evidence.

Revocation does not release an allocation that still has reserved or filled
exposure. This control prevents another Goal from overbooking the paper account.

The Operator read model uses the durable reconciliation sequence. A later
difference cannot be hidden by an older current result.

Runtime health reports `DEGRADED` for locked or unavailable reconciliation
truth. It does not report `READY` for a known difference.

The review found no network Broker client and no real paper or live Order path.
The PostgreSQL suite remains pending because this process cannot access Docker.
