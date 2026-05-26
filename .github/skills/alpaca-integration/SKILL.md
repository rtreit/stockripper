---
name: alpaca-integration
description: Apply StockRipper's provider-adapter patterns to Alpaca paper integration across equities, options, shorts, leveraged ETFs, and shared market-data work.
---

# Alpaca and Market Data Integration Skill

## When to Use

- Implementing or reviewing Alpaca paper account, positions, orders (including multi-leg options and shorts), streaming, or portfolio-history code
- Wiring market-data, options-chain, short-interest, or news providers into the multi-track workflow
- Fixing reconciliation, rate-limiting, or provider-normalization bugs

## Key Rules

1. Keep provider access in a dedicated adapter layer; no LLM or judge may call providers directly.
2. The Alpaca client must refuse to instantiate against any non-paper endpoint.
3. Normalize provider output immediately into StockRipper-owned models before any workflow consumes it.
4. Apply timeouts, retries, and **shared (not per-track)** rate limits so aggressive tracks cannot starve conservative ones.
5. Preserve provider status, request IDs, retrieval timestamps, and reconciliation metadata for debugging.
6. Generate deterministic per-track `client_order_id` values so cross-track duplicates are impossible and idempotency holds.
7. Reconcile every per-track ledger view against the underlying Alpaca account after every batch and at end of day.
