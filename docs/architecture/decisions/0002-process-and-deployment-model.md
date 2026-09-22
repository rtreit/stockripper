# ADR-0002: Process and deployment model

Status: Accepted

Date: 2026-08-18

## Context

The system needs explicit component boundaries without early distributed-system
complexity. Strategy code must remain unable to submit an order.

## Decision

Use a modular monolith for the Operator interface, Goal service, Session
coordinator, Risk component, Execution coordinator, Reconciliation component,
Broker adapter, and Audit service.

Run the Strategy runtime as a separate process. The application host owns all
durable state and all external Broker calls. The Strategy worker has no Broker
credentials and no database write credentials.

Package each process as a Linux container. Use Docker Compose for local and
single-host deployment. Run one application host as the execution leader for
one Broker account. Use a durable database lease to reject a second leader.

Create separate deployment projects for paper and live environments. Each
project has a separate database, network, secret mount, service identity, and
hostname. Do not put paper and live Broker credentials in one deployment.

## Safety impact

One execution leader reduces duplicate submission risk. A separate Strategy
process cannot bypass Risk, approval, idempotency, or audit controls. Separate
deployments make endpoint and credential crossing visible and testable.

## Rejected options

- Microservices were rejected for the first implementation. Distributed state
  and retries add failure paths before independent scaling is necessary.
- Kubernetes was rejected for the first deployment. Automatic rescheduling and
  scaling complicate execution leadership and restart analysis.
- One deployment with both paper and live secrets was rejected. A configuration
  error could cross execution environments.
- An in-process Strategy runtime was rejected. It gives Strategy code more
  access than its role requires.

## Migration constraints

Modules must communicate through explicit application contracts. Modules must
not share internal persistence models. A later service extraction must preserve
transactional idempotency, ordering, and audit continuity.

The application must stop execution when it loses its leadership lease. A
deployment change must not permit two leaders for one Broker account.

## References

- [Common web application architectures](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures)
- [gRPC services with ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore?view=aspnetcore-10.0)
