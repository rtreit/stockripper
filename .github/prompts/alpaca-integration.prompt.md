---
name: alpaca-integration
description: Implement or update StockRipper provider integration behavior with safe auth handling, rate limits, and reconciliation.
---

# Provider Integration Prompt

Implement the provider integration with these constraints:

1. Keep provider logic in a dedicated adapter boundary; no LLM may call the provider directly.
2. Use secure credential loading and avoid hardcoded secrets.
3. Add explicit timeouts, retries, and shared (not per-track) rate limiting so aggressive tracks cannot starve conservative ones.
4. Normalize responses into StockRipper-owned models and preserve source / status / retrieval-timestamp metadata.
5. For Alpaca specifically: paper endpoint only; deterministic per-track `client_order_id`; support equities, options multi-leg, shorts, and leveraged ETFs as the paper account allows; reconcile per-track ledger state after every batch and at end of day.
6. Add tests that use mocked responses and verify deterministic per-track risk-gate and workflow behavior, including the universal-floor enforcement.
