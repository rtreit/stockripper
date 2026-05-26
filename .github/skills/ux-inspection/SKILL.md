---
name: ux-inspection
description: Inspect StockRipper dashboards, terminal output, and evidence surfaces to verify clarity, traceability, and workflow safety.
---

# UX Inspection Skill

## When to Use

- Reviewing dashboard layouts, reports, or CLI output for the multi-track system
- Verifying that evidence, per-track risk status, and order state are visible
- Diagnosing confusing run summaries, leaderboard views, or emergency-control surfaces

## Key Rules

1. Every claim rendered in a dashboard or report should be traceable to a source, timestamp, and confidence value.
2. Per-track risk status, latest judge action, and reconciliation state must be obvious at a glance.
3. Do not treat a visually clean screen as sufficient if the evidence trail is hidden or ambiguous.
4. Verify the UI exposes no per-order approval pattern — only observation plus the global kill switch and per-track pause/resume.
5. Capture before/after artifacts when fixing layout, density, or readability issues.

## Suggested Workflow

- Inspect the current surface with the same scenario and data set.
- Check evidence visibility, chart readability, leaderboard prominence, and emergency-control discoverability.
- Patch the smallest UI change that improves comprehension without hiding important state.
- Re-capture and verify the same scenario.
