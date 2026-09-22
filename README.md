# Stockripper

Stockripper is a goal-driven market research and trade execution system.

The repository contains a local simulated-paper MVP. It supports `REPLAY`,
`RESEARCH`, `PAPER_REVIEW`, and `PAPER_AUTO`.

The paper MVP has no network Broker client. It cannot reach Alpaca or a live
account. The simulated Broker uses explicit `PAPER` authority only.

Read the [functional specification](./docs/functional-specification.md) for
the approved behavior. Read the [paper MVP guide](./docs/implementation/phase-1-paper-mvp.md)
for setup, operation, safety limits, and validation evidence.

The [architecture baseline](./docs/architecture/README.md) records accepted
technology decisions. The [safety review](./docs/architecture/safety-review.md)
records the execution safety assessment.

The repository includes shared guidance for Codex, GitHub Copilot, and Claude
Code. Read [AI coding environment](./docs/ai-coding-environment.md) for the
layout and maintenance rules.

## Quick start

Install .NET 10 and Docker Desktop. Start local PostgreSQL and configure local
secrets:

```powershell
./eng/start-local.ps1
```

Start the Operator host:

```powershell
dotnet run --project src/Stockripper.Host --urls http://127.0.0.1:5080
```

Open `http://127.0.0.1:5080`. The host applies migrations and verifies Audit
integrity before it becomes ready.

Stop local PostgreSQL without deleting its volume:

```powershell
./eng/stop-local.ps1
```

## Quality checks

Run the repository build:

```powershell
./build.ps1
```

Run all automated tests:

```powershell
./test.ps1
```

The test command uses isolated PostgreSQL 18 databases in Docker.

Historical implementation records remain in `docs/implementation/`. Their
boundary statements describe the named slice at its recorded date.
