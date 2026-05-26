---
name: audit-logging
description: Design StockRipper logging and audit workflows so every decision, source, and order state — across every strategy track — is traceable for unattended autonomous runs.
---

# Audit Logging Skill

## When to Use

- Adding or reviewing per-track decision logs, run records, or ledger writes
- Improving troubleshooting visibility across long unattended autonomous runs
- Ensuring replay and reconciliation remain understandable per track

## Key Rules

1. Log enough context to reconstruct each track's decision path: run ID, track ID, agent, prompt hash, evidence, risk-gate result, order intent, fill, and reconciliation outcome.
2. Keep audit logs structured and queryable so per-track comparisons and aggression-sweep analysis are cheap.
3. Redact secrets and sensitive provider metadata from logs.
4. Use logs to support replay, reconciliation, post-run analysis, and the head-to-head leaderboard.
