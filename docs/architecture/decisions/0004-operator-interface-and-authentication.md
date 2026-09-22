# ADR-0004: Operator interface and authentication

Status: Accepted

Date: 2026-08-18

## Context

The Operator needs a local, accessible interface that shows authority and
execution state. Live actions require strong authentication and clear context.

## Decision

Use an ASP.NET Core Blazor Web App with interactive server rendering. Host the
interface in the application host. Do not expose a public registration path.

Use ASP.NET Core Identity passkeys for Operator authentication. A local
bootstrap command creates the first Operator. The Operator must register two
passkeys before a live deployment can become executable.

Require a fresh passkey assertion for live Goal activation, each live review
approval, kill-switch recovery, and emergency actions. Do not use this check as
routine `LIVE_AUTO` Session confirmation.

Set the WebAuthn server domain explicitly. Require HTTPS and HSTS outside local
development. Persist ASP.NET Core data-protection keys outside the container,
and encrypt them at rest with an X.509 certificate.

Give Operator authority and administrative access separate permissions. An
Administrator cannot activate a Goal unless that identity also has explicit
Operator authority.

Every execution view must show the exact mode, `PAPER` or `LIVE` text, masked
account, Goal version, activation, Session, kill switch, and reconciliation
state. Do not combine paper and live actions on one screen.

## Safety impact

Passkeys provide phishing-resistant Operator authentication. Fresh assertions
bind high-risk actions to an authenticated Operator without weakening
`LIVE_AUTO` activation semantics. Separate interfaces reduce environment
confusion.

## Rejected options

- Password-only authentication was rejected. It does not meet the selected
  strong-authentication baseline for live authority.
- A desktop interface was rejected. It adds another application platform and
  complicates remote operations.
- A command-line-only interface was rejected. It cannot present the complete
  authority and reconciliation context as clearly.
- Public identity federation was deferred. The initial system has one local
  Operator and no public access requirement.

## Migration constraints

Operator identifiers and action evidence must remain provider-neutral. A later
identity provider must preserve strong reauthentication and immutable action
evidence. Authentication migration cannot grant trading authority.

## References

- [Passkeys in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys/?view=aspnetcore-10.0)
- [ASP.NET Core data-protection key storage](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0)
