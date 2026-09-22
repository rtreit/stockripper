# ADR-0007: Strategy runtime boundary

Status: Accepted

Date: 2026-08-18

## Context

The Strategy component can use deterministic algorithms or probabilistic model
output. It cannot authorize or submit an order.

## Decision

Run Strategy code in a separate .NET worker process. Use a versioned Protocol
Buffers contract over local authenticated gRPC. The application host sends an
immutable input snapshot reference and receives a decision proposal.

The worker has read access only to approved input snapshots. It has no Broker
secret, Broker network route, execution command, or operational database write
credential. The application host validates every response against the declared
schema, size limit, deadline, and Strategy version.

The Decision guard and Risk component run in the application host. They apply
deterministic checks after every Strategy result. A timeout, invalid response,
version mismatch, or worker failure creates no Execution intent.

Record the input snapshot, Strategy artifact digest, configuration digest,
contract version, seed, output, and guard results. Replay uses an injected
clock and fixed input data.

## Safety impact

Process and credential isolation prevent Strategy output from becoming Broker
authority. Deterministic guards fail closed on invalid or unavailable output.
Recorded inputs and versions support decision replay.

## Rejected options

- Direct Broker access from Strategy code was rejected. It bypasses authority,
  Risk, idempotency, reconciliation, and audit controls.
- An in-process plug-in was rejected for the first implementation. A defective
  Strategy would share all application process privileges.
- A message broker was rejected. Request and response work does not require a
  second durable infrastructure system.
- Python was deferred. A versioned gRPC contract allows it later without adding
  a second production runtime now.

## Migration constraints

Protocol changes must be backward compatible or use a new contract version.
Another language can implement the worker after it passes the same replay,
resource, timeout, and hostile-output tests.

The execution host remains the only component that can create an Execution
intent or call a Broker adapter.

## References

- [gRPC services with ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore?view=aspnetcore-10.0)
