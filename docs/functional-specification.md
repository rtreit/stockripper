# Stockripper functional specification

Version: 0.3

Status: Approved functional baseline

Approval date: 2026-08-18

Approved by: Operator

Scope: Research, paper trading, and live trading

This document defines required Stockripper behavior. It does not select an
implementation technology.

The
[v0.2 live trading revision](../Stockripper%20Specification%20v0.2%20%E2%80%94%20Live%20Trading%20Revision.md)
is the source for the live trading requirements in this baseline.

## 1. Purpose

Stockripper manages goal-driven market research and trade execution. It uses
one explicit execution mode for each immutable goal version.

Stockripper must support research, paper trading, and live trading without
crossing their authorization boundaries.

### 1.1 Normative language

The word "must" identifies a required behavior. The word "can" identifies an
allowed behavior.

### 1.2 Product scope

Stockripper must provide these capabilities:

- Define and version investment goals.
- Activate one immutable goal version.
- Run deterministic replay and read-only research.
- Create reviewable trade decisions.
- Submit paper and live orders in authorized modes.
- Apply deterministic risk checks before submission.
- Reconcile broker orders, fills, and positions.
- Recover safely after interruption.
- Record a complete audit trail.
- Stop execution with a persistent kill switch.

### 1.3 Initial non-goals

The first implementation does not require these capabilities:

- Custody of funds or securities
- Broker-dealer services
- Tax preparation
- Investment advice for third parties
- Social trading
- Public strategy marketplaces
- High-frequency or exchange-colocated trading
- Automatic transfer of cash between accounts

Later specifications can add these capabilities.

## 2. Product principles

### 2.1 Explicit authority

An executable action must have a complete authorization chain. The chain
starts at an immutable goal version and ends at an exact broker account.

### 2.2 Accepted market risk

A valid trade can lose money. A loss does not prove a software failure.

### 2.3 Unacceptable software risk

These outcomes are software failures:

- Stockripper trades the wrong account.
- Stockripper exceeds the authorized capital allocation.
- Stockripper crosses paper and live endpoints.
- Stockripper duplicates an order.
- Stockripper creates an unauditable trade.

### 2.4 Safety before availability

Stockripper must stop execution when it cannot prove authority or state.
Reduced availability is acceptable during a safety conflict.

### 2.5 Broker truth

The broker is authoritative for accepted orders, fills, and broker positions.
Stockripper remains authoritative for goals, authority, decisions, and intent.

### 2.6 Replayable behavior

Stockripper must preserve the inputs and versions needed to reproduce each
decision when the source data remains available.

## 3. Actors

### 3.1 Operator

The Operator owns goals and execution authority. The Operator can activate,
pause, revoke, approve, reject, and stop authorized work.

### 3.2 Stockripper

Stockripper coordinates research, decisions, risk checks, execution,
reconciliation, recovery, and audit records.

### 3.3 Broker

The Broker accepts, rejects, cancels, and fills orders. The Broker reports
accounts, balances, orders, fills, positions, and market session state.

### 3.4 Market data provider

The Market data provider supplies historical or current market data. It does
not grant execution authority.

### 3.5 Strategy component

The Strategy component produces a decision proposal. It cannot authorize or
submit an order.

### 3.6 Administrator

The Administrator manages deployment and secret references. Administrative
access does not grant trading authority.

## 4. Execution modes

Each goal version must select one mode. A session must inherit that mode
without modification.

| Mode | Data use | Order path | Required approval |
| --- | --- | --- | --- |
| `REPLAY` | Historical, fixed | None | Goal activation |
| `RESEARCH` | Historical or current | None | Goal activation |
| `PAPER_REVIEW` | Current or simulated | Paper | Each execution intent |
| `PAPER_AUTO` | Current or simulated | Paper | Goal activation |
| `LIVE_REVIEW` | Current | Live | Each execution intent |
| `LIVE_AUTO` | Current | Live | Goal activation |

### 4.1 Mode rules

- Stockripper must reject an unknown mode.
- Stockripper must reject a missing executable mode.
- A goal version cannot change its mode.
- A session cannot override its goal version mode.
- A strategy cannot select or change a mode.
- `REPLAY` and `RESEARCH` cannot create an executable order intent.
- Review modes require approval for each immutable execution intent.
- Auto modes do not require approval for each execution intent.

### 4.2 Live auto authority

Deliberate `LIVE_AUTO` goal activation is the authorization boundary. A valid
later session can execute without session confirmation.

Stockripper must not add routine session confirmation after valid activation.
Stockripper must still complete all preflight and risk checks.

## 5. Functional records

All durable records must use stable identifiers. A record must retain its
creation time and source identity.

### 5.1 Goal

A Goal represents an Operator objective. It is a stable container for goal
versions.

A Goal must contain:

- Goal identifier
- Display name
- Objective
- Owner identifier
- Creation time
- Current lifecycle status

### 5.2 Goal draft

A Goal draft is mutable. It cannot authorize a session or order.

A Goal draft must contain every field required for a Goal version. Publication
must validate the complete draft.

### 5.3 Goal version

A published Goal version is immutable. A change requires a new Goal version.

A Goal version must contain:

- Goal version identifier
- Goal identifier
- Version number
- Execution mode
- Objective
- Instrument universe
- Strategy version
- Data policy version
- Risk policy version
- Capital allocation
- Execution schedule
- Decision schedule
- Output and evaluation criteria
- Creator identifier
- Creation time
- Content digest

An executable Goal version must also contain:

- Broker connection version
- Expected broker environment
- Expected broker account identifier
- Required broker capabilities
- Order policy version
- Reconciliation policy version

### 5.4 Strategy version

A Strategy version identifies one immutable strategy artifact and configuration.

It must contain:

- Strategy identifier and version
- Artifact identity and digest
- Configuration digest
- Required input schema version
- Produced output schema version
- Creation time

### 5.5 Data policy version

A Data policy version defines decision input rules.

It must contain:

- Approved data sources
- Instrument identity rules
- Market calendar source
- Timestamp and time-zone rules
- Freshness limits
- Missing data response
- Snapshot retention rule
- Corporate action handling when applicable

### 5.6 Order policy version

An Order policy version defines allowed order behavior.

It must contain:

- Allowed order types
- Allowed time-in-force values
- Price protection rules
- Quantity precision rules
- Submission timeout rule
- Safe retry rule
- Cancellation rule
- Replacement rule
- Schedule-close response
- Extended-hours permission

### 5.7 Reconciliation policy version

A Reconciliation policy version defines comparison behavior.

It must contain:

- Reconciliation schedule
- Broker history range
- Event overlap range
- Position comparison rule
- Numeric tolerance by instrument
- External order response
- Difference severity rules
- Execution lock rules

### 5.8 Goal activation

A Goal activation grants current authority to one Goal version.

A Goal activation must contain:

- Activation identifier
- Goal version identifier
- Operator identifier
- Activation time
- Activation state
- Activation evidence
- Optional expiry time
- Revocation time when revoked
- Revocation reason when revoked

Only one Goal version for one Goal can be active at one time.

### 5.9 Account allocation ledger

The Account allocation ledger prevents conflicting authority on one Broker
account.

Each allocation entry must contain:

- Allocation entry identifier
- Broker connection version
- Broker account identifier
- Goal version identifier
- Allocation amount and currency
- Exposure method identifier
- Effective start and end times
- Allocation state

Activation must reserve the Goal capital allocation atomically. The sum of
active allocations cannot exceed the Operator-defined account limit.

Concurrent Goals and Sessions must use the same durable ledger for one account.

### 5.10 Broker connection version

A Broker connection version describes one immutable connection binding.

It must contain:

- Connection identifier and version
- Broker provider identifier
- Environment classification
- Endpoint identity or fingerprint
- Secret reference
- Expected account identifier
- Required account capabilities
- Connection policy version

The environment classification must be `PAPER` or `LIVE`. Stockripper must not
infer the classification from an account name.

### 5.11 Risk policy version

A Risk policy version defines deterministic execution limits.

It must define or explicitly disable each applicable control:

- Allowed instruments
- Allowed order types
- Allowed position direction
- Capital allocation calculation
- Maximum order exposure
- Maximum position exposure
- Maximum portfolio exposure
- Concentration limit
- Buying power reserve
- Price protection rule
- Market data freshness limit
- Trading schedule
- Daily loss response
- Open order limit
- Retry limit
- Review approval expiry

A missing control value cannot mean that the control is disabled.

### 5.12 Session

A Session runs one Goal version under one Goal activation.

A Session must contain:

- Session identifier
- Goal version identifier
- Activation identifier
- Execution mode
- Start and end times
- Session state
- Observed connection identity
- Observed account identity
- Input snapshot references
- Failure reason when applicable

### 5.13 Decision

A Decision records one Strategy component result.

A Decision must contain:

- Decision identifier
- Session identifier
- Goal version identifier
- Strategy version
- Input snapshot references
- Decision time
- Proposed action
- Explanation data
- Confidence data when applicable
- Deterministic guard results
- Decision status

A Decision cannot contain broker authority. A Decision cannot directly submit
an order.

### 5.14 Execution intent

An Execution intent is an immutable request for one external order action.

It must contain:

- Intent identifier
- Decision identifier
- Goal version identifier
- Session identifier
- Mode
- Account binding
- Instrument identifier
- Side
- Order type
- Quantity or notional value
- Price constraints
- Time-in-force value
- Expiry time
- Risk evaluation identifier
- Idempotency key
- Creation time
- Intent status

An Operator edit must create a new Execution intent. Stockripper must evaluate
the new intent again.

### 5.15 Review approval

A Review approval binds one Operator action to one Execution intent.

It must contain:

- Approval identifier
- Intent identifier
- Operator identifier
- Approval or rejection result
- Action time
- Intent digest
- Expiry time
- Operator note when supplied

Approval of one intent cannot authorize another intent.

### 5.16 Order record

An Order record tracks one broker order identity.

It must contain:

- Internal order identifier
- Intent identifier
- Idempotency key
- Broker client order identifier
- Broker order identifier when known
- Broker account identifier
- Submitted request snapshot
- Current order state
- Broker event references
- First submission time
- Last update time
- Terminal time when applicable

### 5.17 Fill record

A Fill record represents one broker-reported execution.

It must contain:

- Internal fill identifier
- Broker fill identifier when available
- Internal order identifier
- Broker order identifier
- Instrument identifier
- Quantity
- Price
- Fee data when available
- Broker execution time
- Receipt time
- Source payload reference

Stockripper must deduplicate Fill records.

### 5.18 Position snapshot

A Position snapshot records broker position truth at one time.

It must contain:

- Snapshot identifier
- Broker connection version
- Broker account identifier
- Instrument identifier
- Quantity
- Cost data when available
- Broker observation time
- Receipt time

### 5.19 Reconciliation record

A Reconciliation record compares Stockripper state with Broker state.

It must contain:

- Reconciliation identifier
- Scope
- Start and end times
- Broker account identifier
- Compared order range
- Compared fill range
- Compared position snapshot
- Differences
- Applied corrections
- Result state
- Operator action when required

### 5.20 Audit event

An Audit event records one material state change or external action.

It must contain:

- Event identifier
- Event type
- Event sequence
- Event time
- Actor identity
- Goal version identifier when applicable
- Session identifier when applicable
- Related record identifiers
- Previous and new states
- Reason
- Source reference
- Integrity data

## 6. State models

State changes must use explicit transitions. Stockripper must reject an invalid
transition and record the rejection.

### 6.1 Goal activation states

Valid states are:

- `INACTIVE`
- `ACTIVE`
- `PAUSED`
- `REVOKED`
- `EXPIRED`

Valid transitions are:

- `INACTIVE` to `ACTIVE`
- `ACTIVE` to `PAUSED`
- `PAUSED` to `ACTIVE`
- `ACTIVE` to `REVOKED`
- `PAUSED` to `REVOKED`
- `ACTIVE` to `EXPIRED`
- `PAUSED` to `EXPIRED`

`REVOKED` and `EXPIRED` are terminal states.

### 6.2 Session states

Valid states are:

- `CREATED`
- `PREFLIGHT`
- `READY`
- `RUNNING`
- `STOPPING`
- `RECONCILING`
- `COMPLETED`
- `FAILED`
- `LOCKED`

A Session must complete preflight before `READY`. An executable Session must
complete reconciliation before `COMPLETED`.

`LOCKED` requires reconciliation or Operator action. It cannot submit new
orders.

The normal transition path is:

`CREATED` to `PREFLIGHT` to `READY` to `RUNNING` to `STOPPING` to
`RECONCILING` to `COMPLETED`.

An active nonterminal state can enter `FAILED` or `LOCKED`. A corrected locked
Session must return through `PREFLIGHT`.

### 6.3 Decision states

Valid states are:

- `PROPOSED`
- `GUARD_REJECTED`
- `INTENT_CREATED`
- `NO_ACTION`
- `FAILED`

A valid actionable Decision can create one or more Execution intents. Each
intent must retain the Decision identifier.

### 6.4 Execution intent states

Valid states are:

- `CREATED`
- `RISK_ACCEPTED`
- `RISK_REJECTED`
- `REVIEW_PENDING`
- `APPROVED`
- `REJECTED`
- `EXPIRED`
- `SUBMISSION_PENDING`
- `SUBMITTED`
- `SUBMISSION_UNKNOWN`
- `CANCELED`
- `FAILED`

An approved intent becomes invalid after its expiry time. A changed risk input
must cause a new risk evaluation before submission.

An auto-mode intent can move from `RISK_ACCEPTED` to `SUBMISSION_PENDING`. A
review-mode intent must move through `REVIEW_PENDING` and `APPROVED`.

### 6.5 Order states

Valid states are:

- `CREATED`
- `SUBMITTING`
- `ACKNOWLEDGED`
- `PARTIALLY_FILLED`
- `FILLED`
- `CANCEL_PENDING`
- `CANCELED`
- `REJECTED`
- `EXPIRED`
- `SUBMISSION_UNKNOWN`
- `RECONCILIATION_REQUIRED`

Terminal broker states are `FILLED`, `CANCELED`, `REJECTED`, and `EXPIRED`.

`CREATED` must move to `SUBMITTING` before an external call. `SUBMITTING` can
move to `ACKNOWLEDGED`, `REJECTED`, or `SUBMISSION_UNKNOWN`.

`ACKNOWLEDGED` and `PARTIALLY_FILLED` can move to any valid later Broker state.
`CANCEL_PENDING` remains nonterminal until Broker confirmation.

Reconciliation can move `SUBMISSION_UNKNOWN` to an observed Broker state. An
unresolved difference must move the Order to `RECONCILIATION_REQUIRED`.

### 6.6 Kill switch states

Valid states are:

- `CLEAR`
- `TRIGGERED`
- `RECOVERY_REQUIRED`

Only an authorized Operator can clear `RECOVERY_REQUIRED`. Stockripper must
complete reconciliation before it accepts the clear action.

## 7. Goal workflow

### 7.1 Draft creation

The Operator can create and edit a Goal draft. A draft cannot start a Session.

### 7.2 Publication

Stockripper must validate all required fields before publication. Publication
creates an immutable Goal version and content digest.

Executable Goal publication must validate these bindings:

- Mode and environment compatibility
- Broker connection version
- Broker account identifier
- Capital allocation
- Execution schedule
- Risk policy version
- Order policy version
- Reconciliation policy version

Activation must also reserve the capital allocation in the Account allocation
ledger. A conflicting or excessive allocation must prevent activation.

### 7.3 Activation

Activation must be a deliberate Operator action. Stockripper must show the
complete authority summary before activation.

The summary must show:

- Goal and Goal version
- Execution mode
- Broker provider
- Paper or live environment
- Masked account identifier
- Capital allocation
- Execution schedule
- Risk policy version
- Strategy version
- Activation expiry when present

The activation record must preserve the exact summary and Operator evidence.

### 7.4 Live activation

`LIVE_REVIEW` and `LIVE_AUTO` activation must require strong Operator
authentication. The activation interface must label the environment as live.

The interface must not use color as the only live indicator.

### 7.5 Pause and revocation

Pause must stop new Session starts and new Execution intents. Pause does not
remove existing broker orders.

Revocation is permanent for that activation. Revocation must stop new Session
starts and new Execution intents.

The Operator must use order cancellation or the kill switch for open orders.

## 8. Session workflow

### 8.1 Session creation

Stockripper can create a Session only from an active Goal activation. The
Session must copy immutable identifiers from that activation.

### 8.2 Preflight

Every Session must verify:

- Goal activation remains active.
- Goal version digest remains valid.
- Current time is inside the allowed schedule.
- Required data sources are available.
- Required versioned policies are available.
- The kill switch is clear.

An executable Session must also verify:

- Broker endpoint identity matches the binding.
- Broker environment matches the mode.
- Authenticated account matches the binding.
- Required account capabilities are present.
- Broker clock and market session data are usable.
- Reconciliation state is current.
- Capital and buying power data are current.

Preflight failure must prevent execution. Stockripper must record each failed
check without recording a secret.

### 8.3 Session execution

A Session can run scheduled or Operator-started decision cycles. Each cycle
must use the same Goal version.

### 8.4 Session completion

A read-only Session can complete after it records its output. An executable
Session must reconcile before completion.

## 9. Research and replay

### 9.1 Replay mode

`REPLAY` must use a fixed input data set and an injected clock. It must not
read execution credentials.

A Replay result must identify:

- Data set version
- Time range
- Strategy version
- Goal version
- Configuration versions
- Random seed when applicable
- Output metrics

### 9.2 Research mode

`RESEARCH` can use historical or current read-only data. It must not create an
Execution intent.

Research output must identify its data sources and observation times.

### 9.3 Promotion prohibition

Stockripper must not convert a Replay or Research Session into an executable
Session. The Operator must publish and activate an executable Goal version.

## 10. Decision workflow

### 10.1 Strategy input

Stockripper must give the Strategy component a versioned input snapshot. The
input must exclude broker credentials and execution authority.

### 10.2 Strategy output

The Strategy component can propose action or no action. The proposal must use
a defined schema.

Malformed output must become `GUARD_REJECTED`. It must not reach the Broker
adapter.

### 10.3 Deterministic guards

Stockripper must apply deterministic guards after each Strategy component
result. These guards must validate schema, instrument, mode, schedule, and data
freshness.

Probabilistic model output cannot authorize or submit an order.

The normal actionable pipeline is:

Decision proposal, deterministic guard, Execution intent, risk evaluation,
review when required, final checks, submission, and reconciliation.

### 10.4 Decision evidence

Stockripper must retain enough evidence to explain each proposed action. It
must also record rejected and no-action results.

## 11. Review workflow

### 11.1 Review queue

`PAPER_REVIEW` and `LIVE_REVIEW` must place valid Execution intents in a review
queue.

The queue must show:

- Environment
- Masked account identifier
- Goal and Goal version
- Instrument and side
- Quantity or notional value
- Order type and price constraints
- Maximum calculated exposure
- Risk results
- Intent expiry
- Decision explanation

### 11.2 Approval

The Operator can approve or reject an immutable Execution intent. Approval must
bind to the intent digest.

Stockripper must run final pre-submit checks after approval. A failed check
must invalidate submission.

### 11.3 Modification

The Operator cannot modify an approved intent in place. A modification must
create a new intent and risk evaluation.

### 11.4 Expiry

An expired approval cannot authorize submission. Stockripper must require a new
intent or approval according to the policy.

## 12. Risk control

### 12.1 Risk authority

The Risk component can reject an action. It cannot expand Goal authority.

The Operator cannot bypass one failed risk check. The Operator must publish a
new policy or Goal version.

### 12.2 Capital allocation

Capital allocation is a hard execution limit. Stockripper must include current
positions and worst-case open order exposure in its calculation.

Every supported instrument and order type must have a defined exposure method.
Stockripper must reject an action when the method is absent.

### 12.3 Price protection

Each executable buy or short action must have computable maximum exposure.
Stockripper must reject an unbounded order when exposure cannot be proven.

### 12.4 Reservation

Stockripper must reserve calculated exposure before broker submission. It must
release or adjust the reservation after broker events and reconciliation.

Concurrent Sessions must use the same durable reservation state for one
account.

The Account allocation ledger must include allocations from every active Goal
on that account.

### 12.5 Pre-submit evaluation

Stockripper must evaluate these inputs immediately before submission:

- Active Goal authority
- Current account identity
- Current endpoint identity
- Current positions
- Open orders and reservations
- Capital allocation
- Buying power
- Instrument permission
- Order type permission
- Price protection
- Data freshness
- Execution schedule
- Kill switch state

### 12.6 Post-fill evaluation

Stockripper must update exposure after every fill. It must trigger a safety
response when observed exposure violates policy.

The safety response must block new exposure. It must start reconciliation and
notify the Operator.

## 13. Broker integration

### 13.1 Broker adapter contract

Each Broker adapter must provide these logical operations:

- Identify endpoint and environment
- Authenticate and identify account
- Report account capabilities
- Report balances and buying power
- Report market session state
- Submit an order with a client identifier
- Find an order by client identifier
- Read open and historical orders
- Read fills
- Read positions
- Request cancellation
- Report adapter health

### 13.2 Connection handshake

Stockripper must complete a handshake before executable work. The observed
environment and account must equal the Goal binding.

A mismatch must lock execution. Configuration labels cannot override observed
Broker identity.

### 13.3 Idempotent submission

Stockripper must create and persist the idempotency key before submission. The
key must be unique for one immutable Execution intent.

Before the external call, Stockripper must durably record:

- Goal and activation identifiers
- Execution intent and digest
- Final authorization and risk results
- Exposure reservation
- Broker connection and account binding
- Complete outbound order request
- Broker client order identifier
- Submission preparation audit event

The durable write must complete atomically. A failure must prevent the Broker
call.

Stockripper must reuse the same Broker client order identifier for a safe retry.
It must never create a new identifier for the same intent.

### 13.4 Unknown submission outcome

A timeout or connection loss can make submission outcome unknown. Stockripper
must not submit a blind retry.

Stockripper must query the Broker with the client identifier. It must enter
`SUBMISSION_UNKNOWN` when Broker truth remains unavailable.

`SUBMISSION_UNKNOWN` must block a replacement order for the same intent.

### 13.5 Broker event ingestion

Stockripper must accept duplicate and out-of-order Broker events. Event handling
must be idempotent.

Stockripper must retain source payload references for audit and diagnosis.

### 13.6 Cancellation

A cancellation request is not a canceled order. Stockripper must retain
exposure until Broker truth confirms a terminal state.

## 14. Reconciliation

### 14.1 Reconciliation scope

Reconciliation must compare:

- Stockripper Order records
- Broker orders
- Stockripper Fill records
- Broker fills
- Stockripper calculated positions
- Broker positions
- Open exposure reservations

### 14.2 Reconciliation times

Stockripper must reconcile:

- Before an executable Session becomes ready
- After an unknown submission outcome
- After a process restart
- After a connection interruption
- After a kill-switch trigger
- Before an executable Session completes
- On the configured periodic schedule

### 14.3 Difference handling

Stockripper can correct local derived state from Broker truth. It must record
the old state, Broker evidence, and correction.

Stockripper must lock execution when a difference can change authority or
exposure. The Operator must receive a clear difference report.

### 14.4 External orders

An order created outside Stockripper is an external order. Reconciliation must
record it and include its exposure.

Stockripper must not claim ownership of an external order. Policy must define
whether external orders lock new execution.

## 15. Kill switch

### 15.1 Scope

The kill switch must support system, connection, account, and Goal scopes.
The broadest active scope controls execution.

### 15.2 Trigger behavior

A trigger must immediately block:

- New Sessions
- New Decisions that can create exposure
- New Execution intents
- New approvals
- New Broker submissions

Stockripper must attempt cancellation for affected open orders. It must track
each cancellation until Broker confirmation or reconciliation.

### 15.3 Position behavior

The kill switch must not liquidate positions by default. Emergency liquidation
must be a separate, explicit policy and Operator action.

### 15.4 Persistence

The triggered state must survive process and host restart. Failure to read kill
switch state must block execution.

### 15.5 Recovery

Stockripper must reconcile affected accounts before switch recovery. It must
show unresolved orders, fills, positions, and exposure.

An authorized Operator must clear `RECOVERY_REQUIRED`. Clear evidence must
enter the audit trail.

## 16. Restart recovery

### 16.1 Startup lock

Stockripper must start executable components in a locked state. It must not
submit an order before recovery completes.

### 16.2 Recovery sequence

Stockripper must complete this sequence:

1. Load durable authority and execution state.
2. Load persistent kill-switch state.
3. Verify record integrity.
4. Reconnect to the bound Broker endpoint.
5. Verify environment and account identity.
6. Retrieve orders, fills, positions, and balances.
7. Resolve unknown submissions.
8. Reconcile orders, fills, positions, and reservations.
9. Evaluate current risk and schedule state.
10. Unlock only valid scopes.

### 16.3 Auto mode recovery

A valid `PAPER_AUTO` or `LIVE_AUTO` activation can resume after successful
recovery. Stockripper must not require routine session confirmation.

Recovery must not resume when authority expired, changed, or became invalid.

## 17. Audit and traceability

### 17.1 Required event coverage

Stockripper must record these events:

- Goal draft publication
- Goal activation, pause, resume, expiry, and revocation
- Session creation and every Session transition
- Connection and account verification
- Decision creation and guard results
- Risk evaluation and reservation changes
- Review approval, rejection, and expiry
- Execution intent creation and transition
- Broker request and response references
- Order and Fill transitions
- Reconciliation start, result, and correction
- Kill-switch trigger and recovery
- Administrative configuration changes
- Integrity and authorization failures

### 17.2 Event properties

Audit events must be append-only. The storage design must detect event deletion,
replacement, or reordering.

Each external action must have a trace from Goal version to resulting position.

### 17.3 Time

Stockripper must store event time in Coordinated Universal Time. It must also
preserve Broker time and receipt time when both exist.

### 17.4 Sensitive data

Audit data must not contain secret values. Access controls must protect full
account identifiers and sensitive strategy data.

## 18. Operator interface

### 18.1 Persistent status

Every execution view must show:

- Current mode
- Paper or live environment
- Masked account identifier
- Goal and Goal version
- Activation state
- Session state
- Kill-switch state
- Reconciliation state

The interface must not use color as the only status indicator.

### 18.2 Live distinction

Live views and actions must use explicit `LIVE` text. Paper views and actions
must use explicit `PAPER` text.

The interface must not place live and paper submit controls in one ambiguous
context.

### 18.3 Activation interface

The activation interface must show the complete authority summary. It must
require an explicit Operator action after the summary appears.

### 18.4 Review interface

The review interface must show immutable intent details and risk results. It
must show changes by creating a replacement intent.

### 18.5 Operational alerts

The interface must alert the Operator for:

- Account or endpoint mismatch
- Risk rejection
- Submission unknown
- Reconciliation difference
- Kill-switch trigger
- Broker disconnection
- Stale market data
- Audit integrity failure

## 19. Security and access

### 19.1 Secret handling

Source control must contain secret references only. Runtime logs, errors, and
audit events must not contain secret values.

### 19.2 Credential isolation

Paper and live credentials must use separate secret references. A connection
must use the least privilege required for its mode.

### 19.3 Operator authentication

Live activation, live approval, kill-switch recovery, and emergency actions
must require authenticated Operator identity.

### 19.4 Administrative separation

Administrative access cannot create trading authority. Trading authority must
come from an Operator activation or review approval.

### 19.5 Untrusted input

Stockripper must validate Broker data, market data, imported files, and model
output before use.

## 20. Logical component boundaries

The implementation must preserve these logical responsibilities:

- Goal service manages drafts, versions, and activation.
- Session coordinator manages Session state and schedules.
- Data service creates versioned input snapshots.
- Strategy runtime produces decision proposals.
- Decision guard validates Strategy component output.
- Risk component evaluates exposure and policy.
- Execution coordinator owns intents, reservations, and submissions.
- Broker adapter isolates provider behavior.
- Reconciliation component compares durable and Broker state.
- Audit service records material events.
- Operator interface presents authority and state.

One process can contain multiple logical components. A component boundary does
not require a network service.

## 21. Configuration and versioning

### 21.1 Versioned configuration

Behavior that can change a decision or order must use an immutable version.
This rule includes strategies, data policies, risk policies, and order policies.

An authorized safety action can revoke a version without changing its content.
A revoked required version must block new execution.

### 21.2 Runtime configuration

Runtime configuration can select deployment resources. It cannot change Goal
authority or silently replace a bound version.

### 21.3 Configuration failure

Missing or invalid executable configuration must block execution. Stockripper
must identify the failed configuration without exposing secrets.

## 22. Failure behavior

### 22.1 Fail-closed conditions

These conditions must block new execution:

- Unknown mode
- Invalid Goal digest
- Inactive Goal activation
- Account mismatch
- Endpoint mismatch
- Environment mismatch
- Missing policy version
- Stale required data
- Unknown submission outcome
- Unresolved reconciliation difference
- Kill-switch trigger
- Audit integrity failure
- Unavailable durable state

### 22.2 Degraded read-only operation

Stockripper can keep read-only views available during an execution lock. The
interface must show that execution is unavailable.

### 22.3 Notification failure

Notification failure cannot remove an execution lock. Stockripper must record
notification failure and retry according to policy.

## 23. Quality requirements

### 23.1 Determinism

Replay must produce the same decisions for the same versioned inputs and seed.
External timestamps must enter through explicit boundaries.

### 23.2 Durability

Authority, idempotency, order, Fill, reconciliation, and kill-switch state must
survive restart.

### 23.3 Concurrency

Concurrent workers must not exceed shared allocation. They must not submit the
same Execution intent more than once.

### 23.4 Observability

Stockripper must expose structured health, lock, reconciliation, and execution
status. Metrics must not contain secret values.

### 23.5 Accessibility

Operator workflows must support keyboard use. Status and warnings must not
depend on color alone.

## 24. Acceptance test matrix

### 24.1 All modes

Each mode must pass these tests:

- Correct mode inheritance
- Unknown mode rejection
- Goal digest validation
- Schedule enforcement
- Decision evidence recording
- Audit continuity
- Restart behavior

### 24.2 Executable modes

Each executable mode must pass these tests:

- Correct endpoint and account
- Wrong endpoint rejection
- Wrong account rejection
- Missing binding rejection
- Revoked or expired activation rejection
- Mismatched Goal version binding rejection
- Missing or revoked policy rejection
- Conflicting account allocation rejection
- Allocation boundary acceptance
- Allocation excess rejection
- Schedule closure before submission
- Duplicate submission prevention
- Restart before durable submission preparation
- Restart after preparation and before the Broker call
- Restart after the Broker call and before acknowledgement storage
- Timeout before acknowledgement
- Timeout after Broker acceptance
- Partial Fill handling
- Cancellation race handling
- Rejected order handling
- Out-of-order Broker events
- Process restart during each Order state
- Kill-switch trigger during each Order state
- End-to-end audit trace

### 24.3 Review modes

Review modes must pass these tests:

- Approval binds to one intent digest.
- Rejection prevents submission.
- Modification creates a new intent.
- Expired approval prevents submission.
- Final risk failure prevents submission.

### 24.4 Auto modes

Auto modes must pass these tests:

- Valid activation permits later Sessions.
- Routine session confirmation is absent.
- Preflight failure prevents execution.
- Activation expiry prevents new Sessions.
- Successful recovery permits safe resume.

### 24.5 Live modes

Live mode automated tests must use a Broker fake. An approved manual runbook
can use a dedicated live account with bounded capital.

## 25. Delivery phases

### 25.1 Phase 0: Deterministic foundation

Phase 0 includes:

- Goal drafts and immutable Goal versions
- All six mode values and validation
- Activation records
- Session state model
- Replay and Research workflows
- Versioned data snapshots
- Strategy and guard contracts
- Risk policy model
- Append-only audit records
- Broker fake
- Root build and test commands
- Continuous integration checks

Phase 0 acceptance requires deterministic Replay and complete read-only audit
traceability. No real Broker order path can exist.

### 25.2 Phase 1: Paper fire

Phase 1 includes:

- Broker adapter contract
- Paper account binding
- `PAPER_REVIEW`
- `PAPER_AUTO`
- Execution intents and approvals
- Durable idempotency
- Fill processing
- Reconciliation
- Exposure reservations
- Kill switch
- Restart recovery

Phase 1 acceptance requires all executable tests against a Broker fake. It also
requires approved paper-environment end-to-end tests.

### 25.3 Phase 2: Live fire

Phase 2 includes:

- Live credential isolation
- Live endpoint and account binding
- `LIVE_REVIEW`
- `LIVE_AUTO`
- Deliberate live activation
- Actual-Fill reconciliation
- Persistent kill-switch behavior
- Live restart recovery
- End-to-end live traceability
- Manual live test runbook

Phase 2 acceptance requires all Phase 1 tests for live modes with a Broker
fake. It also requires bounded manual tests in a dedicated live account.

The Operator must approve the account, capital limit, instruments, schedule,
and runbook before the first manual live test.

### 25.4 Phase 3: Extension

Phase 3 can add more Brokers, instruments, strategies, interfaces, and
deployment targets. Each addition must preserve the same authority chain.

## 26. Architecture decisions required before implementation

The project must record these decisions before Phase 0 implementation:

- Primary implementation language
- Process and deployment model
- Durable database
- Audit integrity method
- Operator interface type
- First Broker and Broker environment
- First market data source
- Initial instrument types
- Authentication provider
- Secret storage method
- Strategy runtime boundary
- Observability stack

Each decision must include safety impact, rejected options, and migration
constraints.

## 27. Glossary

### Account binding

The immutable link between Goal authority and one observed Broker account.

### Capital allocation

The maximum authorized exposure for one Goal version.

### Execution intent

An immutable, risk-evaluated request for one Broker order action.

### Goal activation

The Operator action that grants current authority to one Goal version.

### Goal version

An immutable definition of objective, mode, bindings, policies, and schedule.

### Idempotency key

A stable identifier that prevents duplicate external action for one intent.

### Kill switch

A persistent control that blocks new execution and starts order cancellation.

### Reconciliation

The comparison and controlled correction of Stockripper state with Broker
truth.

### Software risk

The risk that Stockripper violates authority, identity, allocation, execution,
recovery, or audit requirements.
