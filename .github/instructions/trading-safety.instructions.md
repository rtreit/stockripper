---
applyTo: "**/*"
---

# Trading safety

Before a change can affect execution, identify its applicable execution modes.

- Preserve immutable goal versions and exact execution bindings.
- Keep paper and live endpoints, accounts, credentials, and state separate.
- Fail closed on missing or inconsistent authorization data.
- Preserve durable idempotency, reconciliation, audit, and restart behavior.
- Never let an automated test place a live order.
- Treat a plausible unintended live order as the highest-severity defect.

Use the root `AGENTS.md` for the complete safety contract.
