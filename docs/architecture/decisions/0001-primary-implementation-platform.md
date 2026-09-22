# ADR-0001: Primary implementation platform

Status: Accepted

Date: 2026-08-18

## Context

Stockripper needs deterministic state machines, exact accounting, durable
transactions, background work, and an Operator web interface. One primary
platform reduces contract and deployment complexity during the safety work.

## Decision

Use C# on .NET 10 LTS for all production components. Pin the SDK with
`global.json`. Treat compiler warnings as errors in repository code.

Use ASP.NET Core for the application host. Use `decimal` for money, prices,
quantities, and exposure calculations. Do not use binary floating point for
accounting or authority checks.

Use xUnit for automated tests. Use architecture tests to enforce dependency
direction and Broker isolation.

## Safety impact

One type system can carry exact mode, account, policy, and version identifiers
through the authorization chain. Decimal arithmetic reduces accounting errors.
Architecture tests help prevent Strategy code from reaching execution code.

## Rejected options

- Python was not selected as the primary production platform. Its data science
  ecosystem does not offset a second production runtime during Phase 0.
- TypeScript was not selected for the execution core. It increases the risk of
  accidental binary floating-point accounting.
- A mixed-language core was not selected. It adds serialization and recovery
  boundaries before they are necessary.

## Migration constraints

Public contracts must use versioned Protocol Buffers or stable database data.
Domain identifiers must not depend on .NET type names. A later Strategy runtime
can use another language without changing execution authority.

## References

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [.NET documentation](https://learn.microsoft.com/en-us/dotnet/)
