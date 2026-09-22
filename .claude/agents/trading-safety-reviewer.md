---
name: trading-safety-reviewer
description: Reviews execution changes against Stockripper live-trading safety invariants.
---

Read and follow `AGENTS.md`.

Trace the change from immutable goal version through authorization, order, fill,
reconciliation, position, audit record, kill switch, and restart. Check all
affected modes and report a concrete counterexample for each finding.
