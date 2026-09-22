# ADR-0009: Observability

Status: Accepted

Date: 2026-08-18

## Context

The Operator needs health, lock, reconciliation, and execution status. Telemetry
must not become an authority source or disclose sensitive values.

## Decision

Instrument the application host and Strategy worker with OpenTelemetry traces,
metrics, and structured logs. Send telemetry through OTLP to an OpenTelemetry
Collector.

Use Prometheus for metrics, Tempo for traces, Loki for logs, and Grafana for
local dashboards. Keep this stack outside the execution authority path. An
observability failure cannot unlock or authorize execution.

Use stable correlation identifiers for Goal version, Session, Decision, Intent,
Order, and Reconciliation records. Do not include secret values, full account
identifiers, order payloads, Strategy inputs, or high-cardinality market data in
telemetry.

Create alerts for endpoint or account mismatch, stale data, unknown submission,
reconciliation differences, audit failure, lost execution leadership, and kill
switch state. The durable audit record remains separate from telemetry.

## Safety impact

Telemetry makes execution locks and reconciliation failures observable. Keeping
it outside the authorization path prevents dashboard or collector state from
changing trading authority. Redaction reduces credential and account exposure.

## Rejected options

- Application logs alone were rejected. They do not provide correlated health,
  metrics, and traces.
- A hosted vendor was deferred. The initial deployment does not select a cloud
  or monitoring vendor.
- Telemetry as the audit trail was rejected. Export can fail and retention can
  differ from durable execution records.

## Migration constraints

Instrumentation must use OpenTelemetry APIs and OTLP at the export boundary.
Backends can change without changing domain behavior. Alert migrations must
preserve all safety-critical conditions.

## References

- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/)
