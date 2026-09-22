# Architecture baseline

Status: Accepted

Date: 2026-08-18

This baseline selects the technologies required before Phase 0 implementation.
The [functional specification](../functional-specification.md) remains the
authority for product behavior.

## Selected stack

| Concern | Selection |
| --- | --- |
| Primary language | C# on .NET 10 LTS |
| Application shape | Modular monolith with one isolated Strategy worker |
| Operator interface | ASP.NET Core Blazor Web App |
| Durable database | PostgreSQL 18 |
| Data access | Npgsql and Entity Framework Core |
| Audit integrity | Append-only events with a SHA-256 hash chain and signed checkpoints |
| First Broker | Alpaca Trading API in the paper environment |
| First market data | Alpaca historical and current stock data |
| Initial instruments | Long-only, whole-share United States stocks and ETFs |
| Authentication | Local ASP.NET Core Identity with passkeys |
| Secret storage | Development user secrets and read-only deployment secret files |
| Strategy boundary | Separate .NET worker through a versioned gRPC contract |
| Observability | OpenTelemetry with an OpenTelemetry Collector and Grafana stack |

## Decision records

- [ADR-0001: Primary implementation platform](decisions/0001-primary-implementation-platform.md)
- [ADR-0002: Process and deployment model](decisions/0002-process-and-deployment-model.md)
- [ADR-0003: Durable data and audit integrity](decisions/0003-durable-data-and-audit-integrity.md)
- [ADR-0004: Operator interface and authentication](decisions/0004-operator-interface-and-authentication.md)
- [ADR-0005: Broker and market data](decisions/0005-broker-and-market-data.md)
- [ADR-0006: Initial trading scope](decisions/0006-initial-trading-scope.md)
- [ADR-0007: Strategy runtime boundary](decisions/0007-strategy-runtime-boundary.md)
- [ADR-0008: Secret storage](decisions/0008-secret-storage.md)
- [ADR-0009: Observability](decisions/0009-observability.md)
- [Architecture safety review](safety-review.md)

## Safety boundary

Phase 0 cannot contain a real Broker order path. It uses only a Broker fake.
Phase 1 can connect only to the Alpaca paper environment. Phase 2 requires a
new safety review and the approvals in the functional specification.

The paper deployment and live deployment must use separate databases, secret
mounts, networks, service identities, and Broker endpoints. One process must
never load paper and live Broker credentials together.
