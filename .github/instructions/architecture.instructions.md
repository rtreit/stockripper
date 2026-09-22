---
applyTo: "src/**,tests/**,docs/architecture/**"
---

# Architecture

- Keep goal, authorization, decision, execution, broker, reconciliation, risk,
  and audit boundaries explicit.
- Keep broker-specific behavior at the execution edge.
- Use explicit state machines for lifecycle behavior.
- Persist enough state before an external action to recover safely.
- Make external actions idempotent.
- Keep research code unable to bypass execution authorization.
- Record a decision before introducing a foundational technology or dependency.
