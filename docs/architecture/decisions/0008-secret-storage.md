# ADR-0008: Secret storage

Status: Accepted

Date: 2026-08-18

## Context

Broker credentials and authentication keys must stay outside source control.
Paper and live credentials must not cross deployment boundaries.

## Decision

Use .NET user secrets for local Phase 0 and paper development. Local development
must not store or load live Broker credentials.

Use files provisioned outside the repository for deployed secrets. Mount each
file read-only into its authorized container. Restrict the source file and mount
to the deployment service identity. Configuration contains only a secret name
and mounted path.

Create separate secret roots and service identities for paper and live
deployments. A deployment must reject a secret path outside its configured root.
The live deployment must not mount paper secrets. The paper deployment must not
mount live secrets.

Use an `ISecretProvider` boundary. Never pass secret values through command-line
arguments. Do not put secret values in ordinary environment variables. Redact
configuration, exception, audit, and telemetry output.

Credential rotation creates a new immutable Broker connection version. An
active Goal remains bound to its prior version until the Operator publishes and
activates a replacement Goal version.

## Safety impact

Separate mounts and identities reduce the effect of a wrong endpoint or wrong
configuration. Path validation prevents silent fallback to another environment.
Versioned rotation preserves the account authority chain.

## Rejected options

- Committed `.env` files were rejected. Source control must contain references
  only.
- Ordinary environment variables were rejected for secret values. Process and
  diagnostics tools can expose them.
- One shared secret store path was rejected. A lookup error could cross paper
  and live credentials.
- A cloud secret provider was deferred. The initial deployment does not select
  a cloud platform.

## Migration constraints

Secret references must use provider-neutral names. A later managed secret
provider must preserve environment isolation, least privilege, rotation
versioning, redaction, and fail-closed lookup behavior.

Backups must not include decrypted Broker secret files.
