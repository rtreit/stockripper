# Phase 0 foundation slice

Status: Complete

Date: 2026-08-18

## Scope

This slice creates the compilable .NET solution and its first safety
boundaries. It does not complete Phase 0.

The slice affects all six execution modes. It creates no Broker submission
path and uses no Broker credentials.

## Included behavior

- Exact parsing and serialization for all six mode values
- Rejection of missing, unknown, numeric, lowercase, or padded mode values
- Immutable Goal versions with deterministic content digests
- Required paper or live binding for each executable Goal version
- Rejection of Broker bindings in `REPLAY` and `RESEARCH`
- Immutable Goal activation records and explicit state transitions
- Session mode inheritance from the Goal version
- Explicit Session state transitions
- Injected time through `TimeProvider`
- A disconnected Broker connection probe fake
- Exact environment, endpoint, account, and capability preflight checks
- A versioned Strategy gRPC contract
- A Strategy worker that returns `NO_ACTION`
- A Blazor host that shows execution as disabled
- Architecture tests that reject a real Broker order surface

## Failure behavior

An unknown mode raises a domain rule failure. An executable Goal without an
exact binding cannot publish.

A read-only Goal cannot contain a Broker binding. A Session cannot start from
the wrong, paused, revoked, or expired activation.

Broker preflight fails on an environment, endpoint, account, or capability
difference. The error does not contain the observed account or endpoint.

## Safety test coverage

The domain tests cover all six modes. Broker preflight tests cover all four
executable modes.

Each executable mode has negative tests for wrong environment, wrong endpoint,
wrong account, and missing capability. Architecture tests verify that the fake
has no HTTP or socket dependency.

## Deferred Phase 0 work

- Signed audit checkpoints outside the operational database
- Decision guard and Risk policy evaluation
- Durable Session recovery
- Operator authentication
- Accessibility testing for the Operator interface
- Observability instrumentation

Allocation, schedule, stale-policy, order retry, Fill, reconciliation, and
kill-switch tests are not applicable to this slice. The implementation does
not contain those behaviors yet.

Phase 1 Broker submission tests remain deferred. No automated test can reach a
paper or live Broker endpoint.

## Commands

Run `./build.ps1` from the repository root. Run `./test.ps1` after the build
passes.
