# Data Contract Instructions

## Purpose

These instructions govern schema, API, and data-contract work in StockRipper.

## Source of Truth

- `PROJECT_SPEC.md` is the source of truth for workflow behavior, model shape, risk policy, and audit requirements.
- Pydantic models are the default source of truth for shared request, response, recommendation, risk, and ledger schemas.
- Keep provider-specific DTOs separate from internal StockRipper models.

## Contract Rules

- Validate every external payload at the boundary before it enters the workflow.
- Keep field names, types, and required metadata explicit and versioned.
- Preserve source metadata for claims, evidence, and provider data.
- Do not let raw provider responses leak into agent or judge logic.
- Keep ledger records stable enough to replay and reconcile over time.
- If a transport contract is added later, keep it explicit, versioned, and easy to validate.

## Schema Design Guidance

- Prefer explicit, typed models over loosely structured dictionaries.
- Model confidence, timestamps, source IDs, and data-quality warnings in the schema, not as ad hoc string fields.
- Keep risk decisions, order attempts, and reconciliation events structured and queryable.
- When a schema must change, update the implementation and tests together.

## Validation Expectations

- Add tests for schema validation, provider normalization, and replay behavior.
- Verify that changed contracts still support deterministic risk calculations and audit trails.
- Use `uv`-managed Python tooling for formatting, linting, type checks, and tests.
