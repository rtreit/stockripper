---
name: debug-investigation
description: Investigate a StockRipper bug using a root-cause-first workflow that checks workflow state, external providers, and risk gates.
---

# Debug Investigation Prompt

Investigate the issue using this workflow:

1. Reproduce the bug and capture expected vs. actual behavior, including which strategy track(s) it affects.
2. Check scheduler state, provider output, agent recommendations, per-track judge decisions, per-track risk-gate results, universal-floor outcomes, execution-adapter behavior, and ledger entries.
3. Identify whether the problem is a workflow bug, a provider contract issue, a prompt-injection problem, a per-track configuration error, or a data-quality issue.
4. Apply the smallest correct fix that preserves determinism, auditability, and full autonomy.
5. Add regression coverage and summarize the root cause and validation.
