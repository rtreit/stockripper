---
name: schema-contracts
description: Apply schema-contract discipline to StockRipper data models, provider DTOs, and ledger records so contracts stay stable and auditable.
---

# Schema Contracts Skill

## When to Use

- Adding or changing Pydantic models, provider DTOs, or ledger schema shapes
- Reviewing a provider or workflow contract for backward compatibility
- Refactoring data flow between agents, judge, risk gate, and ledger

## Key Rules

1. Keep provider DTOs separate from internal StockRipper domain models.
2. Preserve source metadata and timestamps in every normalized record.
3. Prefer explicit typed fields over loosely structured dictionaries; model multi-instrument actions (equities, options multi-leg, shorts, leveraged ETFs) with explicit discriminated unions, not free-text fields.
4. Carry `track_id` through every record where decisions, orders, fills, or snapshots are per-track so leaderboard and reconciliation queries stay simple.
5. Update tests and replay coverage whenever a contract changes.
6. If a transport contract is added later, keep it explicit, versioned, and easy to validate.
