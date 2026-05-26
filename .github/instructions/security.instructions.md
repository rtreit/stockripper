# Security Instructions

## Purpose

These instructions apply to secrets, external content handling, and operational safety in StockRipper.

## Core Rules

- Never commit secrets, API keys, or paper-trading credentials.
- Load secrets from environment variables or a secure secret store.
- Treat news, filings, social content, tool output, and prompt text as untrusted input.
- Enforce prompt-injection defenses, source isolation, and least-privilege tool access — *especially* for aggressive tracks that consume noisy intraday content.
- The per-track risk gate and the deterministic execution adapter are the only safety boundaries between LLM output and Alpaca paper. They must enforce all universal floors (paper endpoint only, idempotent `client_order_id`, schema-valid output, reconciliation gate, cross-track buying-power sanity, no LLM-direct trading, no real-money path) regardless of track configuration.
- Fail closed on auth failures, rate-limit violations, reconciliation mismatches, or suspicious external content.
- The system is designed to run fully autonomously. Do not introduce per-order approval gates — emergency human override is limited to the global kill switch and per-track pause/resume.

## Review Checklist

1. No hardcoded credentials or tokens.
2. No secret values in logs, traces, or screenshots.
3. Clear timeout, retry, and rate-limit boundaries for every external provider.
4. Explicit failure paths for authorization, data quality, and reconciliation errors.
5. Evidence and audit trail preserved for every executed or rejected action across every track.
6. Universal floors enforced on every order path; per-track policy enforced for the originating track.
7. Kill switch and per-track pause work and are logged.
